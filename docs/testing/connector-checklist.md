# Browser connector manual checklist

Run after any change to `juvin-host`, the connector socket, `ConnectorService`, or the extension's background script. Unit tests cover framing and protocol logic; this checklist covers what only a real browser exercises: host launch, the OS endpoint, and the capture round trip.

## Steps (any OS)

1. Build the solution and the extension (`dotnet build Juvin.slnx`, `npm run build` in `extension/`).
2. Start the app with `XDG_DATA_HOME` (or the Windows profile) pointed at a scratch directory. The log must show `Browser connector ready at ...`.
3. Register the host: press Register for each browser in the setup wizard's Browser connector step or in the Browser connector section of settings. Each row must then read Registered. The dev scripts (`scripts/dev/register-host.sh` on Linux, `scripts/dev/register-host.ps1` on Windows) remain an alternative. On Ubuntu with Snap Firefox, if registration did not pre-approve the host, run `flatpak permission-set webextensions com.juvin.host snap.firefox yes` or expect the portal's consent dialog on first connect.
4. Load the extension: `npx web-ext run --source-dir dist/firefox` for Firefox; `dist/chrome` as an unpacked extension in Chrome or Edge. The app log must show `<browser> <version> connected`.
5. Open the popup and press Test connection. It must report a round trip in milliseconds.
6. Open the test page from the popup, select the draft, press the shortcut. The app log must show `Captured 161 draft chars, 3 messages, EntireThread, adapter test-page` and nothing from the draft or thread text.
7. Open the setup wizard from Settings and go to step 6, Try it. Press Open sample email: the test page must open in a new tab of the browser and the window must come to the front, and "Sample email opened" must tick. Select the draft and press the shortcut: the action menu must appear beside the draft and "Draft captured" must tick. Pick an action: the draft must change and "Rewrite done" must tick. Review from the right-click list, with an action picked in the panel, must tick it too.
8. Quit the browser. The app log must show `<browser> disconnected` and no `juvin-host` process may remain.

## Gmail steps (any OS)

Run after steps 1 to 5, signed in to a Gmail account with test conversations only. Use synthetic text; the log lines below carry counts, never content.

1. Open a conversation of three or more messages, all expanded. Reply, type a two-line draft, select part of it, and press the shortcut. The log must show `Captured <n> draft chars, <messages in the thread> messages, EntireThread, adapter gmail`.
2. Open a conversation long enough to show an "N older messages" bundle. Reply, select, and press the shortcut. Gmail must expand the whole conversation within about 3 seconds and the log must show `EntireThread` with every message counted.
3. Open a reply in a conversation and a new message window at the same time. Select text in the new message and capture: the log must show `0 messages, DraftOnly`. Select text in the reply and capture: the log must show the conversation's messages.
4. With a reply draft captured, apply a replacement for that capture. The selected text must change in place, and Ctrl+Z (Cmd+Z on macOS) must restore the original draft.
5. Capture again, type one character in the draft, then apply. The apply must be refused with `draft-changed` and the draft must stay as typed.

Steps 4 and 5 run through the rewrite panel (Phase 4): pressing the shortcut on a selected draft opens the panel at the top-right of the primary screen and the default action's result streams in. For step 4 press Apply once the result is complete: the panel hides, the selection is replaced, and Ctrl+Z (Cmd+Z on macOS) in the editor restores the draft. For step 5 wait for the result, type one character in the draft, then press Apply: the panel must show "Draft changed" with Copy and Recapture, Apply must stay disabled, and the draft must stay as typed.

### Gmail results

| Date | OS | Browser | Full thread | Collapsed thread | Two drafts | Apply and Undo | Edited draft refused |
| --- | --- | --- | --- | --- | --- | --- | --- |
| October 10, 2026 | Fedora 44, GNOME Wayland | Zen 1.22.3b (Flatpak, Gecko 156) | yes, 3 of 3 | yes, 6 of 6 after one Expand all click (265 ms; 3.2 s with the tab in the background) | yes | yes | yes |
| October 10, 2026 | Windows 11 | Chrome 155 | yes, 3 of 3 | yes, 6 of 6 after one Expand all click (433 ms) | yes | yes, real Ctrl+Z | yes |
| October 10, 2026 | Ubuntu 25.04, GNOME Wayland | Zen 1.23.2b (Flatpak, Gecko 157) | yes, 3 of 3 | yes, 6 of 6 after one Expand all click (429 ms; 269 to 410 ms with the popup tab selected) | yes | yes | yes |

