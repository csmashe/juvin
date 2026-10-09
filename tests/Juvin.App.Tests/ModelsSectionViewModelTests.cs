using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.Tests;

public sealed class ModelsSectionViewModelTests
{
    private readonly FakeProviders _providers = new();
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public void ChoosingCodex_SavesTheMode()
    {
        var vm = Create();

        vm.SelectedMode = vm.ModeOptions.Single(option => option.Mode == ProcessingMode.Codex);

        Assert.Equal(ProcessingMode.Codex, _settings.Current.ProcessingMode);
        Assert.True(vm.IsCodexMode);
        Assert.False(vm.IsLocalMode);
    }

    [Fact]
    public async Task CommittingAServerUrl_SavesItTrimmedAndListsItsModels()
    {
        const string url = "http://10.0.3.20:11434";
        _providers.Servers[url] = FakeProviders.Reachable(
            "0.12.3",
            FakeProviders.Model("qwen3.5:27b", "27B"),
            FakeProviders.Model("qwen3:8b", "8.2B", lowQuality: true)
        );
        var vm = Create();

        vm.CommitLocalServerUrlCommand.Execute($"  {url} ");
        await vm.CheckLocalCommand.ExecuteAsync(null);

        Assert.Equal(url, _settings.Current.LocalServerUrl);
        Assert.Equal("Private server", vm.LocalServerLocation);
        Assert.Equal(["qwen3.5:27b (27B)", "qwen3:8b (8.2B)"], vm.LocalModels.Select(option => option.Label));
        Assert.True(vm.LocalModels[1].IsLowQuality);
        Assert.Contains("Ollama 0.12.3", vm.LocalStatus);
    }

    [Fact]
    public void AnInvalidServerUrl_IsNotSaved()
    {
        var vm = Create();

        vm.LocalServerUrl = "not a url";

        Assert.Equal(new AppSettings().LocalServerUrl, _settings.Current.LocalServerUrl);
        Assert.Equal(0, _settings.SaveCount);
        Assert.NotNull(vm.LocalStatus);
    }

    [Fact]
    public async Task ChoosingAModel_SavesIt()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable(
            null,
            FakeProviders.Model("a"),
            FakeProviders.Model("b")
        );
        var vm = Create();
        await vm.CheckLocalCommand.ExecuteAsync(null);

        var before = vm.LocalStatus;
        vm.SelectedLocalModel = vm.LocalModels.Single(option => option.Id == "b");

