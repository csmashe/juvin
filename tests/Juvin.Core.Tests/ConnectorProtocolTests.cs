using System.Text.Json.Nodes;
using Juvin.Core.Connector;
using Juvin.Core.Rewrite;

namespace Juvin.Core.Tests;

public sealed class ConnectorProtocolTests
{
    private const string CaptureResponse = """
        {"v":1,"type":"context.response","id":"c1","ok":true,
         "capture":{
           "adapter":"gmail",
           "pageUrl":"https://mail.google.com/mail/u/0/",
           "capturedAt":"2026-10-09T14:03:21.512Z",
           "subject":"Invoice for September",
           "editor":{"id":"e-7f3a","hash":"sha256:9f86d0..."},
           "selection":{"text":"Hi team, ...","start":0,"end":118},
           "thread":{
             "completeness":"entire-thread",
             "collapsedCount":0,
             "messages":[{"from":"Ana <ana@example.com>","to":["me@example.com"],"sent":"2026-10-08T09:12:00Z","body":"..."}]
           }}}
        """;

    // Every example in docs/connector-protocol.md, verbatim.
    public static TheoryData<string, Type> SpecExamples => new()
    {
        {
            """{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}""",
            typeof(HelloMessage)
        },
        {
            """
            {"v":1,"type":"config",
             "actions":[{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"}],
             "adapters":["gmail","generic","test-page"],
             "shortcut":"Ctrl+Shift+Y",
             "health":{"mode":"local","modelReachable":null}}
            """,
            typeof(ConnectorConfig)
        },
        { """{"v":1,"type":"ping","id":"p1"}""", typeof(PingMessage) },
        { """{"v":1,"type":"pong","id":"p1"}""", typeof(PongMessage) },
        { """{"v":1,"type":"shortcut.pressed"}""", typeof(ShortcutPressedMessage) },
        { """{"v":1,"type":"context.request","id":"c1"}""", typeof(ContextRequestMessage) },
        { CaptureResponse, typeof(ContextResponseMessage) },
        {
            """{"v":1,"type":"context.response","id":"c1","ok":false,"error":{"code":"no-selection","message":"Nothing is selected in the editor."}}""",
            typeof(ContextResponseMessage)
        },
        {
            """{"v":1,"type":"apply.request","id":"a1","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi team, thank you for the update..."}""",
            typeof(ApplyRequestMessage)
        },
        { """{"v":1,"type":"apply.response","id":"a1","ok":true}""", typeof(ApplyResponseMessage) },
        {
            """{"v":1,"type":"apply.response","id":"a1","ok":false,"error":{"code":"draft-changed","message":"The draft changed since it was captured."}}""",
            typeof(ApplyResponseMessage)
        },
        {
            """{"v":1,"type":"error","id":"c1","error":{"code":"unknown-type","message":"Unknown message type."}}""",
            typeof(ErrorMessage)
        },
    };

