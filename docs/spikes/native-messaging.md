# Spike: native messaging round trip

Date: October 8, 2026. Phase 0 item 1 of `docs/mvp-scope.md`. Result: **go** on Fedora, Ubuntu (GNOME, KDE Plasma, and Hyprland), and Windows for every browser tested, with design requirements for sandboxed Linux browsers recorded below. macOS is assumed to work and is deferred until a Mac is available (decision of October 8, 2026).

Throwaway code lives in `spikes/native-messaging/` (see its README to rerun).

## Setup

- Linux: Fedora 44, GNOME on Wayland, xdg-desktop-portal 1.22.1. Ubuntu 25.04 VM, GNOME on Wayland, xdg-desktop-portal 1.20 with the WebExtensions portal, Snap Firefox.
- Windows: Windows 11 Pro 23H2 (build 22631), the project test machine, driven over SSH.
- Host: a self-contained single-file .NET 10 console app that reads length-prefixed JSON on stdin, answers `hello` with `pong`, and logs lengths and process facts.
- Extension: Manifest V3, one source for both browsers. Chrome ID pinned with a `key` in the manifest; Firefox ID pinned with `browser_specific_settings.gecko.id`.
- Round trip measured by the extension: `hello` out, `pong` back, `ack` out, `bye` back.

## Results: Fedora

| Browser | Packaging | Host manifest location | Round trip | Result |
| --- | --- | --- | --- | --- |
| Firefox 156.0.1 | Fedora RPM | `~/.mozilla/native-messaging-hosts/` | 125 ms | go |
| Chrome 154.0.8037.92 | Google RPM | `<user-data-dir>/NativeMessagingHosts/` | 46 ms | go |
| Firefox 157.0 | Flathub Flatpak | `~/.var/app/org.mozilla.firefox/.mozilla/native-messaging-hosts/` | 148 ms | go, host runs inside the sandbox |
| Zen 1.22.3b (Gecko 156) | Flathub Flatpak | `~/.var/app/app.zen_browser.zen/.mozilla/native-messaging-hosts/` | 138 ms | go, needs a Flatpak override |

Round trip times include spawning the 74 MB single-file host. Trimming the host (framework-dependent or NativeAOT) is a packaging concern, not a blocker.

## Results: Ubuntu

| Browser | Packaging | Host manifest location | Round trip | Result |
| --- | --- | --- | --- | --- |
| Firefox 142.0.1 | Snap (Ubuntu default) | `~/.mozilla/native-messaging-hosts/` on the host, read by the portal | 175 ms | go, through the WebExtensions portal |
| Chrome 155 | deb, extracted without root | `<user-data-dir>/NativeMessagingHosts/` | 75 ms | go |

The Snap host process is spawned by `xdg-desktop-portal` outside the sandbox, so the host binary can live anywhere and the app socket is the normal one. Chrome was extracted from the deb and run with `--no-sandbox` because the setuid helper cannot be installed without root; that affects only the test setup.

## Results: Ubuntu 26.04 with KDE Plasma and Hyprland

Added later on October 8, 2026 on a second Ubuntu VM (26.04 LTS cloud image with both desktops under SDDM, xdg-desktop-portal 1.21 with the KDE and Hyprland portal backends). Snap Firefox used the WebExtensions portal, pre-approved with `flatpak permission-set` as on GNOME; Chrome is the Google deb.

| Desktop | Browser | Packaging | Route | Round trip | Result |
| --- | --- | --- | --- | --- | --- |
| KDE Plasma 6.6, Wayland | Firefox 157.0.1 | Snap | portal, host launched by `xdg-desktop-portal` | 425 ms | go |
| KDE Plasma 6.6, Wayland | Chrome 155 | Google deb | `<user-data-dir>/NativeMessagingHosts/` | 211 ms | go |
| Hyprland 0.53, Wayland | Firefox 157.0.1 | Snap | portal, host launched by `xdg-desktop-portal` | 361 ms | go |
| Hyprland 0.53, Wayland | Chrome 155 | Google deb | `<user-data-dir>/NativeMessagingHosts/` | 63 ms | go |

