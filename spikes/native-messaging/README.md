# Native messaging spike (throwaway)

Result and findings: `docs/spikes/native-messaging.md`. Nothing here is product code.

## Layout

- `host/`: .NET console host. Publish with `dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish`.
- `extension/`: MV3 extension that connects to `com.juvin.spike` on install and POSTs the outcome to `http://127.0.0.1:18765/report`.
- `tools/report-server.py`: collects those POSTs into `/tmp/juvin-nm-spike/reports.jsonl`.
- `tools/chrome-load.py`: launches Chrome with a temp profile and loads the unpacked extension over the DevTools pipe.
- `tools/uninstall.sh`: removes the host manifests and sandbox copies this spike registered.
- `tools/host-probe.ps1`: drives the host exe over stdio on Windows.
- `tools/windows-setup.ps1` and `tools/windows-uninstall.ps1`: write the manifests and the `HKCU` keys for Firefox, Chrome, and Edge.
- `tools/chrome-load.ps1` and `tools/report-server.ps1`: PowerShell versions of the Chrome loader and collector; the loader also drives Edge.
- `tools/run-in-session.ps1` and `tools/windows-firefox-run.cmd`: run web-ext in the interactive session when driving the machine over SSH.

## Rerun

1. Publish the host, then write host manifests named `com.juvin.spike` pointing at `host/publish/juvin-nm-spike` (see the results doc for each browser's directory). For Chrome use the ID in `chrome-extension-id.txt`; it is derived from `chrome-dev.pem`, which is not committed, so regenerate both if the key is missing.
2. Start `python3 -I tools/report-server.py` in the background.
3. Firefox or a Flatpak Gecko browser: `web-ext run --source-dir extension --firefox /usr/bin/firefox` or `--firefox flatpak:org.mozilla.firefox`.
4. Chrome: `mkdir -p /tmp/juvin-chrome-profile/NativeMessagingHosts`, copy the Chrome manifest there, then `python3 -I tools/chrome-load.py $PWD/extension /tmp/juvin-chrome-profile`.
5. Read `/tmp/juvin-nm-spike/reports.jsonl`.

## Rerun on Ubuntu with Snap Firefox

Same as Fedora, with three differences: approve the host once with `flatpak permission-set webextensions com.juvin.spike snap.firefox yes` (or let the dialog appear by running from the desktop), export `TMPDIR=$HOME/snap/firefox/common/tmp` before `web-ext run`, and when driving the machine over SSH also export `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, and `DBUS_SESSION_BUS_ADDRESS` from the desktop session. The loader honours `CHROME_EXTRA_ARGS`, for example `--no-sandbox` for a Chrome extracted from the deb without root.

## Rerun on Windows

1. Publish with `-r win-x64` and copy `juvin-nm-spike.exe`, `chrome-extension-id.txt`, the `extension` folder, and the `tools` scripts into one folder such as `C:\juvin-spike`.
2. Run `windows-setup.ps1` from that folder, then `report-server.ps1` in a second window.
3. Chrome or Edge: `chrome-load.ps1 -Extension <folder>\extension -Profile $env:TEMP\juvin-chrome-profile -Chrome <path to chrome.exe or msedge.exe>`.
4. Firefox: `web-ext run --source-dir <folder>\extension --firefox "C:\Program Files\Mozilla Firefox\firefox.exe"` from a desktop session, or `windows-firefox-run.cmd` through `run-in-session.ps1` over SSH.
5. Read `$env:TEMP\juvin-nm-spike\reports.jsonl`.