    [Theory]
    [MemberData(nameof(SpecExamples))]
    public void SpecExamples_ParseAndReserializeToEquivalentJson(string json, Type expectedType)
    {
        var (message, error) = ConnectorProtocol.Parse(json);

        Assert.Null(error);
        Assert.IsType(expectedType, message);
        var reserialized = ConnectorProtocol.Serialize(message);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(reserialized)),
            $"Expected {json}\nbut got {reserialized}"
        );
    }

    [Fact]
    public void Serialize_PutsTheEnvelopeFirstAndOmitsAMissingId()
    {
        Assert.Equal("""{"v":1,"type":"shortcut.pressed"}""", ConnectorProtocol.Serialize(new ShortcutPressedMessage()));
        Assert.Equal("""{"v":1,"type":"ping","id":"p9"}""", ConnectorProtocol.Serialize(new PingMessage { Id = "p9" }));
    }

    [Theory]
    [InlineData("not json", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("[1]", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"type":"ping","id":"p1"}""", ConnectorErrorCodes.BadMessage, "p1")]
    [InlineData("""{"v":"1","type":"ping","id":"p1"}""", ConnectorErrorCodes.BadMessage, "p1")]
    [InlineData("""{"v":2,"type":"ping","id":"p1"}""", ConnectorErrorCodes.UnsupportedVersion, "p1")]
    [InlineData("""{"v":1.5,"type":"ping"}""", ConnectorErrorCodes.UnsupportedVersion, null)]
    [InlineData("""{"v":1,"id":"x"}""", ConnectorErrorCodes.BadMessage, "x")]
    [InlineData("""{"v":1,"type":7}""", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"v":1,"type":"rewrite","id":"r1"}""", ConnectorErrorCodes.UnknownType, "r1")]
    [InlineData("""{"v":1,"type":"ping"}""", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"v":1,"type":"hello","extension":{"id":"x"}}""", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"v":1,"type":"context.response","id":"c1","ok":true}""", ConnectorErrorCodes.BadMessage, "c1")]
    [InlineData("""{"v":1,"type":"context.response","id":"c1","ok":false}""", ConnectorErrorCodes.BadMessage, "c1")]
    [InlineData("""{"v":1,"type":"ping","id":"\ud800"}""", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"v":1,"type":"pi\ud800ng","id":"p1"}""", ConnectorErrorCodes.BadMessage, null)]
    [InlineData("""{"v":1,"type":"apply.request","id":"a1","editorId":"e","expectedHash":"h","start":0,"end":1,"text":null}""", ConnectorErrorCodes.BadMessage, "a1")]
    [InlineData("""{"v":1,"type":"apply.request","id":"a1","editorId":"e","expectedHash":"h","text":"t"}""", ConnectorErrorCodes.BadMessage, "a1")]
    [InlineData("""{"v":1,"type":"apply.request","id":"a1","editorId":"e","expectedHash":"h","start":3,"end":1,"text":"t"}""", ConnectorErrorCodes.BadMessage, "a1")]
    public void Parse_RejectsBadMessagesWithTheSpecCode(string json, string code, string? id)
    {
        var (message, error) = ConnectorProtocol.Parse(json);

        Assert.Null(message);
        Assert.NotNull(error);
        Assert.Equal(code, error.Error.Code);
        Assert.Equal(id, error.Id);
    }

    [Fact]
    public void Parse_IgnoresUnknownProperties()
    {
        var (message, error) = ConnectorProtocol.Parse("""{"v":1,"type":"ping","id":"p1","extra":{"a":1}}""");

        Assert.Null(error);
        Assert.Equal("p1", Assert.IsType<PingMessage>(message).Id);
    }

    [Fact]
    public void Parse_RejectsANullThreadMessage()
    {
        var json = CaptureResponse.Replace("\"messages\":[", "\"messages\":[null,");

        Assert.Equal(ConnectorErrorCodes.BadMessage, ConnectorProtocol.Parse(json).Error?.Error.Code);
    }

    [Fact]
    public void Parse_RejectsAnUnknownCompleteness()
    {
        var json = CaptureResponse.Replace("entire-thread", "everything");

        Assert.Equal(ConnectorErrorCodes.BadMessage, ConnectorProtocol.Parse(json).Error?.Error.Code);
    }

    [Theory]
    [InlineData("entire-thread", ThreadCompleteness.EntireThread)]
    [InlineData("loaded-messages-only", ThreadCompleteness.LoadedMessagesOnly)]
    [InlineData("visible-text-only", ThreadCompleteness.VisibleTextOnly)]
    [InlineData("draft-only", ThreadCompleteness.DraftOnly)]
    [InlineData("truncated", ThreadCompleteness.TruncatedForModelLimit)]
    public void Completeness_MapsEveryWireValue(string wire, ThreadCompleteness expected)
    {
        Assert.True(ConnectorProtocol.TryParseCompleteness(wire, out var completeness));
        Assert.Equal(expected, completeness);
    }

    [Fact]
    public void CapturedContext_MapsTheCaptureIntoRewriteInput()
    {
        var json = CaptureResponse
            .Replace("entire-thread", "truncated")
            .Replace("\"collapsedCount\":0", "\"collapsedCount\":2")
            .Replace("\"messages\":[", "\"messages\":[{\"from\":\"Bo\",\"body\":\"first\",\"sent\":\"\"},");
        var response = Assert.IsType<ContextResponseMessage>(ConnectorProtocol.Parse(json).Message);

        var captured = CapturedContext.From(response.Capture!);

        Assert.Equal("Hi team, ...", captured.Context.Draft);
        Assert.Equal("Invoice for September", captured.Context.Subject);
        Assert.Equal(ThreadCompleteness.TruncatedForModelLimit, captured.Context.Completeness);
        Assert.Equal(2, captured.Context.CollapsedCount);
        Assert.Equal(2, captured.Context.Thread.Count);
        Assert.Equal("Bo", captured.Context.Thread[0].From);
        Assert.Empty(captured.Context.Thread[0].To);
        Assert.Null(captured.Context.Thread[0].Sent);
        Assert.Equal(["me@example.com"], captured.Context.Thread[1].To);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 12, 0, TimeSpan.Zero), captured.Context.Thread[1].Sent);
        Assert.Equal("gmail", captured.Adapter);
        Assert.Equal("e-7f3a", captured.EditorId);
        Assert.Equal("sha256:9f86d0...", captured.EditorHash);
        Assert.Equal("https://mail.google.com/mail/u/0/", captured.PageUrl);
        Assert.Equal((0, 118), (captured.SelectionStart, captured.SelectionEnd));
    }
}
