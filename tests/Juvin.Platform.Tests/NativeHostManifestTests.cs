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
        Assert.Equal(HostRegistration.NotRegistered, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));

        File.WriteAllText(path, NativeHostManifest.Render(BrowserFamily.Firefox, "/a/juvin-host"));

        Assert.Equal(HostRegistration.Registered, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/b/juvin-host"));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, null));
    }

    [Theory]
    [InlineData(BrowserFamily.Firefox)]
    [InlineData(BrowserFamily.Chromium)]
    public void Check_IgnoresWhitespaceInARenderedManifest(BrowserFamily family)
    {
        var path = _temp.File(NativeHostManifest.FileName);
        using var rendered = JsonDocument.Parse(NativeHostManifest.Render(family, "/a/juvin-host"));
        File.WriteAllText(path, JsonSerializer.Serialize(rendered.RootElement));

        Assert.Equal(HostRegistration.Registered, NativeHostManifest.Check(path, family, "/a/juvin-host"));
    }

    [Fact]
    public void Check_TreatsAManifestForTheOtherFamilyAsStale()
    {
        var path = _temp.File(NativeHostManifest.FileName);
        File.WriteAllText(path, NativeHostManifest.Render(BrowserFamily.Chromium, "/a/juvin-host"));

        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));
    }

    [Theory]
    [InlineData("""{"path":"/a/juvin-host"}""")]
    [InlineData("""{"name":"com.juvin.host","description":"Juvin browser connector","path":"/a/juvin-host","type":"stdio"}""")]
    [InlineData("""{"name":"com.juvin.host","description":"Juvin browser connector","path":"/a/juvin-host","type":"pipe","allowed_extensions":["extension@juvin.app"]}""")]
    [InlineData("""{"name":"com.juvin.host","description":"Juvin browser connector","path":"/a/juvin-host","type":"stdio","allowed_extensions":["someone@else.example"]}""")]
    [InlineData("""{"name":"com.juvin.host","description":"Juvin browser connector","path":"/a/juvin-host","type":"stdio","allowed_extensions":["extension@juvin.app","someone@else.example"]}""")]
    [InlineData("""{"name":"other.host","description":"Juvin browser connector","path":"/a/juvin-host","type":"stdio","allowed_extensions":["extension@juvin.app"]}""")]
    public void Check_TreatsAManifestThatDiffersFromTheRenderedOneAsStale(string json)
    {
        var path = _temp.File(NativeHostManifest.FileName);
        File.WriteAllText(path, json);

        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));
    }

    [Fact]
    public void Check_TreatsAnUnreadableManifestAsStale()
    {
        var path = _temp.File(NativeHostManifest.FileName);
        File.WriteAllText(path, "not json");

        Assert.Null(NativeHostManifest.ReadHostPath(path));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));
    }

    [Fact]
    public void Check_TreatsAPathWithAnUnpairedSurrogateAsStale()
    {
        var path = _temp.File(NativeHostManifest.FileName);
        File.WriteAllText(path, """{"path":"\uD800"}""");

        Assert.Null(NativeHostManifest.ReadHostPath(path));
        Assert.Equal(HostRegistration.Stale, NativeHostManifest.Check(path, BrowserFamily.Firefox, "/a/juvin-host"));
    }

    [Fact]
    public void RequireHost_ThrowsAUserReadableErrorWhenTheHostIsMissing()
    {
        Assert.Equal("/a", NativeHostManifest.RequireHost("/a"));
        Assert.Throws<NativeHostRegistrationException>(() => NativeHostManifest.RequireHost(null));
    }
}
