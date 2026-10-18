using System.Runtime.Versioning;
using Juvin.Platform.Mac.Panel;
using Behavior = Juvin.Platform.Mac.Panel.MacPanelWindowSupport.CollectionBehavior;

namespace Juvin.Platform.Tests;

// The Objective-C calls need macOS; these cover the behavior arithmetic only.
[SupportedOSPlatform("macos")]
public sealed class MacPanelWindowSupportTests
{
    [Fact]
    public void PanelBehavior_JoinsEverySpaceAndFullScreenInsteadOfAvaloniasPrimaryWindowDefaults()
    {
        const nuint avaloniaDefault = Behavior.FullScreenPrimary | Behavior.Managed | Behavior.MoveToActiveSpace;

        var behavior = MacPanelWindowSupport.PanelBehavior(avaloniaDefault, stageManagerRoles: false);

        Assert.Equal(Behavior.CanJoinAllSpaces | Behavior.FullScreenAuxiliary, behavior);
    }

    [Fact]
    public void PanelBehavior_JoinsOtherAppsFromMacOs13_ReplacingAStageManagerRole()
    {
        var behavior = MacPanelWindowSupport.PanelBehavior(Behavior.Primary | Behavior.FullScreenAllowsTiling, stageManagerRoles: true);

        Assert.Equal(Behavior.CanJoinAllSpaces | Behavior.FullScreenAuxiliary | Behavior.CanJoinAllApplications, behavior);
    }

    [Fact]
    public void PanelBehavior_KeepsUnrelatedFlags()
    {
        const nuint ignoresCycle = 1 << 6;

        var behavior = MacPanelWindowSupport.PanelBehavior(ignoresCycle, stageManagerRoles: false);

        Assert.Equal(ignoresCycle | Behavior.CanJoinAllSpaces | Behavior.FullScreenAuxiliary, behavior);
    }

    [Fact]
    public void PanelBehavior_IsStableOnceApplied()
    {
        var once = MacPanelWindowSupport.PanelBehavior(Behavior.FullScreenPrimary, stageManagerRoles: true);

        Assert.Equal(once, MacPanelWindowSupport.PanelBehavior(once, stageManagerRoles: true));
    }
}
