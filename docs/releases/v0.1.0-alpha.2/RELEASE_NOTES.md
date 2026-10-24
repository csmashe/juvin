# Juvin 0.1.0-alpha.2

Second alpha. The user guide is now on the [wiki](https://github.com/csmashe/juvin/wiki), with install steps for each system.

**Before you update:** if you registered Chrome with alpha.1, open Settings, Browser connector after updating and press **Update** on the Chrome row once. It reads "Needs update" until you do, and the Chrome Web Store version of the extension can only connect after it.

**New and changed**
- The Chrome Web Store version of the extension can connect to Juvin. The store listings for Chrome and Firefox are in review.
- On Windows and Linux, launchers read **Juvin (Alpha)** while Juvin is in alpha.
- On macOS, the menu bar shows **Juvin** with an **About Juvin** item.
- The setup wizard reopens at the step you left after a restart.

**Fixed**
- Linux: Allow keyboard access hung for a long time after the password was accepted.
- Windows: Open sample email in the setup wizard left the browser behind the wizard.
- Linux: after switching from Sway or Hyprland to another desktop, Juvin could still use the old desktop's shortcut settings, and removing the Sway shortcut could drop a blank line from the end of the Sway config.
- The "draft changed" card fits short screens.

The macOS dmg (Apple silicon) is ad-hoc signed and not notarized, so macOS blocks the first open: allow it in System Settings, Privacy & Security, **Open Anyway**. The Windows installer is not code-signed yet, so SmartScreen may ask you to confirm with **More info, Run anyway**.

## What's Changed
* Name the macOS menu Juvin and resume setup where a restart left it by @csmashe in https://github.com/csmashe/juvin/pull/40
* Prepare the Chrome Web Store and Firefox Add-ons submissions by @csmashe in https://github.com/csmashe/juvin/pull/41
* Allow the Chrome Web Store build to reach the native host by @csmashe in https://github.com/csmashe/juvin/pull/42
* Show the prerelease word in the launcher name by @csmashe in https://github.com/csmashe/juvin/pull/43
* Address the open CodeQL alerts by @csmashe in https://github.com/csmashe/juvin/pull/44
* Fix what the alpha.2 test pass found on every system by @csmashe in https://github.com/csmashe/juvin/pull/45


**Full Changelog**: https://github.com/csmashe/juvin/compare/v0.1.0-alpha.1...v0.1.0-alpha.2

