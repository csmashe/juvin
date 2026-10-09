# Overlay panel spike

Throwaway Phase 0 spike. Results and conclusions are in `docs/spikes/overlay-panel.md`.

- `app/`: one Avalonia 12 program, `juvin-overlay-spike [--seconds N] [--cover]`. Shows a borderless, topmost, 360 by 200 panel at the top-right of the primary screen's working area, optionally opens a maximized normal window after it (`--cover`), prints one JSON line with the screen, working area, scaling, position, expected position, and backend, then exits.
- `tools/windows-run.cmd` with `tools/run-in-session.ps1`: run it in the interactive session when driving Windows over SSH.

## Rerun

Linux: `cd app && dotnet build && bin/Debug/net10.0/juvin-overlay-spike --cover`. Under GNOME Wayland the app runs through XWayland, so `xdotool search --name "Juvin overlay spike"` and `xprop` can confirm the geometry and `_NET_WM_STATE_ABOVE`. On a VM, take the console screenshot from the Proxmox host with `pvesh create /nodes/<node>/qemu/<id>/monitor --command "screendump /tmp/x.ppm"`.

Windows: `dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64`, copy `publish/win-x64` and `tools/` to `C:\juvin-spike\overlay`, run `windows-run.cmd` through `run-in-session.ps1`, screenshot the same way.
