# Juvin 0.1.0-alpha.4

Fourth alpha. Mostly fixes from the alpha.4 test pass on every system, plus a sturdier capture when Gmail or Outlook changes its page.

**Before you update:** this release goes with version 0.1.3 of the browser extension. Your browser updates it from the Chrome Web Store or Firefox Add-ons once the stores publish it. On macOS, if the shortcut stops working after the update, open the setup wizard's Accessibility step, press **Open Accessibility settings**, turn Juvin on again, and press **Restart Juvin**.

**New and changed**
- If Gmail or Outlook changes its page so Juvin no longer recognizes the draft editor, the shortcut still works on the selected text in the editor you are typing in. Juvin then sends the page's visible text as the conversation, labelled **Visible text only**, instead of refusing.
- The browser extension reconnects by itself after Juvin restarts or updates, and when Juvin starts at login after the browser. Before, it stayed disconnected until you opened its popup. The extension asks for the `alarms` permission for this.
- General has a **Quit Juvin** row on every system, for desktops whose tray has no menu (Sway with swaybar).
- **Delete all data** also removes Juvin's diagnostic logs.
- On macOS, **Open Accessibility settings** clears Juvin's old Accessibility entry first when Juvin is not trusted, so the permission comes back after an update without Terminal.
- On macOS, when a model server on your network cannot be reached, Juvin points you to the Local Network permission.
- A failed Codex check says which command failed and how to see why, instead of calling Codex too old. Juvin also finds Codex in the folder its own installer uses.

**Fixed**
- Outlook: once the quoted original moved into the reply editor, Juvin rewrote the reply without it as context.
- Outlook: in one editor layout, a refused Apply left the selected line empty. Juvin now puts your text back whenever Apply refuses after touching the editor.
- Firefox: rewriting a phrase inside a line left no-break spaces around the new words.
- With the rewrite panel focused after a declined capture, pressing the shortcut again said to focus the browser instead of capturing.
- Hyprland: Escape often did not hide the rewrite panel after its first show.
- Sway: a harmless panel placement was logged as a failure.
- Linux: after an upgrade, Flatpak browsers read **Needs update** in Browser connector until you pressed Update.
- Launch at login opened the main window when the tray started a few seconds after Juvin.
- Quitting Juvin during a Codex rewrite or check could leave Codex running.
- Windows: uninstalling right after quitting Juvin could leave `juvin-host.exe` and the install folder behind.
- The setup wizard showed the local model server box after you chose Codex mode.
- The extension popup now mentions Outlook, and About describes the diagnostic log.

The macOS dmg (Apple silicon) is ad-hoc signed and not notarized, so macOS blocks the first open: allow it in System Settings, Privacy & Security, **Open Anyway**. The Windows installer is not code-signed yet, so SmartScreen may ask you to confirm with **More info, Run anyway**.

## What's Changed
* Prepare Windows signing with SignPath Foundation by @csmashe in https://github.com/csmashe/juvin/pull/53
* Keep rewriting when Gmail or Outlook changes its draft editor by @csmashe in https://github.com/csmashe/juvin/pull/54
* Remove the logs on Delete all data by @csmashe in https://github.com/csmashe/juvin/pull/55
* Version the browser extension 0.1.3 by @csmashe in https://github.com/csmashe/juvin/pull/56
* Let the macOS kit drive Firefox 159 and steady a hook timing test by @csmashe in https://github.com/csmashe/juvin/pull/57
* Let Juvin get its macOS Accessibility permission back after an update by @csmashe in https://github.com/csmashe/juvin/pull/58
* Keep Flatpak browsers registered after a Linux upgrade by @csmashe in https://github.com/csmashe/juvin/pull/63
* Stop showing the Local model server box when Codex mode is chosen by @csmashe in https://github.com/csmashe/juvin/pull/64
* Put the user's text back whenever Apply refuses after touching the editor by @csmashe in https://github.com/csmashe/juvin/pull/65
* Find Codex where its own installer puts it by @csmashe in https://github.com/csmashe/juvin/pull/66
* Reconnect the browser extension on its own after Juvin restarts by @csmashe in https://github.com/csmashe/juvin/pull/67
* Keep the shortcut working while the rewrite panel has focus by @csmashe in https://github.com/csmashe/juvin/pull/68
* Let Sway users quit Juvin, and stop a false Sway placement failure by @csmashe in https://github.com/csmashe/juvin/pull/70
* Stop Codex processes when Juvin quits by @csmashe in https://github.com/csmashe/juvin/pull/71
* Let Escape hide the rewrite panel on Hyprland every time by @csmashe in https://github.com/csmashe/juvin/pull/72
* Remove juvin-host.exe on uninstall when a browser still runs it by @csmashe in https://github.com/csmashe/juvin/pull/74
* Keep Outlook's quoted original as context once it moves into the reply editor by @csmashe in https://github.com/csmashe/juvin/pull/59
* Tell macOS users to allow Local Network when a LAN model server seems unreachable by @csmashe in https://github.com/csmashe/juvin/pull/60
* Say why a Codex check failed instead of calling Codex too old by @csmashe in https://github.com/csmashe/juvin/pull/61
* Mention Outlook in the popup and the log in About by @csmashe in https://github.com/csmashe/juvin/pull/62
* Keep Juvin in the tray at login when the tray starts late by @csmashe in https://github.com/csmashe/juvin/pull/73
* Keep plain spaces around a rewritten phrase in Firefox by @csmashe in https://github.com/csmashe/juvin/pull/69


**Full Changelog**: https://github.com/csmashe/juvin/compare/v0.1.0-alpha.3...v0.1.0-alpha.4
