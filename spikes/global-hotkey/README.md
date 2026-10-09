# Global hotkey spike

Throwaway Phase 0 spike. Results and conclusions are in `docs/spikes/global-hotkey.md`.

- `app/`: one .NET 10 console program, `juvin-hotkey-spike`. `selftest --backend sharphook|evdev|socket` starts a backend, injects Ctrl+Shift+Y (ydotool on Linux, SendInput on Windows), and prints a JSON line saying whether the press arrived and how long after injection. `--gnome` installs a GNOME custom shortcut running `juvin-hotkey-spike trigger` for the socket backend and removes it afterwards. `--no-inject` waits for a real key press instead.
- `run-linux.sh`: runs the three backends on this machine.
- `tools/windows-run.cmd` with `tools/run-in-session.ps1`: SharpHook test in the interactive session when driving Windows over SSH.
- `tools/windows-pipe-test.ps1`: named pipe route on Windows, no interactive session needed.

## Rerun

Fedora or Ubuntu: `cd app && dotnet build`, make sure ydotoold is running, then `../run-linux.sh`. On Ubuntu the `ydotoold` package is separate from `ydotool`, the daemon socket must be readable by the user, and the key syntax differs (see the script header). To test evdev without the input group, install the udev rule from the results doc and run `udevadm trigger --subsystem-match=input --action=change`.

Windows: `dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64`, copy `publish/win-x64` and `tools/` to `C:\juvin-spike\hotkey`, then run `windows-run.cmd` through `run-in-session.ps1` and `windows-pipe-test.ps1` directly.
