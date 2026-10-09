using Juvin.Core.Localization;
using Juvin.Core.Models;
using Juvin.Core.Providers;

namespace Juvin.App.ViewModels;

/// <summary>One entry of the Local / Codex choice shared by the Models section and the setup wizard.</summary>
public sealed record ProcessingModeOption(ProcessingMode Mode, string Label, string Hint)
{
    public static IReadOnlyList<ProcessingModeOption> All(Localizer localizer) =>
    [
        new(ProcessingMode.Local, localizer["Models.Mode.Local"], localizer["Models.Mode.LocalHint"]),
        new(ProcessingMode.Codex, localizer["Models.Mode.Codex"], localizer["Models.Mode.CodexHint"]),
    ];
}

/// <summary>A model in a picker: the name with its parameter size, flagged when it is low-quality class.</summary>
public sealed record ModelOption(string Id, string Label, bool IsLowQuality)
{
    public static ModelOption From(ModelInfo model) =>
        new(
            model.Id,
            model.ParameterSize is { } size ? $"{model.DisplayName} ({size})" : model.DisplayName,
            model.IsLowQualityClass
        );
}
