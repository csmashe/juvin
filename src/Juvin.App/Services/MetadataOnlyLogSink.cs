using System.Diagnostics;
using Avalonia.Logging;

namespace Juvin.App.Services;

/// <summary>Forwards Avalonia warnings without their message, since binding warnings can quote bound values such as draft text.</summary>
internal sealed class MetadataOnlyLogSink : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(level, area, source);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Write(level, area, source);

    private static void Write(LogEventLevel level, string area, object? source)
    {
        if (level >= LogEventLevel.Warning)
        {
            Trace.WriteLine($"[Avalonia] {level} in {area} from {source?.GetType().Name ?? "unknown"}");
        }
    }
}
