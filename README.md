# Juvin

Works beside you.

Juvin is an open-source desktop assistant for Linux, macOS, and Windows that helps inside the apps you already use. Select the email you are drafting, press a shortcut, and Juvin reads your draft together with the whole conversation it replies to, then offers a rewrite that is softer, clearer, shorter, or corrected while keeping your deadlines, names, and intent intact. You review the result, ask for changes in plain language, and apply it back into your editor. Juvin runs on your own machine and uses a local model by default, so your email never leaves your computer unless you choose otherwise.

Status: early development. The desktop app skeleton (settings window, setup wizard, tray, local control socket) builds and runs on Linux and Windows; the rewrite workflow and the browser extension are in progress.

- [MVP scope and phases](docs/mvp-scope.md)
- [Research](docs/research/desktop-ai-assistant-research.md)
- [Brand assets](assets/brand/README.md)

Built with .NET 10 and Avalonia, plus a browser extension for Firefox and Chrome.

## Building

Requires the .NET 10 SDK.

```
dotnet build Juvin.slnx
dotnet test Juvin.slnx
dotnet run --project src/Juvin.App
```

## License

GPL-3.0. See [LICENSE](LICENSE).
