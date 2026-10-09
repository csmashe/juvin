using Juvin.App.Services;
using Juvin.App.Tests.Fakes;
using Juvin.Core.Localization;

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
}
