using System.Runtime.Versioning;
using System.Text;
using Juvin.Platform.Mac.Secrets;

namespace Juvin.Platform.Tests;

// Uses the real login Keychain under a throwaway service, so it runs only in the macOS CI job.
[SupportedOSPlatform("macos")]
public sealed class KeychainTests : IDisposable
{
    private readonly string _service = "JuvinTests." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS())
        {
            Keychain.Delete(_service, "token");
        }
    }

    [MacFact]
    public void SetGetOverwriteAndDelete_RoundTrip()
    {
        Assert.Null(Keychain.Find(_service, "token"));

        Keychain.Upsert(_service, "token", [.. "first"u8]);
        Assert.Equal("first", Encoding.UTF8.GetString(Keychain.Find(_service, "token")!));

        Keychain.Upsert(_service, "token", [.. "second"u8]);
        Assert.Equal("second", Encoding.UTF8.GetString(Keychain.Find(_service, "token")!));

        Keychain.Delete(_service, "token");
        Assert.Null(Keychain.Find(_service, "token"));
        Keychain.Delete(_service, "token");
    }
}
