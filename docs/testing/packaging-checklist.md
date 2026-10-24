# Packaging manual checklist

## Linux packages

Run before publishing a release and after any change to `scripts/build-linux-packages.sh`, the install layout, or the dependency lists. `scripts/smoke-test-linux-packages.sh` covers metadata, a container install, and a headless start; this checklist covers what only a real desktop shows: the menu entry, the icon, the tray, the browser connector, and the global shortcut.

### Steps

1. Build the packages with `scripts/build-linux-packages.sh <version>` or take them from the Release Linux workflow's artifact. Run `scripts/smoke-test-linux-packages.sh <version>`; it must end with `All smoke tests passed.`
2. Install the deb (`sudo apt install ./juvin_<version>_amd64.deb`) or the rpm (`sudo dnf install ./juvin-<version>-1.x86_64.rpm`) on a desktop that has never had Juvin. The install must pull in its dependencies without errors.
3. Open the desktop's application menu and search for Juvin. It must appear once, with the Juvin mark as its icon, named "Juvin (Alpha)" for an alpha version (the prerelease word in brackets) or "Juvin" for a release, and launch the app. The tray icon must appear where the desktop shows tray icons.
4. Run the setup wizard. Register the browser connector for each installed browser: every row must read Registered and the manifests must point at `/opt/juvin/juvin-host` (Flatpak browsers: at the copy inside the browser's sandbox).
5. Load the extension and press Test connection in its popup. It must report a round trip.
6. Set the shortcut, select the test page draft, and press it. The rewrite panel must open with a result.
7. Remove the package (`sudo apt remove juvin` or `sudo dnf remove juvin`). `/opt/juvin`, `/usr/bin/juvin`, and the menu entry must be gone; the user's `~/.local/share/Juvin` stays.
8. AppImage: `chmod +x Juvin-<version>-x86_64.AppImage` and run it from a directory such as `~/Applications`. Register a native browser: its manifest must point at `~/.local/share/Juvin/juvin-host`, not at a `/tmp/.mount_*` path. Quit, start the AppImage again, and check the browser still connects. On a desktop that uses the desktop's own shortcut (GNOME, KDE, Hyprland, Sway on Wayland), the shortcut command must name the `.AppImage` file. The copy is removed with the last manifest that uses it; the app has no unregister button, so that part is covered by the registrar's unit tests.

### Results

| Date | Distro | Package | Install | Menu and icon | Tray | Connector | Shortcut | Remove |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| October 13, 2026 | Ubuntu 25.04, GNOME 48 Wayland | deb 0.1.0-dev | Pass: `apt-get install ./juvin_0.1.0-dev_amd64.deb` resolved every dependency (libicu76, libxt6t64, libssl3t64) | Pass: one entry, launched with `gtk-launch juvin`, the eight icon sizes and the symbolic SVG installed | Not shown: the session had no StatusNotifierWatcher at the time (`gdbus` reports the name unowned), so the probe correctly said no tray | Pass: Snap Firefox registered to `/opt/juvin/juvin-host`, Flatpak Zen showed Needs update and Update replaced its sandbox copy with the packaged host (bytes identical); the packaged host connected to the running app and exited 0; the extension connection test was not run | Not run (the app was started from SSH without the desktop variables, so no backend was available) | Pass: `apt-get remove` left no `/opt/juvin`, wrappers, desktop entry, or icons, and `~/.local/share/Juvin` stayed |
| October 13, 2026 | Ubuntu 25.04, GNOME 48 Wayland | AppImage 0.1.0-dev | Pass: ran under FUSE from `~/Applications` with `APPIMAGE` set | Not applicable | Same session as above | Pass: Firefox showed Needs update (manifest pointed at `/opt/juvin/juvin-host`) and Update pointed it at `<data>/juvin-host` (mode 700, bytes identical to the AppImage's host); Zen stayed Registered because its sandbox copy already matched; the copy connected to the running app, and after quitting and relaunching the AppImage both browsers still read Registered and the copy still connected | Pass: the GNOME custom keybinding command is `/home/csmashe/Applications/Juvin-0.1.0-dev-x86_64.AppImage --trigger` | Deleted the file; nothing else to remove |
| October 13, 2026 | Fedora 44, GNOME 48 Wayland | rpm 0.1.0-dev | Pass: `dnf install` of the local rpm resolved every requirement | Pass: one entry, launched with `gtk-launch juvin`, nine icon files installed | Pass: shown (the session has a StatusNotifier host) | Pass: native Firefox and Chrome showed Needs update (manifests pointed at a Debug host) and Update pointed both at `/opt/juvin/juvin-host`; the packaged host connected to the running app and exited 0; the extension connection test was not run. The first run exposed that `APPIMAGE` leaked in from the AppImage-packaged terminal host and made the rpm register the data-directory copy; after the `APPDIR` check the same session registered `/opt/juvin/juvin-host` | Pass: evdev listens (the user has keyboard access), so no desktop shortcut was written | Pass: `dnf remove` left no `/opt/juvin`, wrappers, desktop entry, or icons |
| October 13, 2026 | Fedora 44, GNOME 48 Wayland | AppImage 0.1.0-dev | Pass: ran under FUSE from `dist/` | Not applicable | Pass | Pass: both native browsers showed Needs update and Update pointed them at `<data>/juvin-host` (mode 700), which connected to the running app and exited 0 | Not applicable (evdev) | Deleted the file; nothing else to remove |
| October 15, 2026 | Ubuntu 25.04, GNOME 48 Wayland (VM 820) | AppImage 0.1.0-dev.5 | Pass: ran under FUSE from `~/Applications` with a fresh data folder | Pass: `juvin-appimage.desktop` with `StartupWMClass=juvin` and the eight PNG sizes plus the symbolic SVG were written on the first run, the dock showed the mark, the Exec line followed a move to `~/Applications/moved/`, and `gtk-launch juvin-appimage` started the app | Pass: the mark showed in the top bar | Pass: Register for Google Chrome wrote a manifest pointing at `~/.local/share/Juvin/juvin-host` and the extension connected through it | Not run (step 8's shortcut press was covered on October 13) | Not applicable |

## Windows installer

Run before publishing a release and after any change to `scripts/build-windows-installer.ps1`, `scripts/windows/juvin.iss`, or the install layout. `scripts/smoke-test-windows-installer.ps1` covers a silent install, the installed files and registry entries, a start of the app, and a silent uninstall; this checklist covers the interactive install, the Start menu, the tray, the browser connector, the shortcut, and upgrades.

### Steps

1. Build the installer with `scripts\build-windows-installer.ps1 <version>` or take it from the Release Windows workflow's artifact. Run `scripts\smoke-test-windows-installer.ps1 <version>`; it must end with `All smoke tests passed.`
2. On a Windows account that has never had Juvin, run `Juvin-<version>-x64-setup.exe` by double-clicking it. It must not ask for administrator rights (no UAC prompt), it must propose `%LOCALAPPDATA%\Programs\Juvin`, and the setup window and the finished page must show the Juvin mark. SmartScreen warns about an unsigned file until code signing is added; note what it shows.
3. Leave Launch Juvin ticked on the last page. The app must start and the tray icon must appear in the notification area with the Juvin mark.
4. Open the Start menu and search for Juvin. It must appear once with the Juvin mark, named as the Linux menu entry is ("Juvin (Alpha)" for an alpha), and launch the app (a second launch shows the running window). Settings, Apps, Installed apps must list it under the same name with the mark, the version, and Excel on the Web as publisher.
5. Run the setup wizard. Register the browser connector for Chrome and Firefox: every row must read Registered, both manifests in `%LOCALAPPDATA%\Juvin\NativeHost` must point at `%LOCALAPPDATA%\Programs\Juvin\juvin-host.exe`, and the extension's Test connection must report a round trip.
6. Set the shortcut (Ctrl+Shift+F by default), select the test page draft, and press it. The action menu must open beside the draft.
7. Upgrade in place: with an older version installed and running, run a newer installer. Setup must ask you to quit Juvin first (quit it from the tray and click Retry), then install into the same folder without asking for it again, and the browser rows must still read Registered afterwards without registering again. When the prerelease word changed (alpha to beta, or to a release), the Start menu must show only the new name.
8. Uninstall from Installed apps. With Juvin still running in the tray, the uninstaller must ask you to close it first. After quitting, `%LOCALAPPDATA%\Programs\Juvin`, the Start menu entry, and the desktop icon (if it was chosen) must be gone; `%LOCALAPPDATA%\Juvin` and the browsers' `NativeMessagingHosts` keys stay.

### Results

| Date | Windows | Installer | Install | Start menu and icon | Tray | Connector | Shortcut | Upgrade | Uninstall |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| October 14, 2026 | Windows 11 Pro 23H2 (VM 500) | 0.1.0-dev.2 then 0.1.0-dev.3 | Pass: no UAC prompt, the wizard proposed `%LOCALAPPDATA%\Programs\Juvin` and the mark showed in its title bar; SmartScreen was not shown (the file came over scp, so it carried no mark of the web) | Pass: one `Juvin.lnk` pointing at the installed exe; Installed apps entry with the mark, the version, and Excel on the Web | Pass: the installed exe is listed in `HKCU\Control Panel\NotifyIconSettings` once the app runs | Pass: all three rows (Firefox, Chrome, Edge) showed Needs update from a dev copy and Update pointed the manifests in `%LOCALAPPDATA%\Juvin\NativeHost` at `%LOCALAPPDATA%\Programs\Juvin\juvin-host.exe`; the installed host connected to the running app and exited 0; the extension connection test was not run | Not run: Home shows "Ctrl+Shift+F, listening"; the real press is covered by the Slice C run of the same hook | Pass: with dev.2 running, the dev.3 setup stopped with the close-Juvin prompt (silent run exit 1); after quitting it installed into the same folder, the uninstall entry read 0.1.0-dev.3, and the three rows still read Registered without registering again | Pass: with Juvin running the uninstaller stopped with the same prompt; after quitting, the folder, shortcut, App Paths key, and uninstall entry were gone and `%LOCALAPPDATA%\Juvin` and the browser keys stayed |

## macOS dmg

Run before publishing a release and after any change to `scripts/build-mac-dmg.sh`, `scripts/mac/entitlements.plist`, or the bundle layout. `scripts/smoke-test-mac-dmg.sh` runs on the GitHub macOS runner in the Release macOS workflow and covers what a machine with no person at it can show: the image verifies and holds `Juvin.app` and an Applications link, the Info.plist carries the bundle identifier `com.juvin.app`, the versions, the Local Network usage description, and a minimum macOS read from the payload, every executable and the bundle pass `codesign --verify --deep --strict` with the hardened runtime and the JIT entitlement, `juvin-host` reports that no app is running and `juvin-cli --help` runs, and the app started through Launch Services opens its control socket, answers `--trigger`, and quits on SIGTERM. It prints the Gatekeeper verdict without failing on it, because an ad-hoc signature is always rejected. This checklist covers what only a person at a real Mac sees.

### Steps

1. Build the dmg with `scripts/build-mac-dmg.sh <version>` or take it from the Release macOS workflow's artifact. Run `scripts/smoke-test-mac-dmg.sh <version>`; it must end with `All smoke tests passed.`
2. Download the dmg in Safari or Chrome so it carries the quarantine flag (`xattr -p com.apple.quarantine` on the file shows a value), open it, and drag Juvin to Applications. Double-click Juvin. macOS must refuse it as from an unidentified developer; note the exact dialog and whether System Settings, Privacy and Security, Open Anyway lets it start (on macOS 15 and later the Control-click Open bypass is gone). Until the dmg is notarized this is what every user sees.
3. The first start must show the Juvin mark in the Dock and in the menu bar, and the setup wizard. The menu bar icon's menu must open the window and quit the app. Close the window: with Close to tray on, the Dock icon stays and the menu bar icon reopens the window; note what clicking the Dock icon does.
4. Run the setup wizard. The Accessibility row must open System Settings at Accessibility; after granting it, the row must read granted and Home must show the shortcut listening. Register the browser connector for each installed browser: the manifests in `~/Library/Application Support/<browser>/NativeMessagingHosts` must point at `/Applications/Juvin.app/Contents/MacOS/juvin-host`, and the extension's Test connection must report a round trip.
5. Start Juvin straight from the mounted dmg without copying it. Registering a browser must refuse with the message about moving Juvin to Applications (the app runs translocated).
6. Point the model server at a machine on the LAN (not localhost) and test it. macOS must ask for Local Network access with the Info.plist's description; after allowing it the test must pass, and denying it must show a connection error, not a hang.
7. Set the shortcut, select the test page draft, and press it. The action menu must open beside the draft.
8. Run the Launch at login steps below on this Mac with a real log out and log in (not a restart of the app): Juvin must start with its menu bar icon and no window, and the shortcut must work without granting Accessibility again.
9. Uninstall by quitting Juvin and moving `/Applications/Juvin.app` to the Trash. `~/Library/Application Support/Juvin` and the manifests stay.

### Results

None yet. The first run of the steps is on a rented Apple Silicon Mac (macOS 26).

## Launch at login

Run on each OS before publishing a release and after any change to the login item implementations or the `--background` start. The entry is checked against the installed build, not a Debug build, because it names the running executable.

### Steps

1. Install the package for the OS, finish the setup wizard, and turn on Launch at login in General. The switch must stay on with no error line.
2. Check the entry. Linux: `~/.config/autostart/juvin.desktop` exists, carries `X-Juvin-Managed=true`, and its `Exec` names `/opt/juvin/juvin` (deb or rpm) or the `.AppImage` file, followed by `--background`. macOS: `~/Library/LaunchAgents/com.juvin.app.plist` names the executable inside `Juvin.app` and `--background`. Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has a `Juvin` value of `"%LOCALAPPDATA%\Programs\Juvin\juvin.exe" --background`; Settings, Apps, Startup lists Juvin as on.
3. Sign out and sign in again. Juvin must start with its tray icon and no window, and the shortcut must open the action menu. On a Linux desktop without a tray host the window opens instead.
4. Linux AppImage: move the `.AppImage` file, start it from the new place, quit, sign out and in. The entry must name the new path and Juvin must start.
5. Turn Launch at login off. The entry must be gone, and signing out and in must not start Juvin.
6. Uninstall with Launch at login on. Windows: the uninstaller must remove the `Juvin` Run value, and must leave one that names another executable alone. Linux and macOS: the entry lives in the user's home, which a package removal does not touch; sign out and in and note what the leftover entry does.

### Results

October 15, 2026, Ubuntu 25.04, GNOME 48 Wayland (VM 820), AppImage 0.1.0-dev.5, steps 1, 2, 3, and 5 passed and step 4 was only partly run: the switch stayed on; `~/.config/autostart/juvin.desktop` carried `X-Juvin-Managed=true` and `Exec="<AppImage path>" --background`; after a fresh automatic login Juvin ran with `--background`, showed the tray mark, and opened no window; running the AppImage again showed the window; turning the switch off removed the entry and the next login started nothing. Step 4: the entry named the moved AppImage because the switch was turned on after the move; moving the file with the switch already on and signing in again was not run. `loginctl terminate-session` left GDM at its greeter despite automatic login, so each sign-in was forced with `systemctl restart gdm3`.

October 15, 2026, Windows 11 Pro 23H2 (VM 500), 0.1.0-dev.4: step 6 only. The smoke test's Run value was gone after the silent uninstall, and a value of `"C:\Other\juvin.exe" --background` planted before a second uninstall was still there afterwards. Steps 1 to 5 and the Linux and macOS runs are still to do.
