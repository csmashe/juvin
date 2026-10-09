# Spike: global hotkey delivery

Date: October 8, 2026. Phase 0 item 3 of `docs/mvp-scope.md`. Result: **go** on Linux (GNOME, KDE Plasma, and Hyprland, all Wayland) and Windows, with one backend per platform confirmed and a second Linux route confirmed. macOS is deferred (decision of October 8, 2026).

Throwaway code lives in `spikes/global-hotkey/` (see its README to rerun).

## Setup

- One .NET 10 console program with three backends: SharpHook 5.3.9 (libuiohook global hook), a minimal evdev reader over `/dev/input/event*`, and a local socket (Unix socket in `$XDG_RUNTIME_DIR`, named pipe on Windows) that a desktop-environment shortcut triggers by running the program with `trigger`.
- The program injects Ctrl+Shift+Y itself (ydotool through uinput on Linux, `SendInput` on Windows) and measures the time until the backend reports the press. Injected input, not a physical key press, so the numbers show delivery and order of magnitude only.
- Fedora 44, GNOME Shell 50.5 on Wayland, this machine (user already in the `input` group from earlier setup). Ubuntu 25.04 VM, GNOME Shell 48 on Wayland, user not in the `input` group. Windows 11 Pro 23H2 VM, driven over SSH with the test running in the interactive session through a scheduled task.

## Results

| Platform | Backend | Delivered | Latency | Notes |
| --- | --- | --- | --- | --- |
| Fedora, GNOME Wayland | evdev | yes | 36 ms | 4 keyboard nodes opened, none denied (user in `input` group) |
| Fedora, GNOME Wayland | GNOME custom shortcut to socket | yes | 75 ms | shortcut installed with `gsettings`, applied live, removed afterwards |
| Fedora, GNOME Wayland | SharpHook | no | | expected: libuiohook only sees XWayland clients |
| Ubuntu, GNOME Wayland | evdev, no group, no udev rule | no | | all 6 input nodes denied |
| Ubuntu, GNOME Wayland | evdev, after `uaccess` udev rule | yes | 203 ms (1) | 2 keyboard nodes opened through the logind ACL, no logout, no group change |
| Ubuntu, GNOME Wayland | GNOME custom shortcut to socket | yes | 250 ms (1) | same gsettings path as Fedora |
| Ubuntu, GNOME Wayland | SharpHook | no | | tried with and without XWayland authority |
| Ubuntu 26.04, KDE Plasma 6.6 Wayland | evdev, no group, no udev rule | no | | all 6 input nodes denied |
| Ubuntu 26.04, KDE Plasma 6.6 Wayland | evdev, after `uaccess` udev rule | yes | 29 ms (2) | ydotool 1.0 here, no built-in delay |
| Ubuntu 26.04, KDE Plasma 6.6 Wayland | KDE global shortcut to socket | yes | 90 ms | `.desktop` with `Type=Service` and `X-KDE-Shortcuts` in `~/.local/share/kglobalaccel/`, loaded on session start |
| Ubuntu 26.04, KDE Plasma 6.6 Wayland | SharpHook | no | | |
| Ubuntu 26.04, Hyprland 0.53 | evdev, after `uaccess` udev rule | yes | 27 ms | |
| Ubuntu 26.04, Hyprland 0.53 | Hyprland `bind` to socket | yes | 101 ms | `bind = CTRL SHIFT, Y, exec, ...` appended to `hyprland.conf`, applied by `hyprctl reload` |
| Ubuntu 26.04, Hyprland 0.53 | SharpHook | no | | |
| Windows 11 | SharpHook | yes | 14 ms | low-level keyboard hook, no elevation, interactive session required |
| Windows 11 | named pipe trigger | yes | | `\\.\pipe\juvin-hotkey-spike`, any session; no desktop shortcut writer on Windows, so this route is only useful for the extension command |

