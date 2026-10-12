using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Juvin.App.Localization;
using Juvin.App.ViewModels.Sections;

namespace Juvin.App.Views.Sections;

public partial class PrivacyAndDataSection : UserControl
{
    private const string JsonExtension = ".json";

    public PrivacyAndDataSection()
    {
        InitializeComponent();
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia click handlers must return void; the launcher throws nothing the user can act on
    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PrivacyAndDataSectionViewModel viewModel && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(viewModel.DataFolder));
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia click handlers must return void; every failure is caught below
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PrivacyAndDataSectionViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        try
        {
            var json = new FilePickerFileType(StrExtension.Localizer["PrivacyAndData.JsonFiles"]) { Patterns = ["*" + JsonExtension] };
            var picked = await topLevel.StorageProvider.SaveFilePickerWithResultAsync(
                new FilePickerSaveOptions
                {
                    Title = StrExtension.Localizer["PrivacyAndData.ExportTitle"],
                    SuggestedFileName = "juvin-history",
                    DefaultExtension = "md",
                    ShowOverwritePrompt = true,
                    FileTypeChoices =
                    [
                        new FilePickerFileType(StrExtension.Localizer["PrivacyAndData.MarkdownFiles"]) { Patterns = ["*.md"] },
                        json,
                    ],
                }
            );
            if (picked.File is not { } file)
            {
                return;
            }

            // Pickers that cannot report the chosen type (Linux portals) leave it null, so the name decides then.
            var asJson = picked.SelectedFileType is { } type
                ? type == json
                : file.Name.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase);
            var text = asJson ? viewModel.ExportJson() : viewModel.ExportMarkdown();
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ReportExportFailure(ex);
        }
    }
}
