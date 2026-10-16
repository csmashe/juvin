using CommunityToolkit.Mvvm.ComponentModel;

namespace Juvin.App.ViewModels.Sections;

public sealed class AboutSectionViewModel : ObservableObject
{
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- the view binds this on the instance, and a computed form is flagged as static
    public string Version { get; } = AppVersion.Current;
}
