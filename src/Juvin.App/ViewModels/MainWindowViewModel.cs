using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Juvin.App.ViewModels.Sections;
using Juvin.App.Views;
using Juvin.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Juvin.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private SetupWizard? _openWizard;

    [ObservableProperty]
    private object? _currentSection;

    public MainWindowViewModel(
        IServiceProvider services,
        Localizer localizer,
        HomeSectionViewModel home,
        ShortcutsSectionViewModel shortcuts,
        RewriteActionsSectionViewModel rewriteActions,
        BrowserConnectorSectionViewModel browserConnector,
        ModelsSectionViewModel models,
        PrivacyAndDataSectionViewModel privacyAndData,
        GeneralSectionViewModel general,
        AboutSectionViewModel about
    )
    {
        _services = services;
        NavItems =
        [
            new NavItem("Nav.Home", localizer, Symbol.Home, home),
            new NavItem("Nav.Shortcuts", localizer, Symbol.Keyboard, shortcuts),
            new NavItem("Nav.RewriteActions", localizer, Symbol.TextEditStyle, rewriteActions),
            new NavItem("Nav.BrowserConnector", localizer, Symbol.PlugConnected, browserConnector),
            new NavItem("Nav.Models", localizer, Symbol.BrainCircuit, models),
            new NavItem("Nav.PrivacyAndData", localizer, Symbol.ShieldLock, privacyAndData),
            new NavItem("Nav.General", localizer, Symbol.Settings, general),
            new NavItem("Nav.About", localizer, Symbol.Info, about),
        ];
        Select(NavItems[0]);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public void Navigate<TSection>()
        where TSection : class
    {
        var target = NavItems.FirstOrDefault(item => item.Content is TSection);
        if (target is not null)
        {
            Select(target);
        }
    }

    /// <summary>Brings the open setup wizard, which is modal over the main window, back in front of it.</summary>
    public void ActivateOpenWizard() => _openWizard?.Activate();

    [RelayCommand]
    private void NavigateTo(NavItem? item)
    {
        if (item is not null)
        {
            Select(item);
        }
    }

    [RelayCommand]
    private void OpenWizard()
    {
        if (_openWizard is not null)
        {
            _openWizard.Activate();
            return;
        }

        var wizard = _services.GetRequiredService<SetupWizard>();
        wizard.DataContext = _services.GetRequiredService<SetupWizardViewModel>();
        wizard.Closed += (_, _) => _openWizard = null;
        _openWizard = wizard;

        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner }
        )
        {
            _ = wizard.ShowDialog(owner);
        }
        else
        {
            wizard.Show();
        }
    }

    private void Select(NavItem target)
    {
        foreach (var item in NavItems)
        {
            item.IsSelected = item == target;
        }

        CurrentSection = target.Content;
    }
}

public sealed partial class NavItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public NavItem(string labelKey, Localizer localizer, Symbol symbol, object content)
    {
        Label = localizer[labelKey];
        Symbol = symbol;
        Content = content;
    }

    public string Label { get; }
    public Symbol Symbol { get; }
    public object Content { get; }
}
