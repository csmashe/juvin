using Juvin.App.Tests.Fakes;
using Juvin.App.ViewModels.Sections;
using Juvin.Core.Localization;
using Juvin.Core.Models;

namespace Juvin.App.Tests;

public sealed class HomeSectionViewModelTests
{
    private readonly FakeHistoryService _history = new();
    private readonly Localizer _localizer = new();
    private readonly FakeSettingsService _settings = new();

    [Fact]
    public void TheList_ShowsTheFiveNewest()
    {
        for (var i = 1; i <= 7; i++)
        {
            _history.Add(Entry(i.ToString()));
        }

        var vm = Create();

        Assert.Equal(["7", "6", "5", "4", "3"], vm.RecentRewrites.Select(row => row.Id));
        Assert.True(vm.HasRecentRewrites);
    }

    [Fact]
    public void ARow_ShowsItsEntry()
    {
        _history.Add(Entry("a", subject: null, applied: true));

        var row = Assert.Single(Create().RecentRewrites);

        Assert.Equal("Make clearer", row.Title);
        Assert.Equal("(No subject)", row.Subject);
        Assert.EndsWith(", Gmail", row.Detail);
        Assert.True(row.Applied);
        Assert.Equal("result a", row.Result);
    }

    [Fact]
    public void TheList_FollowsHistoryChanges()
    {
        var vm = Create();
        Assert.False(vm.HasRecentRewrites);

        _history.Add(Entry("a"));

        Assert.Equal("a", Assert.Single(vm.RecentRewrites).Id);
    }

    [Fact]
    public void Delete_RemovesTheEntry()
    {
        _history.Add(Entry("a"));
        _history.Add(Entry("b"));
        var vm = Create();

        vm.DeleteCommand.Execute(vm.RecentRewrites[0]);

        Assert.Equal(["a"], _history.Entries.Select(e => e.Id));
        Assert.Equal(["a"], vm.RecentRewrites.Select(row => row.Id));
    }

    [Fact]
    public void AFailedDelete_ShowsTheError()
    {
        _history.Add(Entry("a"));
        var vm = Create();
        _history.Failure = new IOException("locked");

        vm.DeleteCommand.Execute(vm.RecentRewrites[0]);

        Assert.True(vm.HasError);
        Assert.Single(vm.RecentRewrites);
    }

    [Fact]
    public async Task Copy_WritesTheResult()
    {
        _history.Add(Entry("a"));
        var vm = Create();
        var copied = new List<string>();
        vm.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };

        await vm.CopyCommand.ExecuteAsync(vm.RecentRewrites[0]);

        Assert.Equal(["result a"], copied);
    }

    [Fact]
    public void TheEmptyHint_DependsOnWhetherHistoryIsOn()
    {
        var vm = Create();
        Assert.Equal(_localizer["Home.RecentHint"], vm.RecentHint);

        _settings.Update(s => s with { HistoryEnabled = true });

        Assert.Equal(_localizer["Home.RecentEmpty"], vm.RecentHint);
    }

    private HomeSectionViewModel Create() => new(_history, _settings, _localizer);

    private static HistoryEntry Entry(string id, string? subject = "Re: timeline", bool applied = false) =>
        new()
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            ActionTitle = "Make clearer",
            Subject = subject,
            Adapter = "gmail",
            Draft = "draft " + id,
            Result = "result " + id,
            Applied = applied,
        };
}
