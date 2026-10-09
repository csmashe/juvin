// Throwaway Phase 0 spike: can an Avalonia window sit always-on-top at the
// top-right of the primary display on GNOME Wayland (through XWayland) and Windows?
//
//   juvin-overlay-spike [--seconds 15] [--cover]
//
// Shows a borderless topmost panel at the top-right of the primary screen's
// working area, optionally opens a second maximized normal window after 2 s
// (--cover) to prove the panel stays above it, prints one JSON line with what
// Avalonia believes about the placement, and exits after --seconds.

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal static class Program
{
    public static int Main(string[] args)
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class App : Application
{
    private const int Margin = 16;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var args = desktop.Args ?? [];
        var seconds = 15;
        var i = Array.IndexOf(args, "--seconds");
        if (i >= 0 && i + 1 < args.Length)
        {
            seconds = int.Parse(args[i + 1]);
        }
        var cover = Array.IndexOf(args, "--cover") >= 0;

        var panel = new Window
        {
            Title = "Juvin overlay spike",
            Width = 360,
            Height = 200,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#141D26")),
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#F5A623")),
                BorderThickness = new Thickness(2),
                Child = new TextBlock
                {
                    Text = "Juvin overlay spike\nTop-right, always on top",
                    Foreground = Brushes.White,
                    FontSize = 20,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        desktop.MainWindow = panel;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        panel.Opened += async (_, _) =>
        {
            Place(panel);
            await Task.Delay(500);
            Place(panel);
            await Task.Delay(1500);
            var report = Report(panel, "after-place");
            Window? coverWindow = null;
            if (cover)
            {
                coverWindow = new Window
                {
                    Title = "Juvin cover window",
                    WindowState = WindowState.Maximized,
                    Background = new SolidColorBrush(Color.Parse("#2B6CB0")),
                    Content = new TextBlock
                    {
                        Text = "Maximized normal window opened after the overlay. The overlay should still be visible top-right.",
                        FontSize = 24,
                        Foreground = Brushes.White,
                        Margin = new Thickness(40),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                coverWindow.Show();
                coverWindow.Activate();
                await Task.Delay(1500);
                report = Report(panel, "after-cover");
            }
            Console.WriteLine(report);
            Console.Out.Flush();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            coverWindow?.Close();
            desktop.Shutdown();
        };
    }

    private static void Place(Window panel)
    {
        var screen = panel.Screens.Primary ?? panel.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }
        var scale = panel.RenderScaling;
        var wa = screen.WorkingArea;
        var w = (int)Math.Round(panel.Width * scale);
        var x = wa.Right - w - (int)Math.Round(Margin * scale);
        var y = wa.Y + (int)Math.Round(Margin * scale);
        panel.Position = new PixelPoint(x, y);
    }

    private static string Report(Window panel, string phase)
    {
        var screen = panel.Screens.Primary ?? panel.Screens.All.FirstOrDefault();
        var wa = screen?.WorkingArea ?? default;
        var bounds = screen?.Bounds ?? default;
        var size = panel.FrameSize ?? panel.ClientSize;
        var expectedX = wa.Right - (int)Math.Round(panel.Width * panel.RenderScaling) - (int)Math.Round(Margin * panel.RenderScaling);
        var expectedY = wa.Y + (int)Math.Round(Margin * panel.RenderScaling);
        return JsonSerializer.Serialize(new
        {
            phase,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            backend = panel.PlatformImpl?.GetType().Name,
            screenBounds = $"{bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}",
            workingArea = $"{wa.X},{wa.Y} {wa.Width}x{wa.Height}",
            scaling = panel.RenderScaling,
            position = $"{panel.Position.X},{panel.Position.Y}",
            expected = $"{expectedX},{expectedY}",
            positionMatches = panel.Position.X == expectedX && panel.Position.Y == expectedY,
            frameSize = $"{size.Width}x{size.Height}",
            topmost = panel.Topmost,
            isActive = panel.IsActive
        });
    }
}