(2) KDE and Hyprland rows were added later on October 8, 2026 on a second Ubuntu VM (26.04 LTS, both desktops under SDDM).

(1) Ubuntu 25.04 ships ydotool 0.1.8, which waits 100 ms before pressing by default, so the real delivery time is about 100 ms lower. Both Linux routes land well under the 250 ms the panel needs to feel immediate.

## Findings

- **evdev is the Linux backend.** It works on Wayland with no compositor cooperation. Access comes from either the `input` group (Fedora, set up earlier) or a udev rule tagging keyboards with `uaccess`, which logind turns into a per-session ACL immediately after `udevadm trigger`. The rule used on Ubuntu, equivalent to the reference project's:

  ```
  SUBSYSTEM=="input", ENV{ID_INPUT_KEYBOARD}=="1", TAG+="uaccess", GROUP="input", MODE="0660"
  ```

  Installing it needs one privileged write, which is a wizard setup task with `pkexec`, the same shape as the reference project's hotkey setup task.
- **KDE and Hyprland behave like GNOME.** evdev needs the same udev rule on both; SharpHook sees nothing on either; each desktop's own shortcut mechanism reaches the socket. KDE reads the `.desktop` entry only at session start, so the wizard must say a re-login is needed (the reference project avoids the live D-Bus registration for that reason); Hyprland applies the bind live with `hyprctl reload`. The `Type=Service` line matters: an entry with `Type=Application` was ignored by KGlobalAccel.
- **The GNOME custom shortcut route works and needs no privileges.** Appending a path to `org.gnome.settings-daemon.plugins.media-keys custom-keybindings` and setting name, command, and binding applies live; the command runs with the session environment, so it can find the app's socket in `$XDG_RUNTIME_DIR`. The user's existing list must be preserved and the entry removed on uninstall. This is the fallback when the user declines the udev rule, and it is press-only.
- **SharpHook is Windows only in practice.** On both Wayland desktops it delivered nothing, even with XWayland reachable. On Windows it is the right backend: a low-level hook, no elevation, 14 ms.
- **Injected input looks like real input to all three backends.** uinput devices appear as ordinary keyboards to evdev and to mutter; `SendInput` reaches the low-level hook. Juvin's own future injection (if any) must therefore be filtered out of its hotkey matching, as the reference project does by device name.
- **The GlobalShortcuts portal exists on both GNOME machines** (`org.freedesktop.portal.GlobalShortcuts` version 1 on GNOME 48 and 50). Not tested; it would only matter for a Flatpak build of the app.
- **Driving Windows over SSH:** the hook and `SendInput` need the interactive session (scheduled task with `/it`). Two processes appending to one file with `>>` in cmd overwrite each other; use separate files.

## Consequences for the design

- `IHotkeyBackend` implementations: evdev on Linux (primary, requires the udev rule or group), SharpHook on Windows and macOS, and a desktop-shortcut writer backend on Linux that routes through the local socket (GNOME first; KDE, Hyprland, and Sway writers copied from the reference project later). The backend chain picks the first that can start, as the reference project does.
- The wizard gets a "Keyboard access" task on Linux: detect `input` group or ACL, offer the udev rule install through `pkexec`, verify by opening a keyboard node. Offer the GNOME shortcut as the no-privilege alternative.
- The local socket the app already needs for native messaging is the same one the desktop shortcut hits; `trigger` is just another message on it.
- Section 2.3's recommendation stands: ship the global hotkey and the extension command. Nothing here changes it.

## Not covered

- macOS: SharpHook needs Accessibility permission; deferred with the rest of macOS testing.
- Physical key presses by a user and non-US layouts. Key codes are layout independent for evdev; the GNOME binding string and SharpHook key codes may need layout handling.
- Sway, River, Niri, and X11 sessions. X11 is the backend SharpHook was built for, so it is expected to be global there.
- The GlobalShortcuts portal and a Flatpak build of the app.
