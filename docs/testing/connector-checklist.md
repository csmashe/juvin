# Browser connector manual checklist

Run after any change to `juvin-host`, the connector socket, `ConnectorService`, or the extension's background script. Unit tests cover framing and protocol logic; this checklist covers what only a real browser exercises: host launch, the OS endpoint, and the capture round trip.

## Steps (any OS)

1. Build the solution and the extension (`dotnet build Juvin.slnx`, `npm run build` in `extension/`).
2. Start the app with `XDG_DATA_HOME` (or the Windows profile) pointed at a scratch directory. The log must show `Browser connector ready at ...`.
3. Register the host: press Register for each browser in the setup wizard's Browser connector step or in the Browser connector section of settings. Each row must then read Registered. The dev scripts (`scripts/dev/register-host.sh` on Linux, `scripts/dev/register-host.ps1` on Windows) remain an alternative. On Ubuntu with Snap Firefox, if registration did not pre-approve the host, run `flatpak permission-set webextensions com.juvin.host snap.firefox yes` or expect the portal's consent dialog on first connect.
4. Load the extension: `npx web-ext run --source-dir dist/firefox` for Firefox; `dist/chrome` as an unpacked extension in Chrome or Edge. The app log must show `<browser> <version> connected`.
5. Open the popup and press Test connection. It must report a round trip in milliseconds.
6. Open the test page from the popup, select the draft, press the shortcut. The app log must show `Captured 161 draft chars, 3 messages, EntireThread, adapter test-page` and nothing from the draft or thread text.
7. Quit the browser. The app log must show `<browser> disconnected` and no `juvin-host` process may remain.

## Gmail steps (any OS)

Run after steps 1 to 5, signed in to a Gmail account with test conversations only. Use synthetic text; the log lines below carry counts, never content.

1. Open a conversation of three or more messages, all expanded. Reply, type a two-line draft, select part of it, and press the shortcut. The log must show `Captured <n> draft chars, <messages in the thread> messages, EntireThread, adapter gmail`.
2. Open a conversation long enough to show an "N older messages" bundle. Reply, select, and press the shortcut. Gmail must expand the whole conversation within about 3 seconds and the log must show `EntireThread` with every message counted.
3. Open a reply in a conversation and a new message window at the same time. Select text in the new message and capture: the log must show `0 messages, DraftOnly`. Select text in the reply and capture: the log must show the conversation's messages.
4. With a reply draft captured, apply a replacement for that capture. The selected text must change in place, and Ctrl+Z (Cmd+Z on macOS) must restore the original draft.
5. Capture again, type one character in the draft, then apply. The apply must be refused with `draft-changed` and the draft must stay as typed.

Until the rewrite panel can send an apply (Phase 4), run steps 4 and 5 from the extension's background console (Chrome: Inspect the service worker on the extensions page; Firefox: Inspect on `about:debugging`). Find the Gmail tab's id with `tabs.query`, send `{kind: "juvin.capture"}` with `tabs.sendMessage`, then send `{kind: "juvin.apply", editorId, expectedHash, start, end, text}` using the `editor.id`, `editor.hash`, and `selection` offsets from that capture.

### Gmail results

| Date | OS | Browser | Full thread | Collapsed thread | Two drafts | Apply and Undo | Edited draft refused |
| --- | --- | --- | --- | --- | --- | --- | --- |

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

The Windows and Snap Firefox rows show connect only: the Marionette driver used on Fedora (see below) did not start a session on Firefox 142 under Snap, and the Windows Firefox run was not scripted beyond loading the add-on. The popup and capture code is the same on every OS and was exercised in Firefox on Fedora and in Chrome and Edge on each OS. Not yet covered: Flatpak browsers on Fedora (the app now listens on the per-app runtime socket; rerun this checklist with a Flatpak browser to fill the row) and macOS (no machine available).

Open question: a real Ctrl+Shift+Y press could not be shown reaching Firefox from the scripted sessions (uinput key injection did not land in the browser window on either Linux desktop, for any key), so whether the extension command wins over Firefox's built-in Downloads shortcut on Linux is unverified. A manual press on a desktop settles it; if the built-in wins, the default shortcut must change.

## Notes for automation

- Windows: browsers and the app must run in the interactive session (the pipe name carries the session id). From SSH, use a one-shot scheduled task with `/it`, and set `DOTNET_ROOT` when the .NET 10 runtime is not the machine-wide install.
- Ubuntu over SSH: export `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, `DBUS_SESSION_BUS_ADDRESS`, `DISPLAY=:0`, and `XAUTHORITY` from `/run/user/1000/.mutter-Xwaylandauth.*`; set `TMPDIR=~/snap/firefox/common/tmp` for web-ext with Snap Firefox.
- Chrome ignores `--load-extension`; load the unpacked build over the DevTools protocol (`Extensions.loadUnpacked`) and trigger the command with `chrome.commands.onCommand.dispatch("rewrite")` in the service worker.
- Firefox: WebDriver BiDi refuses to navigate to extension pages. Launch with `--args=-marionette --args=-remote-allow-system-access` (test automation only, never a user requirement) and drive Marionette from the chrome context: `WebExtensionPolicy.getByID(...)` gives the popup and test page URLs to open in tabs, and dispatching a `command` event on the key inside `ext-keyset-id-extension_juvin_app` fires the extension command.
