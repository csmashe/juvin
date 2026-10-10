using System.Text.Json;

namespace Juvin.Platform.Tests;

public sealed class NativeHostManifestTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Render_Firefox_AllowsTheExtensionId()
    {
        using var manifest = JsonDocument.Parse(NativeHostManifest.Render(BrowserFamily.Firefox, "/opt/juvin/juvin-host"));
        var root = manifest.RootElement;

        Assert.Equal(["name", "description", "path", "type", "allowed_extensions"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(NativeHost.Name, root.GetProperty("name").GetString());
        Assert.Equal(NativeHost.Description, root.GetProperty("description").GetString());
        Assert.Equal("/opt/juvin/juvin-host", root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal([NativeHost.FirefoxExtensionId], root.GetProperty("allowed_extensions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Render_Chromium_AllowsTheOriginAndKeepsPathsReadable()
    {
        var json = NativeHostManifest.Render(BrowserFamily.Chromium, @"C:\Users\Zoë\juvin-host.exe");
        using var manifest = JsonDocument.Parse(json);

        Assert.Contains("Zoë", json);
        Assert.Equal(@"C:\Users\Zoë\juvin-host.exe", manifest.RootElement.GetProperty("path").GetString());
        Assert.Equal([NativeHost.ChromeOrigin], manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()));
        Assert.False(manifest.RootElement.TryGetProperty("allowed_extensions", out _));
    }

    [Fact]
    public void Check_ReportsMissingMatchingAndDifferentPaths()
    {
        var path = _temp.File(NativeHostManifest.FileName);
        Assert.Equal(HostRegistration.NotRegistered, NativeHostManifest.Check(path, "/a/juvin-host"));

        File.WriteAllText(path, NativeHostManifest.Render(BrowserFamily.Firefox, "/a/juvin-host"));

        Assert.Equal(HostRegistration.Registered, NativeHostManifest.Check(path, "/a/juvin-host"));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, "/b/juvin-host"));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, null));
    }

    [Fact]
    public void Check_TreatsAnUnreadableManifestAsStale()
    {
        var path = _temp.File(NativeHostManifest.FileName);
        File.WriteAllText(path, "not json");

        Assert.Null(NativeHostManifest.ReadHostPath(path));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, "/a/juvin-host"));
    }

    [Fact]
    public void RequireHost_ThrowsAUserReadableErrorWhenTheHostIsMissing()
    {
        Assert.Equal("/a", NativeHostManifest.RequireHost("/a"));
        Assert.Throws<NativeHostRegistrationException>(() => NativeHostManifest.RequireHost(null));
    }
}
