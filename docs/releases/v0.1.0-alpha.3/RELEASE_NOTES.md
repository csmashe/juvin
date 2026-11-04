# Juvin 0.1.0-alpha.3

Third alpha. Juvin now rewrites drafts in Outlook on the web as well as Gmail. The [user guide](https://github.com/csmashe/juvin/wiki) covers both.

**Before you update:** Outlook needs version 0.1.2 of the browser extension. Your browser updates it from the Chrome Web Store or Firefox Add-ons once the stores publish it. Chrome then asks you to allow Juvin on the Outlook sites; accept, or Juvin keeps working in Gmail only.

**New and changed**
- Outlook on the web (outlook.live.com, outlook.office.com, outlook.office365.com, and outlook.cloud.microsoft) gets the same shortcut, action menu, apply in place, and Ctrl+Z as Gmail. Juvin reads the open messages of a conversation and counts the collapsed ones, or the quoted original under a reply in its own tab or window.
- Juvin keeps a diagnostic log, `Logs/juvin.log` in its data folder, plus the one from the run before. It carries lengths, identifiers, and error types, never your email, and nothing sends it anywhere. The [Troubleshooting](https://github.com/csmashe/juvin/wiki/Troubleshooting#still-stuck) page says where to find it.
- **Delete all data** also removes the shortcut Juvin registered with your desktop and the launch-at-login entry. On Linux and macOS, run it before you uninstall, since uninstalling leaves those behind.
- Crash reports name the source file in Juvin's public code for each of Juvin's own stack frames. They still carry no file names or paths from your computer.

**Fixed**
- Linux: Delete all data left the desktop shortcut in place, so after an uninstall Ctrl+Shift+F could stay pointed at a Juvin that no longer exists.
- Nothing was written to the `Logs` folder, so a Juvin started from the menu, the dock, or the Start menu left no record of what went wrong.

The macOS dmg (Apple silicon) is ad-hoc signed and not notarized, so macOS blocks the first open: allow it in System Settings, Privacy & Security, **Open Anyway**. The Windows installer is not code-signed yet, so SmartScreen may ask you to confirm with **More info, Run anyway**.

## What's Changed
* Version the browser extension 0.1.1 by @csmashe in https://github.com/csmashe/juvin/pull/46
* Stop the loopback test server without a shutdown race by @csmashe in https://github.com/csmashe/juvin/pull/49
* Keep source file names in crash reports from release builds by @csmashe in https://github.com/csmashe/juvin/pull/50
* Rewrite Outlook on the web drafts as well as Gmail by @csmashe in https://github.com/csmashe/juvin/pull/51
* Remove the desktop shortcut on Delete all data by @csmashe in https://github.com/csmashe/juvin/pull/47
* Write a log file in every build by @csmashe in https://github.com/csmashe/juvin/pull/48
* Version the browser extension 0.1.2 by @csmashe in https://github.com/csmashe/juvin/pull/52


**Full Changelog**: https://github.com/csmashe/juvin/compare/v0.1.0-alpha.2...v0.1.0-alpha.3