The Ubuntu row ran in Flatpak Zen on a copy of the signed-in Zen profile from the Fedora desktop, because no browser on that VM held a Gmail session; copying only the Google cookies into the Snap Firefox profile signed in for one page load and was then signed out, so the Snap Firefox route is covered by the connector rows only. On Windows a real Ctrl+Shift+Y press fired the extension command on both Gmail drafts. The October 10 Linux run found and fixed two things only real Gmail showed: the inline reply editor lives inside the row of the message it answers, so that row must stay a message, and the Expand all wait must use the wall clock because a background tab stretches each timer. Confirmed on the same run: `h2[data-thread-perm-id]` and `h2.hP` both match the subject, every row has `span.g3[title]` whose title (with a U+202F space before AM or PM) parses in Gecko, the "N older messages" bundle is a `span[role="button"]` with an `aria-label`, `[aria-label="Expand all"]` matches and `[data-tooltip="Expand all"]` does not, every row has `span.gD[email]`, and Undo after `insertText` restores the draft hash exactly.

### Rewrite panel results

Run with the test page or Gmail. Hide and show: close the panel with Escape or its close button, press the shortcut again, and check that it reappears in the same corner above the browser. Both rows below ran on the bundled test page with qwen3.5:27b on the LAN server; the Fedora buttons were driven through AT-SPI and the command fired through Marionette, the Windows run used real key presses and UI Automation from the interactive session. Gmail was not rerun for the panel: the Windows Chrome profile had been signed out again.

| Date | OS | Browser | Panel shows and streams | Switch action | Follow-up | Changes view | Apply and Undo | Edited draft refused | Hide and show again |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| October 11, 2026 | Fedora 44, GNOME Wayland | Firefox 156 (RPM, XWayland) | yes, qwen3.5:27b on the LAN server | yes | yes, turn 2 of the same session | yes | yes | yes, Draft changed with Apply disabled | yes, three cycles, above and sticky re-asserted |
| October 11, 2026 | Windows 11 | Chrome 155 | yes, real Ctrl+Shift+F, topmost above maximized Chrome at (804,16) | yes | yes | yes | yes, real Ctrl+Z restored the draft | yes, Draft changed with Apply disabled | yes, real Escape, reopened at the same place and still topmost |

### Action menu results

The in-page menu (section 2.8 of `mvp-scope.md`) replaces the panel for a capture from the shortcut. Both rows below ran with qwen3.5:27b on the LAN server and a custom "Pirate voice" action in the settings; keys were real presses (ydotool on Ubuntu through the GNOME desktop-shortcut route, SendKeys on Windows through the SharpHook hook), clicks on the mark and bubbles went through the browser's own input path (Marionette pointer actions, CDP mouse events), and the app log was checked for draft words after each run.

| Date | OS | Browser | Page | Key and click actions apply, Ctrl+Z restores | What am I missing? and follow-up | Ask for a change | Right-click list, custom action by keyboard | Review opens the panel | Edited draft refused, Recapture | Escape and click outside | Try it step |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| October 14, 2026 | Ubuntu 25.04, GNOME Wayland | Zen 1.23.2b (Flatpak, Gecko 157) | test page and Gmail reply (3 messages, EntireThread) | yes | yes | yes | yes, facts chips then the catalog and Review | yes, idle | yes | yes | driven through AT-SPI; ticks checked by eye |
| October 14, 2026 | Windows 11 | Chrome 155 | test page | yes, one coalesced duplicate per press | yes | yes | yes | yes, idle | yes | yes | not run |

Gmail on Windows was not run: the Chrome profile there was signed out again, and a copy of it under a scratch user data directory (remote debugging is refused on the default one) was signed out too.

## Results