        Assert.Equal("b", _settings.Current.LocalModel);
        Assert.Contains("Choose a model", before);
        Assert.DoesNotContain("Choose a model", vm.LocalStatus);
    }

    [Fact]
    public async Task ACheckThatFinishesAfterAnInvalidEdit_KeepsTheValidationMessage()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("a"));
        _providers.LocalProbeGate = new TaskCompletionSource();
        var vm = Create();

        var check = vm.CheckLocalCommand.ExecuteAsync(null);
        vm.LocalServerUrl = "not a url";
        _providers.LocalProbeGate.SetResult();
        await check;

        Assert.DoesNotContain("Ollama", vm.LocalStatus);
        Assert.Equal(["a"], vm.LocalModels.Select(option => option.Id));
    }

    [Fact]
    public async Task ACheckThatFinishesAfterTheAddressWasChangedElsewhere_DoesNotDescribeTheNewAddress()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("a"));
        _providers.LocalProbeGate = new TaskCompletionSource();
        var vm = Create();

        var check = vm.CheckLocalCommand.ExecuteAsync(null);
        _settings.Update(s => s with { LocalServerUrl = "http://10.0.3.20:11434" });
        _providers.LocalProbeGate.SetResult();
        await check;

        Assert.Null(vm.LocalStatus);
        Assert.Empty(vm.LocalModels);
    }

    [Fact]
    public async Task AStatus_FollowsAModelChosenElsewhere()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("a"));
        var vm = Create();
        await vm.CheckLocalCommand.ExecuteAsync(null);
        Assert.Contains("Choose a model", vm.LocalStatus);

        _settings.Update(s => s with { LocalModel = "a" });

        Assert.DoesNotContain("Choose a model", vm.LocalStatus);
        Assert.Equal("a", vm.SelectedLocalModel?.Id);
    }

    [Fact]
    public async Task AStatus_IsDroppedWhenTheSettingsItDescribesChangeElsewhere()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("a"));
        _providers.Codex = new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", [], "codex-cli 0.162.0");
        var vm = Create();
        await vm.CheckLocalCommand.ExecuteAsync(null);
        await vm.CheckCodexCommand.ExecuteAsync(null);
        Assert.NotNull(vm.LocalStatus);
        Assert.NotNull(vm.CodexStatus);

        _settings.Update(s => s with { LocalServerUrl = "http://10.0.3.20:11434", CodexBinaryPath = "/opt/codex" });

        Assert.Null(vm.LocalStatus);
        Assert.Null(vm.CodexStatus);
    }

    [Fact]
    public async Task CheckingWithAnInvalidAddressShown_DoesNotProbeTheSavedOne()
    {
        _providers.Servers[new AppSettings().LocalServerUrl] = FakeProviders.Reachable("0.12.3", FakeProviders.Model("a"));
        var vm = Create();

        vm.LocalServerUrl = "not a url";
        await vm.CheckLocalCommand.ExecuteAsync(null);

        Assert.Empty(vm.LocalModels);
        Assert.DoesNotContain("Ollama", vm.LocalStatus);
    }

    [Fact]
    public void BeforeAnyProbe_TheSavedModelIsTheOnlyItem()
    {
        _settings.Update(s => s with { LocalModel = "gemma4:26b" });

        var vm = Create();

        Assert.Equal(["gemma4:26b"], vm.LocalModels.Select(option => option.Id));
        Assert.Equal("gemma4:26b", vm.SelectedLocalModel?.Id);
    }

    [Fact]
    public void ThinkingAndCodexFields_Save()
    {
        var vm = Create();

        vm.LocalThinkingDisabled = false;
        vm.CommitCodexBinaryPathCommand.Execute(" /opt/codex ");
        vm.CommitCodexModelCommand.Execute("gpt-6");

        Assert.False(_settings.Current.LocalThinkingDisabled);
        Assert.Equal("/opt/codex", _settings.Current.CodexBinaryPath);
        Assert.Equal("gpt-6", _settings.Current.CodexModel);

        vm.CommitCodexBinaryPathCommand.Execute("  ");

        Assert.Null(_settings.Current.CodexBinaryPath);
    }

    [Fact]
    public void SettingsChangedElsewhere_AreShownWithoutSavingAgain()
    {
        var vm = Create();

        _settings.Update(s => s with
        {
            ProcessingMode = ProcessingMode.Codex,
            LocalServerUrl = "http://localhost:1234",
            LocalModel = "m",
            LocalThinkingDisabled = false,
            CodexModel = "gpt-6",
        });

        Assert.Equal(1, _settings.SaveCount);
        Assert.True(vm.IsCodexMode);
        Assert.Equal("http://localhost:1234", vm.LocalServerUrl);
        Assert.Equal("m", vm.SelectedLocalModel?.Id);
        Assert.False(vm.LocalThinkingDisabled);
        Assert.Equal("gpt-6", vm.CodexModel);
    }

    [Fact]
    public void AFailedSave_RestoresTheSavedValue()
    {
        var vm = Create();
        _settings.UpdateFailure = new IOException("disk full");

        vm.LocalThinkingDisabled = false;

        Assert.True(vm.LocalThinkingDisabled);
    }

    [Fact]
    public async Task CheckCodex_ReportsSignedIn()
    {
        _providers.Codex = new ProviderStatus(ProviderStatusKind.Ready, "Provider.Status.Ready", [], "codex-cli 0.162.0");
        var vm = Create();

        await vm.CheckCodexCommand.ExecuteAsync(null);

        Assert.Equal("Codex CLI 0.162.0, signed in.", vm.CodexStatus);
        Assert.False(vm.IsCodexProbing);
    }

    private ModelsSectionViewModel Create()
    {
        var localizer = new Localizer();
        return new ModelsSectionViewModel(_settings, _providers.CreateService(localizer), localizer);
    }
}
