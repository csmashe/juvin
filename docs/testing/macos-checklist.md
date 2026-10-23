# macOS checklist

Juvin has never run on a Mac. Every macOS check so far was deferred ("macOS assumed"): the native messaging round trip (Phase 0), the registrar beyond its path tests (Phase 3), the hook and the panel (Phase 4), and the first real run of the macOS platform package (Phase 5, item 5 of `docs/mvp-scope.md`). This file is the single list for running all of them in one rented session. The dmg build and its smoke test are in the macOS section of `packaging-checklist.md`; this file only refers to them. Use synthetic drafts; the log lines carry counts and codes only.

## Environment

- NoMac (nomac.app) cloud Mac, Apple Silicon, macOS 26.6.2, a fresh VM per session, account `agent` with passwordless sudo. Only `~/drive` persists between sessions.
- By default NoMac answers permission dialogs itself and grants Accessibility to programs started from a command, so a first-run check is honest only on a session started with `--prompts ask`.
- Only one session can exist at a time.
- No browsers come installed. Chrome and Firefox come from Homebrew casks, so they land in `/Applications/Google Chrome.app` and `/Applications/Firefox.app`, where the registrar looks for them.
- Nothing can connect in. The LAN Ollama server (10.0.3.20:11434) is reached through an SSH reverse tunnel and appears as `127.0.0.1:11434` on the Mac, which matches Local mode's default `http://localhost:11434`. Use qwen3.5:27b, never the dictfmt models.
- The build is ad-hoc signed and not notarized. Notarization is a later session.
- Gmail: the user signs in by hand over the desktop view. Drafts only, never send; discard the drafts afterwards.
- Session kit, all in `scripts/mac/`. `session.sh` runs on the Linux machine from the repository root and drives NoMac: `start 1|2`, `tunnel`, `prepare`, `auto`, `pull`, `status`, `handoff`, and `stop-block1`; each can be run again safely. On the Mac, `prepare.sh` installs Chrome and Firefox (Homebrew casks, skipped when present), the .NET SDK from `global.json` and NuGet packages into `~/drive/.cache/juvin-kit`, and Node 24 if missing; builds the extension and the dmg, installs `Juvin.app` to `/Applications`, writes kit host manifests for both browsers, and checks that `127.0.0.1:11434/v1/models` answers. It prints a timed summary and skips any step whose result is already there. `run-automated.sh` runs the automated rows side by side into `artifacts/mac-results/<UTC timestamp>/`: per row a `.log`, a `.status` (pass, fail, or skip), and `.notes`, plus `summary.md`, a table by row id. `run-automated.sh --list` prints the rows and `--only M-10,M-23` reruns some of them. The GitHub workflow `macOS session kit` (`.github/workflows/mac-session-kit.yml`) runs both on the free macOS runner with a stub model server (`openai-stub.py`), which is how the kit itself is tested.
- The kit's manifests are the app's with the description "Juvin browser connector (session kit)", so the wizard shows them as Needs update and M-10 can tell them from the ones Register writes. The browser rows (M-12) need them before anyone presses Register.
- NoMac turns Gatekeeper off on its Macs unless a session starts with `--gatekeeper on` (the CLI's default access is `gatekeeper: off`; the flag is not in `nomac start --help`). `session.sh start 1` passes it.

### macOS facts the rows rely on

- Data folder: `~/Library/Application Support/Juvin` (`Environment.SpecialFolder.LocalApplicationData` on .NET 10). `XDG_DATA_HOME` has no effect on macOS. Each session is a fresh VM, so use the real folder.
- Runtime files: `Runtime/control.sock`, `Runtime/connector.sock`, and the persistent `Runtime/control.lock` and `Runtime/connector.lock`. macOS always uses the data folder, never `XDG_RUNTIME_DIR`.
- Manifests: `com.juvin.host.json` in `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` and `~/Library/Application Support/Mozilla/NativeMessagingHosts/`. Chrome origin `chrome-extension://okfcbcoboinjniofikfnmllkbaeadelo/`, Firefox id `extension@juvin.app`. The manifest `path` is `juvin-host` next to the app executable (or `JUVIN_HOST_PATH` when set).
- LaunchAgent: `~/Library/LaunchAgents/com.juvin.app.plist`. Keychain items: generic passwords under service `Juvin`. No code in the MVP stores one.
- Juvin writes no log file. To keep its output and still let macOS treat the app as its own process for Accessibility, start it with `open --stdout <file> --stderr <file> /Applications/Juvin.app`. If you start the binary from a shell, Accessibility trust belongs to the terminal and a trust check means nothing.
- The default shortcut on macOS is the physical Control+Shift+F (the extension's suggested key is `MacCtrl+Shift+F`), not Command: Command+Shift+F belongs to the browsers and other apps.
- Scripted key presses: `osascript -e 'tell application "System Events" to key code 3 using {control down, shift down}'` presses Control+Shift+F (key code 3 is F, 16 is Y, 6 is Z). The hook sees these the same way it sees the desktop view's keys.

## Session plan

Two blocks, run one after the other on two separate sessions, all from the Linux machine in the repository root. Times are estimates for a first session, from the kit's runs on the GitHub macOS runner plus the downloads a fresh Mac needs; a rerun of `prepare` takes under a minute.

### Block 1: honest prompts (short)

| Step | Command or action | Time |
| --- | --- | --- |
| 1 | `scripts/mac/session.sh start 1`: `nomac start --prompts ask --gatekeeper on --drive none --max-duration 2700 --max-cost 1`, then `nomac ssh-config` and `nomac sync .` | 3 to 5 min |
| 2 | `nomac desktop`, then M-01 and M-02 by hand, nothing else; record the results by hand, since nothing persists | 15 min |
| 3 | `scripts/mac/session.sh stop-block1`, the only kit command that stops a Mac; it refuses unless the kit started this session as block 1 | 1 min |

Block 1 needs a dmg it can download in Safari: a prerelease asset, or any URL the Mac can reach. `curl` adds no quarantine attribute, so a curl download does not exercise Gatekeeper. If no dmg is published yet, run block 2 first, publish the dmg it builds (M-03), and run block 1 afterwards.

Moved out of block 1 to keep it short:

- The hotkey before trust (M-15) runs in block 2 after `tccutil reset Accessibility`, reading the app's own state.
- The Keychain access prompt cannot be checked in this build (see "Not covered").

### Block 2: everything else

| Step | Command or action | Time |
| --- | --- | --- |
| 1 | `scripts/mac/session.sh start 2`: default prompts, `--max-duration 14400 --idle-timeout 3600 --max-cost 5`, then `nomac ssh-config` and `nomac sync .` | 3 to 5 min |
| 2 | `scripts/mac/session.sh tunnel`: `ssh -N -R 11434:10.0.3.20:11434 nomac` in a reconnecting loop, pid in `~/.local/state/juvin-mac-kit/tunnel.pid`; it waits until the Mac answers at `127.0.0.1:11434` | 1 min |
| 3 | `scripts/mac/session.sh prepare` | 5 to 10 min (the runner, with the browsers already there, takes 1.5) |
| 4 | `scripts/mac/session.sh auto`, which pulls `artifacts/mac-results` back when it ends. Meanwhile open `nomac desktop` and sign in to Gmail in Chrome and Firefox for M-28 and M-29, but leave Juvin and its wizard alone | 3 to 5 min (1.5 on the runner) |
| 5 | The by-hand rows, in this order: M-25, M-12's popup check, M-13 to M-22, M-07, M-27, M-28, M-29, M-23, M-09, M-24 | 2 to 3 hours |
| 6 | `scripts/mac/session.sh auto --only M-10` after M-25's Register, and `--only M-23` after the switch in M-23; also `--only M-26` once `JUVIN_SENTRY_DSN` is a NoMac secret if M-26 skipped | 2 min each |
| 7 | `scripts/mac/session.sh handoff` | 2 min |

The session stops at its spend cap and after the idle timeout. The tunnel's SSH connection counts as activity, so while it is up the Mac runs to its time limit. Block 2 ends with a hand-off, not a stop.

What `auto` runs, as `run-automated.sh` groups them:

| Group | Rows | Runs |
| --- | --- | --- |
| Before the app starts, side by side | M-04 then M-04-mac; M-08 (reads M-04's result); M-30; M-03 then M-26 | from the start; M-03 and M-26 each refuse to run beside a running Juvin |
| With the app running, side by side | M-05; M-10; M-23; M-11, M-06, M-12-chrome, M-12-firefox one after another | once M-03 and M-26 end, the kit starts `/Applications/Juvin.app` with `open -g --stdout <results>/juvin-app.log` and leaves it running |
| Checks of by-hand actions | M-10 (after Register in M-25), M-23 (after the switch) | they skip with a reason until then; rerun them with `--only` |

The browser rows read the log of a Juvin the kit started. If Juvin was started another way, they skip and say so: quit it and rerun them with `--only`.

### Hand-off (end of block 2, do not stop the Mac)

`scripts/mac/session.sh handoff` does steps 2 to 4:

1. Leave Juvin installed and running, with Chrome and Firefox open and the extension connected.
2. `nomac pull` the results folder (`artifacts/mac-results`).
3. `nomac status` for the time left; with under an hour left it runs `nomac extend 60`, which adds an hour, up to 8 hours in all. When the stop time cannot be read it warns and goes on.
4. Print the desktop link (`nomac desktop --no-open`) and the session password (`nomac password`), and say that the user owns stopping it with `nomac stop`. It never stops the Mac, and it says when the tunnel is still up.

## Rows

A is automated, H is by hand. Block is 1 or 2.

### Packaging and first run

**M-01** (mvp-scope Phase 5 item 5; H; block 1). Gatekeeper on the downloaded ad-hoc dmg.
- Steps: download the dmg in Safari. Check that `xattr -p com.apple.quarantine ~/Downloads/<dmg>` prints a value. Open the dmg, drag Juvin to Applications, and double-click it in Applications.
- Pass: macOS refuses the first open with its unverified-developer dialog. System Settings, Privacy and Security then offers Open Anyway, and after confirming it the app starts and the wizard opens. A second double-click opens the app with no dialog.
- Record: the exact dialog text and buttons. Record also whether Accessibility was requested at this first start, before any wizard button: libuiohook may ask on its own. Leave the app running for M-02.

**M-02** (mvp-scope Phase 5 item 5, global-hotkey spike; H; block 1). The Accessibility row, from untrusted to trusted.
- Steps:
  1. Before granting anything, read Home and the wizard's Check your shortcut step (step 4). Go to the Setup checklist (step 5) and press Open Accessibility settings.
  2. Turn on Juvin in the list that opens, entering the account password if asked. Return to the wizard and press Check again.
  3. Open Settings, Shortcuts, press Test, and then press Control+Shift+F.
- Pass:
  - Before trust: Home reads "Control+Shift+F, not listening", and the checklist has one row, "Accessibility permission", Recommended, reading "Juvin needs Accessibility permission to listen for the rewrite shortcut." The button shows macOS's own Accessibility dialog for Juvin (`AXIsProcessTrustedWithOptions`) and opens Privacy and Security, Accessibility. The row then says "Turn on Juvin in the list, then check again."
  - After trust: the row reads "Juvin can listen for the rewrite shortcut.", Shortcuts reads "Listening through System keyboard hook.", and Home reads "Control+Shift+F, listening". Test shows Detected. If the row offers Restart Juvin instead, press it; the reopened Juvin must reach the same state (see M-15).
- Record: whether Juvin appeared in the list before the button was pressed. Then `scripts/mac/session.sh stop-block1`.

**M-03** (packaging-checklist macOS section; A; block 2). Build the dmg and run its smoke test as that section says. Then install the dmg into `/Applications`.
- Kit: `prepare.sh` builds the dmg into `dist/` and installs it; row `M-03` runs `scripts/smoke-test-mac-dmg.sh` and the checks below on the installed app and writes `M-03.codesign.txt` and `M-03.spctl.txt`.
- Pass: the smoke test passes. `codesign -dv /Applications/Juvin.app` reports `Signature=adhoc`. `defaults read /Applications/Juvin.app/Contents/Info CFBundleIdentifier` prints the bundle id that M-15 uses. `ls /Applications/Juvin.app/Contents/MacOS` lists the app executable and `juvin-host`.
- Record: the output of `spctl -a -vv /Applications/Juvin.app`.

**M-04** (mvp-scope Phase 1; A; block 2). `dotnet test Juvin.slnx` on macOS 26 arm64.
- Kit: row `M-04` runs `dotnet test Juvin.slnx -c Release` with TRX output and fails on a failed test or a skip reason other than "Windows only." and "Linux only.". Row `M-04-mac` reruns `tests/Juvin.Platform.Tests` filtered to the Mac, Keychain, LaunchAgent, Unix socket, and SharpHook tests and requires the Mac classes to run and pass. Both rows first unlock the login Keychain with `security unlock-keychain -p "$NOMAC_MAC_PASSWORD"`, since over SSH it is locked and the Keychain tests fail with -25308; without the variable they note it, and in CI mode the runner's Keychain is already unlocked.
- Pass: every test passes, with only the Windows-only and Linux-only tests skipped. `KeychainTests.SetGetOverwriteAndDelete_RoundTrip` and `MacAccessibilityPermissionSetupTaskTests` run and are not skipped.

**M-05** (PRIVACY.md, connector-protocol; A; block 2). The data folder and the runtime files, with the app running.
- Kit: row `M-05` checks the first two points. The Privacy and data folder and the sockets after Exit stay by hand (M-21's Exit), since a kill is not the menu's Exit.
- Pass:
  - `stat -f '%Sp %N'` shows `drwx------` on `~/Library/Application Support/Juvin` and on its `Secrets`, `Logs`, and `Runtime` folders.
  - `Runtime/` holds `control.sock`, `connector.sock`, `control.lock`, and `connector.lock`, and no `secret.key` exists.
  - Privacy and data shows the same folder path and its Open folder button opens it in Finder.
  - After Exit from the menu bar the two `.sock` files are gone and the two `.lock` files stay.

**M-06** (Program.cs; A; block 2). One instance, with the app running.
- Kit: row `M-06` checks the process count and the exit codes; that the window comes forward and `--background` shows none is seen over the desktop.
- Steps: run `open -n /Applications/Juvin.app`. Then run the bundle's executable with `--trigger`, and then with `--background`.
- Pass: the second launch brings up the running window and exits, and `pgrep -x juvin | wc -l` stays 1. `--trigger` exits 0. `--background` exits 0 with no window.

**M-07** (Phase 5 item 5; H; block 2). App Translocation refusal.
- Steps: copy Juvin.app to `~/Downloads` and set `xattr -w com.apple.quarantine "0081;$(printf %x "$(date +%s)");Safari;" ~/Downloads/Juvin.app`. Quit the installed app, run `open ~/Downloads/Juvin.app`, and press Register for Chrome in Browser connector.
- Pass: `ps -o command= -p <pid>` shows a path under `/AppTranslocation/`, and Register shows "macOS is running Juvin from a temporary copy, so the browser could not find it after a restart. Move Juvin to the Applications folder, open it from there, and register again." No manifest is written.
- If the path is not translocated with auto-answered prompts, record that; the check then needs block 1. Delete the copy afterwards.

### Secrets and data

**M-08** (CLAUDE.md "Keychain on macOS"; A; block 2). The Keychain round trip is M-04's `KeychainTests`, against the real login Keychain under a throwaway service. Also run `security find-generic-password -s Juvin`.
- Kit: row `M-08` reads M-04's TRX for the test and passes when `security` exits 44 (not found).
- Pass: the test passes and the `security` call finds nothing, because the MVP stores no secret.

**M-09** (PRIVACY.md "Delete all data"; H; block 2). Run this with Launch at login on (after M-23), then press Delete all data in Privacy and data and confirm it.
- Pass: `~/Library/LaunchAgents/com.juvin.app.plist` is gone, `settings.json` is back to its defaults, and the wizard runs at the next start. Turn Launch at login back on for M-24.

### Native messaging and extension

**M-10** (native-messaging spike, mvp-scope Phase 3; H then A; block 2). Registration.
- Steps: in the wizard's Connect your browser step (step 3), press Register for each browser. `prepare.sh` wrote the kit's manifests, so the rows read Needs update, and Update must replace them. Then run `scripts/mac/session.sh auto --only M-10`.
- Kit: row `M-10` checks both files below and passes only when the description is the app's; with the kit's manifests still there it checks their shape and skips.
- Pass:
  - Detected browsers lists Firefox and Google Chrome only, and both rows read Registered.
  - Both `com.juvin.host.json` files have `name` `com.juvin.host`, `type` `stdio`, and `path` set to the bundle's `juvin-host`. Chrome's has `allowed_origins` `["chrome-extension://okfcbcoboinjniofikfnmllkbaeadelo/"]` and Firefox's has `allowed_extensions` `["extension@juvin.app"]`.
  - `python3 -m json.tool` accepts both.

**M-11** (connector-protocol Transport; A; block 2). The host round trip without a browser.
- Steps: write one framed `hello` (4-byte little-endian length, then the JSON) to the bundle's `juvin-host` on stdin, and read one frame back. Then quit the app and run the host again.
- Pass: the frame read back is a `config` message with `v` 1. Without the app, the host exits non-zero.
- Kit: row `M-11` sends the `hello` with `scripts/mac/native-frame.py`. The run without the app is part of M-03's smoke test, which runs before the kit starts Juvin.

**M-12** (connector-checklist steps 4 to 8; A where run-automated.sh drives it, else H; block 2). The browser round trip in Chrome and Firefox. Load `extension/dist/chrome` unpacked in Chrome, and `extension/dist/firefox` with `npx web-ext run`.
- Pass, for each browser:
  - The app log shows `<browser> <version> connected`, and the popup's Test connection reports a round trip in milliseconds.
  - The test page capture logs `Captured 161 draft chars, 3 messages, EntireThread, adapter test-page`.
  - Quitting the browser logs `<browser> disconnected`, and `pgrep juvin-host` finds nothing.
- Record: the Chrome extension id shown on `chrome://extensions`, which must be `okfcbcoboinjniofikfnmllkbaeadelo`.
- Kit: rows `M-12-chrome` and `M-12-firefox` run `scripts/mac/browser-check.mjs` against a fresh profile and the log of the Juvin the kit started. Chrome: branded Chrome ignores `--load-extension`, so it starts with `--remote-debugging-port --enable-unsafe-extension-debugging` and loads `extension/dist/chrome` over CDP with `Extensions.loadUnpacked`, which must return the pinned id; the kit copies the registered manifest into the profile's own `NativeMessagingHosts` folder. Chrome commands cannot be fired from outside the browser, so its capture goes through `juvin --trigger` with the test page selected, which captures only while no system hook listens and Chrome has focus; otherwise the row notes the capture as not checked. Firefox: Marionette installs `extension/dist/firefox` as a temporary add-on, selects the test page's draft, and fires the command by dispatching a `command` event on the extension's key element, so its capture is checked every time. Both rows then quit the browser and check the disconnect line and that no new `juvin-host` is left. The popup's Test connection stays by hand.

**M-13** (extension/README, decision 6; H; block 2). The extension command.
- Steps:
  1. Check the command's key on `chrome://extensions/shortcuts` and on Firefox's Manage Extension Shortcuts page.
  2. Record a different desktop shortcut (Control+Option+J) so that only the browser answers Control+Shift+F. Press Control+Shift+F on the test page in each browser, then restore Control+Shift+F.
- Pass: both pages show Control+Shift+F (the manifest's `MacCtrl+Shift+F`) for "Rewrite the selected draft with Juvin". Each press in Chrome logs one `Captured ...` line and no `Coalesced duplicate press.` line. Firefox does not fire the command inside the draft (macOS binds Control+Shift+F to extending the selection); a press with focus outside a text field must fire it.
- Record: whether a browser menu item also binds Control+Shift+F and which one wins.

### Hotkey and Accessibility

**M-14** (hotkey-checklist steps 2 to 8; H; block 2). The SharpHook hook with the test page in Chrome.
- Pass:
  - Shortcuts reads "Listening through System keyboard hook." and Test shows Detected.
  - A press on the selected draft opens the action menu and logs one `Captured ...` and one `Coalesced duplicate press.`.
  - A press with Finder focused gives `browser-not-focused`. A press with the browsers closed gives `no-browser`.
  - Pressing the old chord while recording opens nothing. After recording Control+Option+J, the log shows `sharphook listens for the new shortcut.` with no `Hook did not report enabled in time.`, Test shows Detected for Control+Option+J, and Control+Shift+F no longer fires. Restore Control+Shift+F afterwards.
  - `--trigger` logs `Trigger ignored while <backend> listens.`
  - Three draft words grepped in the log find nothing.

**M-15** (moved from block 1; H; block 2). Hotkey before trust.
- Steps: quit Juvin, run `tccutil reset Accessibility <bundle id from M-03>`, and start it with `open`. If the row reads Missing, turn Juvin on again in Privacy and Security, Accessibility, and press Check again.
- Pass, before trust: the Setup checklist row reads "Juvin needs Accessibility permission to listen for the rewrite shortcut.", and Shortcuts reads "System keyboard hook could not start: Allow Juvin under Accessibility in System Settings. If it is already on, restart Juvin.". A press does nothing and the app does not crash.
- Pass, after trust: either the row turns Satisfied and the hook listens without a restart, or (as on macOS 26.6.2 on October 20, 2026, when libuiohook's CoreGraphics check kept its first answer) the row reads "Juvin has the permission, but the rewrite shortcut did not start." with a Restart Juvin button. That button quits Juvin and opens it again within a few seconds, and the reopened app reads Satisfied, listens, and logs no `Hook did not start (ErrorAxApiDisabled)`.
- Record: which of the two happened.
- If NoMac grants trust again at launch (the row reads Granted straight away), record that. This row then rests on M-02.

**M-16** (SharpHookHotkeyBackend.ModifiersOf; H; block 2). Modifier mapping.
- Steps: send Control+Shift+F (`key code 3 using {control down, shift down}`) and then Cmd+Shift+F (`{command down, shift down}`).
- Pass: only Control+Shift+F fires, because the default names the physical Control key on macOS.

**M-17** (hotkey-checklist "Not yet covered", mvp-scope Phase 4 slice 4; H; block 2). The physical key.
- Steps: add the German input source and switch to it. Record a shortcut by pressing Control+Shift and the key in the Y position (labelled Z). Send `key code 16` (physical Y) and `key code 6` (physical Z) with Control and Shift.
- Pass: `settings.json` stores `rewriteShortcut` `Ctrl+Shift+Z` and `rewriteShortcutPhysicalKey` `Y`. The hook fires on key code 16 only, and no longer on Control+Shift+F. Restore the default and the US layout afterwards.

### Panel and action menu

**M-18** (mvp-scope section 2.8, connector-protocol `applied`; H; block 2). The in-page menu on the test page in Chrome and Firefox.
- Pass:
  - The menu opens beside the draft, and keys 1 to 5 run their actions.
  - After an apply, the note reads "Draft rewritten. Press Cmd+Z to undo.", and a real Cmd+Z restores the draft.
  - Escape and a click outside close the menu.
  - Right-click on the mark lists every action plus Review.

**M-19** (overlay-panel spike, connector-checklist panel results; H; block 2). The Review panel.
- Steps: open Review from the right-click list.
- Pass:
  - The panel opens at the top right of the main screen's working area, below the menu bar, streams the result, and stays above Chrome while Chrome has focus. Avalonia's `Topmost` maps to a floating level on macOS.
  - Apply hides the panel and replaces the selection, and focus returns to Chrome so that Cmd+Z works without a click.
  - Typing in the draft before Apply gives "Draft changed" with Copy and Recapture. After Copy, `pbpaste | wc -c` matches the result's length.
  - Escape, then the shortcut again, reopens the panel in the same corner.

**M-20** (MacPanelWindowSupport; H; block 2). Spaces and full screen.
- Steps: put Chrome in full screen (its own Space) and open Review there. Switch Spaces with Control+Right.
- Pass: the panel shows over the full-screen browser without switching Spaces, and after Control+Right it is on the new Space when shown again. Console shows no `NSInternalInconsistencyException` about collection behavior. Record what Stage Manager does if it is on.
- Record: the Juvin Dock icon leaves while the panel shows (the app is an accessory app then) and returns once it hides, also with the main window open.
- If it fails again, `scripts/mac/panel-harness.swift` reproduces the show strategies outside Juvin; judge each variant by a screenshot. On October 20, 2026 it showed that the panel joins a full-screen Space only if its collection behavior is set after the switch to the accessory policy.

### Tray and window

**M-21** (MenuBarTrayHostProbe; H; block 2). The menu bar and the window.
- Pass:
  - The menu bar shows the Juvin mark, and its menu has Open Juvin and Exit.
  - General has no "No system tray was found" hint.
  - With Close to tray on, closing the window leaves Juvin running in the menu bar, and Open Juvin brings the window back. Exit quits the app.
- Record: whether Juvin shows in the Dock while its window is hidden, and what Cmd+Q does.

### Launch at login

**M-22** (LaunchAgentLoginItem.IsSupported; H; block 2). Start a Debug build with `dotnet run --project src/Juvin.App` and open General.
- Pass: the Launch at login switch is disabled with "Juvin cannot start itself at login on this computer.", because the Debug executable is not inside an `.app` bundle, so the agent would not start the installed app.

**M-23** (packaging-checklist launch at login steps 1 and 2; H then A; block 2). With the installed app, turn on Launch at login, then run `scripts/mac/session.sh auto --only M-23`.
- Kit: row `M-23` runs the checks below and skips while there is no plist. In CI mode the kit turns Launch at login on in `settings.json` before it starts Juvin, so the app writes the plist at startup.
- Pass: the switch stays on with no error line. `plutil -lint ~/Library/LaunchAgents/com.juvin.app.plist` prints OK, and `plutil -p` shows:
  - `Label` `com.juvin.app`
  - `ProgramArguments` set to the bundle's executable and `--background`
  - `RunAtLoad` 1, `LimitLoadToSessionType` `Aqua`, `AbandonProcessGroup` 1 (so a Restart Juvin helper outlives the agent's Juvin), and `JuvinManaged` 1
- Record: whether macOS posts a background-items notification, and whether Juvin appears under General, Login Items and Extensions.

**M-24** (packaging-checklist launch at login steps 3 and 5; H; block 2; last before the hand-off).
- Steps: log out from the Apple menu, then log in again over the desktop view with the session password.
- Pass:
  - After the login, `ps -o command= -p "$(pgrep -x juvin)"` ends in `--background`, the mark is in the menu bar, no window is open, and the shortcut opens the action menu.
  - Turning the switch off removes the plist, and the next logout and login start nothing.
- Leave the switch off afterwards. Start Juvin again for the hand-off.
- If the desktop view does not survive the logout, record that and skip the second cycle.
- After a logout `nomac screenshot` can fail with "could not create image from display". Use `nomac ssh -- screencapture -x /tmp/s.png`, then `scp nomac:/tmp/s.png .` (the SSH alias `start` wrote).

### Setup wizard

**M-25** (mvp-scope section 4 setup checklist, connector-checklist step 7; H; block 2). The full wizard on the freshly installed app.
- Pass: the wizard opens in front of the Juvin window, also after `open -g`, and stays in front after a Dock icon click.
- Steps and pass, step by step:
  1. Welcome shows Send crash reports, on.
  2. Choose where rewrites run detects the server at `localhost:11434` through the tunnel and lists its models. Choose qwen3.5:27b.
  3. Connect your browser: see M-10.
  4. Check your shortcut shows Control+Shift+F and hears a press.
  5. The Setup checklist shows the one Accessibility row as satisfied.
  6. In Try it, Open sample email opens the test page in the browser and brings it to the front, and the three ticks follow a capture and an apply.
  7. You are ready reads "Select a draft in your browser and press Control+Shift+F to rewrite it."

### Crash reporting

**M-26** (mvp-scope Phase 6 slice 2; A on the Mac, then a check in Sentry; block 2).
- Steps: in a scratch copy of the repository, add the same temporary dispatcher throw as the October 16 check (its message holds a canary phrase and `/Users/agent`), and never commit it. Run the build with `JUVIN_SENTRY_DSN` set and let it crash. Then turn the switch off in Privacy and data and crash it again.
- Kit: row `M-26` does the Mac side. It copies the repository to `~/.juvin-kit/m26-scratch`, adds a throw on the UI thread after `StartHotkeysAsync` in `App.axaml.cs` (only when `JUVIN_KIT_CRASH=1`), builds it in Debug, and runs it twice with `hasCompletedOnboarding` set and `crashReportingEnabled` on, then off, restoring `settings.json` afterwards. Its notes give each crash's UTC time for the Sentry check, which stays by hand. It skips without `JUVIN_SENTRY_DSN` (set it as a NoMac secret) and while Juvin runs. The CI run uses a dead local DSN, so nothing reaches Sentry.
- Pass:
  - Sentry project `juvin` gets one new issue with the value `<redacted>`, frames with module and function names and no paths, `handled: no`, mechanism `ui-dispatcher`, a macOS os context, and an empty user. Neither the canary nor `/Users/agent` appears anywhere in the event JSON.
  - The run with the switch off adds no event.

### Acceptance scenarios

Whether each Phase 6 scenario from `acceptance-checklist.md` is rerun on the Mac:

1. Only the draft changes: rerun as M-28. The undo key (Cmd+Z) and the real key path are what differ on a Mac.
2. Collapsed thread: not rerun. The expansion is content-script logic, already proven in Gecko and Chromium. Run Gmail step 2 inside M-28 only if there is time.
3. Two drafts: not rerun. Editor routing is page and protocol logic that is the same on every OS.
4. Edited draft refused: covered by M-19's Draft changed check. The hash check itself is OS-independent.
5. Cancel or error: not rerun, except Copy through the macOS pasteboard (M-19). Escape and Apply never touch OS code.
6. Local failure, no external request: rerun as M-27. The process, its network stack, and the capture tools differ on macOS.
7. Offline: cannot be checked honestly here. Removing the default route also cuts the desktop view and the tunnel. It rests on the Linux run, which uses the same HTTP client path.

**M-27** (acceptance scenario 6; H; block 2).
- Steps:
  1. Set the server address to `http://127.0.0.1:11435`, where nothing listens.
  2. Start `sudo tcpdump -i pktap,all -k NP -nn -w ~/drive/results/s6.pcap 'not port 22'` before restarting Juvin. pktap tags each packet with its process, which stands in for the Linux bpftrace attribution. Check the syntax with `man tcpdump` and record any change.
  3. Restart Juvin, run an action, wait for the error card, press Try again, and wait for the second error card.
- Pass: the error card says the local model server at that address could not be reached and offers Try again, and the draft is unchanged. In `tcpdump -r ~/drive/results/s6.pcap -k NP`, every packet from `juvin` goes to 127.0.0.1:11435. Everything else belongs to other processes: the browsers, the desktop view's stream, `mDNSResponder`, and system daemons.
- Restore the address to `http://localhost:11434` afterwards.

### Local mode from the command line

**M-30** (mvp-scope Phase 2; A; block 2). A Local mode rewrite through the bundle's `juvin-cli` against the model server at `127.0.0.1:11434`.
- Kit: row `M-30` picks a small general model from `/v1/models` (gemma3:4b, then qwen3:8b, then llama3.1, never a dictfmt or embedding model; `JUVIN_KIT_MODEL` overrides it), sends a synthetic draft and thread with `--action clearer` and a scratch settings file, and records the model, the reply's length, and the time.
- Pass: `juvin-cli` exits 0 and prints a reply. It skips when nothing answers at `127.0.0.1:11434` (start the tunnel).

### Gmail

**M-28** (connector-checklist Gmail steps 1, 4, 5, and 6, acceptance scenario 1; H; block 2). Chrome, signed in by hand, with synthetic text. Press a real Control+Shift+F.
- Pass:
  - Step 1 logs `Captured <n> draft chars, <messages> messages, EntireThread, adapter gmail`.
  - Step 4: Make clearer by key 3 changes only the selected line, and Cmd+Z restores it.
  - Step 5 is refused with `draft-changed`.
  - Step 6 keeps the draft in place on screen while a collapsed conversation expands.
- Discard the drafts afterwards.

**M-29** (Gmail on Gecko under macOS; H; block 2). Firefox, signed in by hand.
- Pass: steps 1 and 4 of M-28 pass, including Cmd+Z. With a whole first line selected, step 1 captures exactly its characters, not the line break after it (Control+Shift+F must not extend the selection), and step 4 keeps the second line on its own line.

## Results

| Row | Date | Block | Build | Result | Notes |
| --- | --- | --- | --- | --- | --- |
| M-01, M-02 | | 1 | | not run | Waits for a published dmg |
| M-03 | Oct 20 | 2 | 52929fb | pass | Smoke test passed; installed app ad-hoc signed, `com.juvin.app`; `spctl` rejects it, as expected without notarization |
| M-04, M-04-mac | Oct 20 | 2 | #38 | pass | 1378 tests: 1219 passed, 159 skipped; macOS set 115: 111 passed, 4 skipped. The first run failed the Keychain test over SSH (locked keychain, -25308); the kit now unlocks it first |
| M-05 | Oct 20 | 2 | 52929fb | pass | Data folder and its Secrets, Logs, Runtime are drwx------; sockets and locks present; no `secret.key` |
| M-06 | Oct 20 | 2 | 52929fb | pass | One instance after `open -n`; `--trigger` and `--background` exit 0 |
| M-07 | Oct 20 | 2 | 52929fb | pass | Quarantined copy translocated after Open Anyway; Update shows the temporary-copy message; manifest unchanged |
| M-08 | Oct 20 | 2 | #38 | pass | Keychain round trip passed; no Juvin item left in the login Keychain |
| M-09 | Oct 20 | 2 | 52929fb | pass | Plist removed, settings back to defaults, wizard at the next start |
| M-10 | Oct 20 | 2 | 52929fb | pass | Both manifests written by the app with the right name, path, and allowlist |
| M-11 | Oct 20 | 2 | 52929fb | pass | Framed hello through `juvin-host` came back as `config` |
| M-12 | Oct 20 | 2 | 52929fb | pass | Chrome 155 and Firefox 158 connected and captured 161 chars from the test page; popup Test connection answered in 2 ms |
| M-13 | Oct 20 | 2 | 52929fb | pass | Chrome shows and fires Ctrl+Shift+F. Firefox does not fire the command inside a text field (macOS binds the key to extending the selection); documented, the key stays |
| M-14 | Oct 20 | 2 | 52929fb | pass | Hook listening; Chrome press captured once with one coalesced duplicate; Finder `browser-not-focused`; no draft words in the log |
| M-15 | Oct 20 | 2 | #38 | pass | Before trust: Failed with the Accessibility hint. After the grant the row offers Restart Juvin, and the relaunched app listens (fixed in #38; the first run needed a manual restart) |
| M-16 | Oct 20 | 2 | 52929fb | pass | Control+Shift+F fires, Cmd+Shift+F does not |
| M-17 | Oct 20 | 2 | 52929fb | pass | German layout: recorded Shift+Super+Z at physical key Y; only that key fires |
| M-18 | Oct 20 | 2 | #38 | pass | Keys run actions, Cmd+Z restores, Escape and click outside close. The right-click list was cut off in a short window; fixed in #38 |
| M-19 | Oct 20 | 2 | #38 | pass | Panel top right; Draft changed with Copy and Recapture; Apply replaced the selection. Focus now returns to the browser after Apply (#38) |
| M-20 | Oct 20 | 2 | #38 | pass | Failed on 52929fb (panel opened on the desktop Space). After #38 the panel shows over full-screen Firefox, again on a second show, and Apply and Cmd+Z work |
| M-21 | Oct 20 | 2 | 52929fb | pass | Menu bar mark with Open Juvin and Exit; close keeps Juvin running; Cmd+Q quits |
| M-22 | Oct 20 | 2 | #38 | pass | Failed on 52929fb (Launch at login offered for a Debug build). After #38 the switch is unavailable there |
| M-23 | Oct 20 | 2 | 52929fb | pass | Plist lints with Label, ProgramArguments, RunAtLoad, Aqua, JuvinManaged. Login Items shows "juvin, Item from unidentified developer" until there is a Developer ID |
| M-24 | Oct 20 | 2 | 52929fb | pass | After login Juvin runs in the background and the shortcut works; with the switch off nothing starts |
| M-25 | Oct 20 | 2 | #38 | pass | Wizard steps 1 to 7 pass. On 52929fb the wizard opened behind the main window; fixed in #38 |
| M-26 | | 2 | | not run | Needs `JUVIN_SENTRY_DSN` as a NoMac secret |
| M-27 | Oct 20 | 2 | 52929fb | pass | Unreachable server card with Try again, draft unchanged; `juvin` sent only SYNs to 127.0.0.1:11435 |
| M-28 | Oct 23 | 2 | #38 | pass | Oct 20: steps 1, 4, 5, 6 passed, but a whole-line Apply in Chrome left an empty line. Oct 23 after #38: first and last line leave no empty line, one Cmd+Z restores |
| M-29 | Oct 23 | 2 | #39 | pass | Oct 23 on #38: Control+Shift+F grew the Firefox selection by a character before the capture, so Apply joined two lines. After #39 the selection keeps its length on the test page and, with all sites allowed, on another site from the first press |
| M-30 | Oct 20 | 2 | 52929fb | pass | `juvin-cli` rewrite with gemma3:4b returned 167 chars |

## Not covered in this session

- Notarization and stapling, and Gatekeeper on a notarized dmg.
- Intel Macs, and macOS versions other than 26.6.2.
- Edge, Brave, and Chromium. The registrar detects and registers them, but only Chrome and Firefox are installed here.
- Store builds of the extension.
- The Keychain access prompt on an ad-hoc build. No code path in the MVP stores a secret (`ISecretStore` has no consumer), so the app never touches the Keychain. A prompt after an update replaces the ad-hoc signature can only be checked once a provider stores a key.
- Acceptance scenario 7 (offline), for the reason in its line above.
