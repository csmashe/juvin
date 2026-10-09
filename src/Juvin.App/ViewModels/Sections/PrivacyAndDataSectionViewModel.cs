using CommunityToolkit.Mvvm.ComponentModel;
using Juvin.Core;

namespace Juvin.App.ViewModels.Sections;

public sealed class PrivacyAndDataSectionViewModel : ObservableObject
{
#pragma warning disable CA1822 // compiled bindings need an instance member
    // ReSharper disable once MemberCanBeMadeStatic.Global -- compiled bindings need an instance member
    public string DataFolder => JuvinEnvironment.DataPath;
#pragma warning restore CA1822
}
