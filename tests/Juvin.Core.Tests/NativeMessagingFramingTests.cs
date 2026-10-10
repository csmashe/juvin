using System.Buffers.Binary;
using Juvin.Core.Connector;

namespace Juvin.Core.Tests;

public sealed class NativeMessagingFramingTests
{
    [Fact]
    public async Task Frames_RoundTripInOrder()
    {
        var stream = new MemoryStream();
        await NativeMessagingFraming.WriteFrameAsync(stream, """{"v":1,"type":"ping","id":"p1"}""", CancellationToken.None);
        await NativeMessagingFraming.WriteFrameAsync(stream, """{"text":"café 日本"}""", CancellationToken.None);
        stream.Position = 0;

        Assert.Equal("""{"v":1,"type":"ping","id":"p1"}""", await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Equal("""{"text":"café 日本"}""", await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Null(await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Write_UsesALittleEndianByteLength()
    {
        var stream = new MemoryStream();

        await NativeMessagingFraming.WriteFrameAsync(stream, "\"é\"", CancellationToken.None);

        Assert.Equal(new byte[] { 4, 0, 0, 0, (byte)'"', 0xC3, 0xA9, (byte)'"' }, stream.ToArray());
    }

    [Fact]
    public async Task Read_AssemblesAFrameDeliveredOneByteAtATime()
    {
        var json = "{\"body\":\"" + new string('x', 5000) + "\"}";
        var buffer = new MemoryStream();
        await NativeMessagingFraming.WriteFrameAsync(buffer, json, CancellationToken.None);

        var frame = await NativeMessagingFraming.ReadFrameAsync(new TricklingStream(buffer.ToArray()), CancellationToken.None);

        Assert.Equal(json, frame);
    }

    [Fact]
    public async Task Read_RejectsALengthOverTheCap()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)NativeMessagingFraming.MaxFrameBytes + 1);

        var ex = await Assert.ThrowsAsync<ConnectorFrameException>(() =>
            NativeMessagingFraming.ReadFrameAsync(new MemoryStream(header), CancellationToken.None)
        );

        Assert.Equal(ConnectorFrameFailure.TooLarge, ex.Failure);
    }

    [Fact]
    public async Task Read_AcceptsAFrameExactlyAtTheCap()
    {
        var json = "\"" + new string('a', NativeMessagingFraming.MaxFrameBytes - 2) + "\"";
        var stream = new MemoryStream();
        await NativeMessagingFraming.WriteFrameAsync(stream, json, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(json, await NativeMessagingFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 5, 0 })]
    [InlineData(new byte[] { 5, 0, 0, 0, (byte)'{', (byte)'}' })]
    public async Task Read_RejectsATruncatedFrame(byte[] bytes)
    {
        var ex = await Assert.ThrowsAsync<ConnectorFrameException>(() =>
            NativeMessagingFraming.ReadFrameAsync(new MemoryStream(bytes), CancellationToken.None)
        );

        Assert.Equal(ConnectorFrameFailure.Truncated, ex.Failure);
    }

    [Fact]
    public async Task Write_RejectsJsonOverTheCap()
    {
        var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<ConnectorFrameException>(() =>
            NativeMessagingFraming.WriteFrameAsync(stream, new string('a', NativeMessagingFraming.MaxFrameBytes + 1), CancellationToken.None)
        );

        Assert.Equal(ConnectorFrameFailure.TooLarge, ex.Failure);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task StreamChannel_SendsAndReceivesFrames()
    {
        var stream = new MemoryStream();
        await using var channel = new StreamConnectorChannel(stream);

        await channel.SendAsync("{}", CancellationToken.None);
        stream.Position = 0;

        Assert.Equal("{}", await channel.ReceiveAsync(CancellationToken.None));
        Assert.Equal("{}"u8.Length + 4, stream.Length);
    }

    /// <summary>Returns at most one byte per read, like a slow pipe.</summary>
    private sealed class TricklingStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