The portal route is desktop independent: the WebExtensions portal lives in `xdg-desktop-portal` itself, and only the consent dialog comes from the desktop's backend. With the approval stored ahead of time neither KDE nor Hyprland showed a difference from GNOME.

## Results: Windows

| Browser | Registration | Round trip | Result |
| --- | --- | --- | --- |
| Firefox 157.0 | `HKCU\Software\Mozilla\NativeMessagingHosts\<name>` default value = manifest path | 506 ms | go |
| Chrome 155 | `HKCU\Software\Google\Chrome\NativeMessagingHosts\<name>` | 163 ms | go |
| Edge 154.0.4258.62 | `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\<name>` | 172 ms | go |

All three use per-user registry keys, so registration needs no elevation. Chrome and Edge share the manifest format and, with the pinned `key`, the same extension ID, so one manifest file serves both. The Firefox time is a first launch after install; later runs were not measured.

## Findings: Linux

1. **The WebExtensions portal is not available here.** Fedora 44's xdg-desktop-portal 1.22.1 does not expose `org.freedesktop.portal.WebExtensions` on the session bus and the binary contains no reference to it. The portal route (host launched outside the sandbox) cannot be assumed on Linux. The documented design for that route is Mozilla's [native messaging portal design](https://firefox-source-docs.mozilla.org/toolkit/components/extensions/webextensions/native-messaging-portal-design.html).
2. **Running the host inside the sandbox works.** A self-contained .NET binary placed under `~/.var/app/<flatpak-id>/` executes inside the browser's sandbox (the Freedesktop runtime provides glibc; `InvariantGlobalization` removes the ICU dependency). The browser finds the manifest in the persisted `.mozilla` directory of its own sandbox home.
3. **The sandboxed host can reach the desktop app two ways.** Verified from inside the Firefox sandbox: a loopback TCP connection to a port on the host, and a Unix socket at `$XDG_RUNTIME_DIR/app/<flatpak-id>/<name>.sock`. Flatpak binds that per-app runtime directory into the sandbox. The Unix socket is the better choice: no port to collide on and peer credentials are available.
4. **Chrome's per-user host directory is under the user data directory**, not a fixed path. For the default profile that is `~/.config/google-chrome/NativeMessagingHosts/`; Chromium, Brave, and other channels each have their own. A custom `--user-data-dir` does not see the default location.
5. **Zen is Gecko with `Vendor=Mozilla`**, so it reads `~/.mozilla/native-messaging-hosts/`, but its Flatpak only persists `~/.zen`. Registration needs `flatpak override --user --persist=.mozilla app.zen_browser.zen` once, then the manifest goes in `~/.var/app/app.zen_browser.zen/.mozilla/native-messaging-hosts/`. Native Zen installs should use `~/.mozilla/native-messaging-hosts/` like Firefox; this was not tested because no native Zen is installed here.
6. **Chrome 154 ignores `--load-extension`**, including with the `DisableLoadExtensionCommandLineSwitch` feature override. Loading an unpacked extension for automated tests works through the DevTools protocol over the debugging pipe (`Extensions.loadUnpacked` with `--remote-debugging-pipe --enable-unsafe-extension-debugging`). `spikes/native-messaging/tools/chrome-load.py` does this. Firefox and the Flatpaks load through `web-ext run` (version 10.7.0 supports `--firefox flatpak:<app-id>`).
7. **The pinned Chrome `key` works.** The ID derived from the public key matched the ID Chrome assigned, so the host manifest allowlist can be written before the extension is ever loaded.

## Findings: Ubuntu

1. **The WebExtensions portal exists and works on Ubuntu.** `org.freedesktop.portal.WebExtensions` is on the session bus, Snap Firefox uses it by default, and the portal launched the host from the host-side Firefox manifest directory. The permission store already held an entry for Ubuntu's GNOME Shell integration extension, so this is the route Ubuntu relies on.
2. **The consent dialog is only shown to the focused app.** On first use the portal asks "allow this extension to start this host". When the browser was launched from SSH and not focused, GNOME refused the dialog (`Only the focused app is allowed to show a system access dialog`) and the connection failed with "An unexpected error occurred". A connection attempted at browser startup, before the window has focus, can hit the same refusal. Once approved, the decision is stored and later connections do not prompt.
3. **The approval can be stored ahead of time.** `flatpak permission-set webextensions <host-name> snap.firefox yes` writes the same permission store entry the dialog would, and the round trip then succeeds without a prompt. The app id is `snap.firefox` for the Snap and the Flatpak id for a Flatpak.
4. **web-ext needs its temporary profile inside the snap-readable home.** The default `/tmp` profile is invisible to the snap; `TMPDIR=~/snap/firefox/common/tmp` fixes it. Test setup only.

## Findings: Windows

1. **The host exe runs as-is.** The same single-file publish for `win-x64` passed the stdio probe and all three browsers with no code change. `Path.GetTempPath()` and the framing code behave the same.
2. **Chrome and Edge read the registry regardless of profile.** A temporary `--user-data-dir` still found the host through `HKCU`, unlike Linux where the manifest directory lives inside the user data directory.
3. **Browsers need an interactive session.** Launched from the SSH service session, Chrome and Edge worked but Firefox failed to start its GPU process and exited. Running web-ext through a one-shot scheduled task with the interactive flag (`tools/run-in-session.ps1`) fixed it. This only matters for automation; users run browsers on a desktop.
4. **winget is unusable from the SSH session** ("Failed when opening source(s)"). Direct installer downloads work. Relevant for any future CI or setup automation on that machine.

## Consequences for the design

- `INativeHostRegistrar` on Linux must register per browser packaging, not per browser: native Firefox, Flatpak Firefox, Flatpak Zen, Chrome, Chromium, and so on, each with its own manifest directory. For a Flatpak it must also copy the host binary under `~/.var/app/<id>/` because the sandbox cannot execute binaries from the host filesystem.
- The desktop app's local socket must listen at `$XDG_RUNTIME_DIR/juvin.sock` for native browsers and additionally at `$XDG_RUNTIME_DIR/app/<flatpak-id>/juvin.sock` for each registered Flatpak browser.
  Since then the layout moved into a directory: `juvin/control.sock` and `juvin/connector.sock` under the runtime directory, and `app/<flatpak-id>/juvin/connector.sock` for each Flatpak browser.
- Both Linux routes are needed: the in-sandbox host with the per-app socket where there is no portal (Fedora Flatpaks), and the host-side manifest where the portal exists (Ubuntu Snap). The registrar can write both; the browser picks the one its build supports.
- The extension must open the native connection from a user action (the shortcut or the popup), not at startup, so the portal consent dialog can appear. The wizard's connection test should tell the user to expect that dialog once. The setup wizard can also pre-approve through the permission store, since the wizard itself is the user's consent.
- The wizard's sandboxed-browser detection (Phase 5) becomes: enumerate installed Flatpak browsers, offer registration for each, and apply the Zen override when needed.
- `INativeHostRegistrar` on Windows writes one `HKCU` key per browser (Firefox, Chrome, Edge, and later Chromium and Brave) pointing at two manifest files, one Gecko and one Chromium. Edge should be a first-class browser in the extension builds and the wizard, since it ships with Windows.

## Not covered by this spike

- macOS round trip. Deferred by decision on October 8, 2026: the mechanism is the same and macOS has no Snap or Flatpak style sandbox, so it is assumed to work until a Mac is available. Expected manifest locations: `~/Library/Application Support/Mozilla/NativeMessagingHosts/` for Firefox and `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` for Chrome. Verify before the Phase 5 packaging work.
- Edge on Linux and macOS, and Chromium and Brave anywhere. They follow the Chrome pattern with their own manifest directories.
- Snap Firefox without the portal (manifest inside `~/snap/firefox/common/.mozilla/`). Not needed while the portal route works.
- A screenshot of the portal consent dialog; GNOME blocks programmatic screenshots over SSH. Its existence is confirmed from the portal's journal messages.
