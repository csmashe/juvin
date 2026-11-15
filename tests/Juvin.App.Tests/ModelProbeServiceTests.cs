using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.Core.Localization;
using Juvin.Core.Providers;

namespace Juvin.App.Tests;

public sealed class ModelProbeServiceTests
{
    private readonly ModelProbeService _service = new FakeProviders().CreateService(new Localizer());

    [Theory]
    [InlineData("http://localhost:11434", "This device")]
    [InlineData("http://127.0.0.1:1234/v1", "This device")]
    [InlineData("http://[::1]:8080", "This device")]
    [InlineData("http://10.0.3.20:11434", "Private server")]
    [InlineData("https://models.example.com", "Private server")]
    [InlineData("not a url", "")]
    [InlineData("ftp://localhost", "")]
    public void LocationLabel_NamesLoopbackAsThisDevice(string url, string expected)
    {
        Assert.Equal(expected, _service.LocationLabel(url));
    }

    [Theory]
    [InlineData("http://10.0.3.20:11434", true, "Provider.Status.Unreachable", true)]
    [InlineData("http://nas:11434", true, "Provider.Status.Unreachable", true)]
    [InlineData("http://localhost:11434", true, "Provider.Status.Unreachable", false)]
    [InlineData("http://127.0.0.1:1234/v1", true, "Provider.Status.Unreachable", false)]
    [InlineData("http://10.0.3.20:11434", false, "Provider.Status.Unreachable", false)]
    [InlineData("http://10.0.3.20:11434", true, "Provider.Status.BadResponse", false)]
    [InlineData("http://10.0.3.20:11434", true, "Provider.Status.NoModel", false)]
    [InlineData("not a url", true, "Provider.Status.Unreachable", false)]
    public void MayNeedLocalNetworkPermission_OnlyForAnUnreachableLanServerOnMac(string url, bool isMac, string messageKey, bool expected)
    {
        var status = new ProviderStatus(ProviderStatusKind.Unreachable, messageKey, []);

        Assert.Equal(expected, ModelProbeService.MayNeedLocalNetworkPermission(status, url, isMac));
    }

    [Fact]
    public void DescribeLocal_OnMac_TellsHowToAllowTheLocalNetwork()
    {
        var localizer = new Localizer();
        var mac = new FakeProviders { IsMac = true }.CreateService(localizer);
        var unreachable = new ProviderStatus(ProviderStatusKind.Unreachable, "Provider.Status.Unreachable", []);

        Assert.Equal(localizer["Models.Status.UnreachableLocalNetworkMac"], mac.DescribeLocal(unreachable, "http://10.0.3.20:11434"));
        Assert.Equal(localizer["Provider.Status.Unreachable"], mac.DescribeLocal(unreachable, "http://localhost:11434"));
        Assert.Equal(localizer["Provider.Status.Unreachable"], _service.DescribeLocal(unreachable, "http://10.0.3.20:11434"));
        Assert.Contains("Ollama 0.12.3", mac.DescribeLocal(FakeProviders.Reachable("0.12.3"), "http://10.0.3.20:11434"));
    }

    [Fact]
    public void RecommendModel_PrefersTheFirstModelThatIsNotLowQuality()
    {
        var pick = ModelProbeService.RecommendModel(
            [
                FakeProviders.Model("small", "4B", lowQuality: true),
                FakeProviders.Model("large", "27B"),
                FakeProviders.Model("other", "26B"),
            ]
        );

        Assert.Equal("large", pick?.Id);
    }

    [Fact]
    public void RecommendModel_FallsBackToTheFirstModel()
    {
        var pick = ModelProbeService.RecommendModel(
            [FakeProviders.Model("a", "4B", lowQuality: true), FakeProviders.Model("b", "8B", lowQuality: true)]
        );

        Assert.Equal("a", pick?.Id);
    }

    [Fact]
    public void RecommendModel_SkipsCloudAndEmbeddingModels()
    {
        var models = new[]
        {
            FakeProviders.Model("cloud", "120B") with { IsRemote = true },
            FakeProviders.Model("embed", "137M", lowQuality: true) with { SupportsChat = false },
            FakeProviders.Model("small", "4B", lowQuality: true),
        };

        Assert.Equal("small", ModelProbeService.RecommendModel(models)?.Id);
        Assert.Equal(["small"], ModelProbeService.UsableModels(models).Select(m => m.Id));
    }

    [Fact]
    public void RecommendModel_IsNullWithoutModels()
    {
        Assert.Null(ModelProbeService.RecommendModel([]));
    }

    [Fact]
    public void CodexInstallCommand_IsTheStandaloneInstallerForTheOs()
    {
        Assert.Equal("curl -fsSL https://chatgpt.com/codex/install.sh | sh", ModelProbeService.CodexInstallCommandFor(isWindows: false));
        Assert.Equal(
            "powershell -ExecutionPolicy ByPass -c \"irm https://chatgpt.com/codex/install.ps1 | iex\"",
            ModelProbeService.CodexInstallCommandFor(isWindows: true)
        );
    }
}
