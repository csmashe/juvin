using System.Runtime.Versioning;
using Juvin.Platform.Windows.Launcher;
using Microsoft.Win32;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("windows")]
public sealed class RunKeyLoginItemTests : IDisposable
{
    private const string ExePath = @"C:\Users\me\AppData\Local\Programs\Juvin\juvin.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // ReSharper disable once UseRawString -- a raw string cannot start with a quote character
    private const string ForeignValue = @"""C:\Other\juvin.exe"" --background";

    private readonly string _rootName = @"Software\JuvinTests\" + Guid.NewGuid().ToString("N");
    private RegistryKey? _userRoot;

    public void Dispose()
    {
        _userRoot?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
        }
    }

    private RunKeyLoginItem Create(string? processPath = ExePath)
    {
        _userRoot ??= Registry.CurrentUser.CreateSubKey(_rootName);
        return new RunKeyLoginItem(processPath, _userRoot);
    }

    private object? RunValue()
    {
        using var key = _userRoot!.OpenSubKey(RunKey);
        return key?.GetValue("Juvin");
    }

    [WindowsFact]
    public async Task Enable_WritesTheRunValueWithTheBackgroundArgument()
    {
        var item = Create();

        await item.EnableAsync(CancellationToken.None);

        Assert.Equal($"\"{ExePath}\" --background", RunValue());
    }

    [WindowsFact]
    public async Task Disable_RemovesTheRunValue_AndIsIdempotent()
    {
        var item = Create();
        await item.EnableAsync(CancellationToken.None);

        await item.DisableAsync(CancellationToken.None);
        await item.DisableAsync(CancellationToken.None);

        Assert.Null(RunValue());
    }

    [WindowsFact]
    public async Task AValueForAnotherExecutable_IsNotEnabledAndNotRemoved()
    {
        var item = Create();
        using (var key = _userRoot!.CreateSubKey(RunKey))
        {
            key.SetValue("Juvin", ForeignValue);
        }

        await item.DisableAsync(CancellationToken.None);

        Assert.Equal(ForeignValue, RunValue());
    }

    [WindowsFact]
    public async Task UnknownExecutable_IsUnsupportedAndEnableThrows()
    {
        var item = Create(processPath: null);

        Assert.False(item.IsSupported);
        await Assert.ThrowsAsync<LoginItemException>(() => item.EnableAsync(CancellationToken.None));
    }
}