| Date | OS | Browser | Route | Connect | Ping | Capture |
| --- | --- | --- | --- | --- | --- | --- |
| October 9, 2026 | Fedora 44, GNOME Wayland | Firefox 156 (RPM) | native | yes | 14 ms | yes |
| October 9, 2026 | Fedora 44, GNOME Wayland | Chrome 154 | native | yes | 1 ms | yes |
| October 9, 2026 | Windows 11 | Chrome 155 | named pipe | yes | 15 ms | yes |
| October 9, 2026 | Windows 11 | Edge 154 | named pipe | yes | 3 ms | yes |
| October 9, 2026 | Windows 11 | Firefox 157 | named pipe | yes | not run | not run |
| October 9, 2026 | Ubuntu 25.04, GNOME Wayland | Firefox 142 (Snap) | WebExtensions portal, pre-approved | yes | not run | not run |
| October 9, 2026 | Ubuntu 25.04, GNOME Wayland | Chrome 155 | native | yes | 4 ms | yes |
| October 10, 2026 | Fedora 44, GNOME Wayland | Zen 1.22.3b (Flatpak) | host inside the sandbox, per-app runtime socket | yes | 4 ms | yes |

The Windows and Snap Firefox rows show connect only: the Marionette driver used on Fedora (see below) did not start a session on Firefox 142 under Snap, and the Windows Firefox run was not scripted beyond loading the add-on. The popup and capture code is the same on every OS and was exercised in Firefox on Fedora and in Chrome and Edge on each OS. Not yet covered: Flatpak Firefox on Fedora (the Zen row used the same route) and macOS (no machine available).

Settled October 10, 2026 on Fedora: a real Ctrl+Shift+Y press in Firefox 156 (run under XWayland so XTEST could deliver the key) opened the Library window's Downloads view and the extension command did not fire, so Firefox's built-in binding wins on Linux and the default shortcut has to change. The same press with the command bound to Ctrl+Shift+F fired the command and logged the test-page capture; Firefox binds no Ctrl+Shift+F of its own on any OS. On October 11, 2026 a real Ctrl+Shift+F press (SendKeys from the interactive session) fired the command in Chrome 155 on the Windows VM and a real Ctrl+Shift+Y did not. Wayland-native Firefox and Zen receive no injected keys at all (ydotool and XTEST both miss them), so that route needs `MOZ_ENABLE_WAYLAND=0`, `DISPLAY=:0`, and the `.mutter-Xwaylandauth.*` file, then `xdotool windowfocus --sync` and `xdotool key`.

## Notes for automation

- Windows: browsers and the app must run in the interactive session (the pipe name carries the session id). From SSH, use a one-shot scheduled task with `/it`, and set `DOTNET_ROOT` when the .NET 10 runtime is not the machine-wide install.
- Ubuntu over SSH: export `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, `DBUS_SESSION_BUS_ADDRESS`, `DISPLAY=:0`, and `XAUTHORITY` from `/run/user/1000/.mutter-Xwaylandauth.*`; set `TMPDIR=~/snap/firefox/common/tmp` for web-ext with Snap Firefox.
- Chrome ignores `--load-extension`; load the unpacked build over the DevTools protocol (`Extensions.loadUnpacked`) and trigger the command with `chrome.commands.onCommand.dispatch("rewrite")` in the service worker.
- Firefox: WebDriver BiDi refuses to navigate to extension pages. Launch with `--args=-marionette --args=-remote-allow-system-access` (test automation only, never a user requirement) and drive Marionette from the chrome context: `WebExtensionPolicy.getByID(...)` gives the popup and test page URLs to open in tabs, and dispatching a `command` event on the key inside `ext-keyset-id-extension_juvin_app` fires the extension command. For the Gmail steps 4 and 5, open the popup URL in a tab and send `juvin.capture` and `juvin.apply` through `window.wrappedJSObject.browser.tabs.sendMessage` from that tab's content context; the plain `window.browser` is hidden from the Marionette sandbox.
- Zen's initial empty tab (`zen-empty-tab`) is invisible to the extension's `tabs.query`, so a capture from a Gmail page loaded into it is declined with `no-active-tab`; open Gmail in a normal tab.
- Flatpak Zen on a signed-in profile: copy the profile (without `lock` and `.parentlock`) and pass `--firefox flatpak:app.zen_browser.zen --firefox-profile <copy> --keep-profile-changes`; the host must be the self-contained publish copied into `~/.var/app/app.zen_browser.zen/data/juvin/`, because the Debug host is framework-dependent and cannot run inside the sandbox.
