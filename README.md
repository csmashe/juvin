# Juvin

Works beside you.

Juvin is an open-source desktop assistant for Linux, macOS, and Windows that helps inside the apps you already use. Select the email you are drafting in Gmail, press Ctrl+Shift+F, and a menu of actions opens beside the draft: What am I missing?, Soften, Make clearer, Make shorter, and Fix grammar, or a request of your own. Juvin reads your draft together with the whole conversation it replies to, so the rewrite keeps your deadlines, names, and intent, and it replaces your selection in place, where Ctrl+Z puts the original back. Juvin uses a model on your own computer or network by default, so your email never leaves your machines unless you choose otherwise.

Status: alpha. The full workflow works on all three systems; see the [user guide](https://github.com/csmashe/juvin/wiki) to install and set it up.

- [User guide (wiki)](https://github.com/csmashe/juvin/wiki)
- [Privacy policy](docs/PRIVACY.md)
- [Release notes](docs/releases)
- [Brand assets](assets/brand/README.md)

Built with .NET 10 and Avalonia, plus a browser extension for Firefox and Chrome.

## Installing

Every release on the [Releases](https://github.com/csmashe/juvin/releases) page has the Linux packages (deb, rpm, and AppImage), the Windows installer, and the macOS dmg, each with the browser connector bundled. The deb and rpm install under `/opt/juvin` and add a menu entry; the AppImage runs from wherever you put it. The Windows installer installs for the current user under `%LOCALAPPDATA%\Programs\Juvin` without asking for administrator rights. The macOS app is not notarized yet, so the first start needs Open Anyway in System Settings. Install the browser extension from the [Chrome Web Store](https://chromewebstore.google.com/detail/juvin/igmlmppjhljfjnepflcpjblbdilkalkg) (Chrome, Edge, Brave, and other Chromium browsers) or [Firefox Add-ons](https://addons.mozilla.org/firefox/addon/juvin/); it needs Juvin 0.1.0-alpha.2 or later. The [user guide](https://github.com/csmashe/juvin/wiki/Installing) walks through each system.

To build the Linux packages yourself:

```
scripts/build-linux-packages.sh <version>
scripts/smoke-test-linux-packages.sh <version>
```

To build the Windows installer yourself (needs Inno Setup 6.3 or later):

```
scripts\build-windows-installer.ps1 <version>
scripts\smoke-test-windows-installer.ps1 <version>
```

## Building

Requires the .NET 10 SDK.

```
dotnet build Juvin.slnx
dotnet test Juvin.slnx
dotnet run --project src/Juvin.App
```

## License

GPL-3.0. See [LICENSE](LICENSE).
