namespace Juvin.Platform.Tests;

internal sealed class TempDirectory : IDisposable
{
    private readonly string _path;

    public TempDirectory()
    {
        // Kept short: Unix socket paths are limited to about 100 bytes.
        _path = Path.Join(Path.GetTempPath(), "jv-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_path);
    }

    public string File(string name) => Path.Join(_path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp folder eventually.
        }
    }
}
