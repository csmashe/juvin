namespace Juvin.Core.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "juvin-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Join(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp folder eventually.
        }
    }
}
