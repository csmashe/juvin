using System.Text.Json;
using Juvin.App.Services;
using Juvin.Platform;

namespace Juvin.App.Tests;

public sealed class ControlCommandHandlerTests
{
    [Theory]
    [InlineData("""{"v":1,"cmd":"show"}""", ControlCommand.Show)]
    [InlineData("""{"cmd":"ping","v":1}""", ControlCommand.Ping)]
    [InlineData("""{"v":1,"cmd":"ping","extra":true}""", ControlCommand.Ping)]
    [InlineData("""{"v":1,"cmd":"trigger"}""", ControlCommand.Trigger)]
    public void Parse_AcceptsKnownCommands(string line, ControlCommand expected)
    {
        var (command, error) = ControlCommandHandler.Parse(line);

        Assert.Equal(expected, command);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not json", LocalSocketProtocol.ErrBadRequest)]
    [InlineData("[1,2]", LocalSocketProtocol.ErrBadRequest)]
    [InlineData("""{"cmd":"show"}""", LocalSocketProtocol.ErrBadRequest)]
    [InlineData("""{"v":"1","cmd":"show"}""", LocalSocketProtocol.ErrBadRequest)]
    [InlineData("""{"v":1,"cmd":5}""", LocalSocketProtocol.ErrBadRequest)]
    [InlineData("""{"v":2,"cmd":"show"}""", LocalSocketProtocol.ErrUnsupportedVersion)]
    [InlineData("""{"v":1.5,"cmd":"show"}""", LocalSocketProtocol.ErrUnsupportedVersion)]
    [InlineData("""{"v":1,"cmd":"SHOW"}""", LocalSocketProtocol.ErrUnknownCommand)]
    [InlineData("""{"v":1,"cmd":"rewrite"}""", LocalSocketProtocol.ErrUnknownCommand)]
    public void Parse_RejectsInvalidRequests(string line, string expectedError)
    {
        var (command, error) = ControlCommandHandler.Parse(line);

        Assert.Null(command);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public async Task Show_RaisesShowRequestedAndRepliesOk()
    {
        var handler = new ControlCommandHandler();
        var shown = 0;
        handler.ShowRequested += () => shown++;

        var reply = await handler.HandleAsync(ControlCommandHandler.ShowRequestLine, CancellationToken.None);

        Assert.Equal(1, shown);
        Assert.Equal("""{"v":1,"ok":true}""", reply);
    }

    [Fact]
    public async Task Trigger_RaisesTriggerRequestedAndRepliesOk()
    {
        var handler = new ControlCommandHandler();
        var triggered = 0;
        var shown = 0;
        handler.TriggerRequested += () => triggered++;
        handler.ShowRequested += () => shown++;

        var reply = await handler.HandleAsync(ControlCommandHandler.TriggerRequestLine, CancellationToken.None);

        Assert.Equal(1, triggered);
        Assert.Equal(0, shown);
        Assert.Equal("""{"v":1,"ok":true}""", reply);
    }

    [Fact]
    public async Task Ping_RepliesOkWithoutShowing()
    {
        var handler = new ControlCommandHandler();
        var shown = 0;
        handler.ShowRequested += () => shown++;

        var reply = await handler.HandleAsync("""{"v":1,"cmd":"ping"}""", CancellationToken.None);

        Assert.Equal(0, shown);
        Assert.Equal("""{"v":1,"ok":true}""", reply);
    }

    [Fact]
    public async Task UnknownCommand_RepliesWithError()
    {
        var reply = await new ControlCommandHandler().HandleAsync("""{"v":1,"cmd":"nope"}""", CancellationToken.None);

        using var document = JsonDocument.Parse(reply);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(LocalSocketProtocol.ErrUnknownCommand, document.RootElement.GetProperty("error").GetString());
    }
}
