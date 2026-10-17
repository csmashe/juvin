using System.Net;
using System.Net.Sockets;
using Juvin.App.Services.CrashReporting;
using Sentry.Protocol;

namespace Juvin.App.Tests.CrashReporting;

public sealed class CrashReportScrubberTests
{
    [Fact]
    public void Scrub_KeepsTypesAndFramesAndDropsEverythingElse()
    {
        var exception = new InvalidOperationException("draft: meet at noon", new IOException("inner"));
        var e = new SentryEvent(exception)
        {
            Message = "draft: meet at noon",
            ServerName = "my-laptop",
            Logger = "draft: meet at noon",
            TransactionName = "draft: meet at noon",
            Fingerprint = ["draft: meet at noon"],
            Distribution = "my-laptop",
            User = new SentryUser { Id = "install-id", Username = "chris" },
            Request = new SentryRequest { Url = "https://mail.example/" },
        };
        e.SetExtra("draft", "meet at noon");
        e.SetTag("operation", "ui-dispatcher");
        e.SetTag("culture", "en-GB");
        e.Contexts.Device.Name = "my-laptop";
        e.Contexts.App.Hash = "hash";
        e.Contexts.App.Identifier = "identifier";
        e.Contexts.OperatingSystem.Name = "Linux";
        var mechanism = new Mechanism
        {
            Type = "Exception.Data[\"Sentry:Mechanism\"] from a caller",
            Description = "meet at noon",
            HelpLink = "https://mail.example/draft?id=noon",
            Handled = false,
            Data = { ["secret"] = "meet at noon" },
        };
        e.SentryExceptions =
        [
            new SentryException
            {
                Type = "System.InvalidOperationException",
                Value = "draft: meet at noon",
                Mechanism = mechanism,
                Stacktrace = new SentryStackTrace
                {
                    Frames =
                    [
                        new SentryStackFrame
                        {
                            Function = "Run",
                            Module = "Juvin.App.Services.RewritePanelService",
                            FileName = "/home/chris/Juvin/RewritePanelService.cs",
                            AbsolutePath = "/home/chris/Juvin/RewritePanelService.cs",
                        },
                    ],
                },
            },
        ];
        e.DebugImages = [new DebugImage { CodeFile = "/home/chris/juvin.dll", DebugFile = "/home/chris/juvin.pdb" }];

        var scrubbed = CrashReportScrubber.Scrub(e)!;

        Assert.Null(scrubbed.Message);
        Assert.Null(scrubbed.ServerName);
        Assert.Null(scrubbed.Logger);
        Assert.Null(scrubbed.TransactionName);
        Assert.Empty(scrubbed.Fingerprint);
        Assert.Null(scrubbed.Distribution);
        Assert.Null(scrubbed.User.Id);
        Assert.Null(scrubbed.User.Username);
        Assert.Null(scrubbed.Request.Url);
        Assert.Empty(scrubbed.Extra);
        Assert.False(scrubbed.Contexts.ContainsKey(Device.Type));
        Assert.Equal("Linux", scrubbed.Contexts.OperatingSystem.Name);
        Assert.Null(scrubbed.Contexts.App.Hash);
        Assert.Null(scrubbed.Contexts.App.Identifier);
        Assert.Equal("ui-dispatcher", scrubbed.Tags["operation"]);
        Assert.Equal("System.IO.IOException", scrubbed.Tags["error.inner_type"]);
        Assert.True(scrubbed.Tags.ContainsKey("error.hresult"));
        Assert.False(scrubbed.Tags.ContainsKey("culture"));
        var sentryException = Assert.Single(scrubbed.SentryExceptions!);
        Assert.Equal("System.InvalidOperationException", sentryException.Type);
        Assert.Equal(CrashReportScrubber.Redacted, sentryException.Value);
        Assert.Empty(mechanism.Data);
        Assert.Null(mechanism.Description);
        Assert.Null(mechanism.HelpLink);
        Assert.Equal("generic", mechanism.Type);
        Assert.False(mechanism.Handled);
        var frame = Assert.Single(sentryException.Stacktrace!.Frames);
        Assert.Equal("Run", frame.Function);
        Assert.Equal("Juvin.App.Services.RewritePanelService", frame.Module);
        Assert.Null(frame.FileName);
        Assert.Null(frame.AbsolutePath);
        var image = Assert.Single(scrubbed.DebugImages!);
        Assert.Null(image.CodeFile);
        Assert.Null(image.DebugFile);
    }

    [Fact]
    public void Scrub_RebuildsEachKeptContextFromApprovedFieldsOnly()
    {
        var startTime = DateTimeOffset.UtcNow;
        var e = new SentryEvent(new InvalidOperationException())
        {
            Contexts =
            {
                ["app"] = new Sentry.Protocol.App { StartTime = startTime, BuildType = "release", Name = "draft: meet at noon", Hash = "hash" },
                ["os"] = new Sentry.Protocol.OperatingSystem { RawDescription = "Fedora Linux 44" },
                ["runtime"] = new Dictionary<string, string> { ["draft"] = "meet at noon" },
                [".NET"] = new Runtime { Name = ".NET", Version = "10.0.10", Identifier = "draft: meet at noon" },
                ["Dynamic Code"] = new Dictionary<string, bool> { ["Compiled"] = true, ["draft: meet at noon"] = true },
            },
        };

        var contexts = CrashReportScrubber.Scrub(e)!.Contexts;

        var app = Assert.IsType<Sentry.Protocol.App>(contexts["app"]);
        Assert.Equal(startTime, app.StartTime);
        Assert.Equal("release", app.BuildType);
        Assert.Null(app.Name);
        Assert.Null(app.Hash);
        Assert.Equal("Fedora Linux 44", Assert.IsType<Sentry.Protocol.OperatingSystem>(contexts["os"]).RawDescription);
        Assert.False(contexts.ContainsKey("runtime"));
        var dotNet = Assert.IsType<Runtime>(contexts[".NET"]);
        Assert.Equal("10.0.10", dotNet.Version);
        Assert.Null(dotNet.Identifier);
        Assert.Equal(["Compiled"], Assert.IsType<Dictionary<string, bool>>(contexts["Dynamic Code"]).Keys);
    }

    [Fact]
    public void Scrub_TagsTheHttpStatus()
    {
        var e = new SentryEvent(new HttpRequestException("draft: meet at noon", null, HttpStatusCode.BadGateway));

        var scrubbed = CrashReportScrubber.Scrub(e)!;

        Assert.Equal("502", scrubbed.Tags[CrashReportErrorFacts.HttpStatusTag]);
    }

    [Fact]
    public void Scrub_TagsTheSocketError()
    {
        var e = new SentryEvent(new HttpRequestException("draft: meet at noon", new SocketException((int)SocketError.ConnectionRefused)));

        var scrubbed = CrashReportScrubber.Scrub(e)!;

        Assert.Equal(nameof(SocketError.ConnectionRefused), scrubbed.Tags[CrashReportErrorFacts.SocketErrorTag]);
        Assert.Equal(typeof(SocketException).FullName, scrubbed.Tags[CrashReportErrorFacts.InnerTypeTag]);
    }
}
