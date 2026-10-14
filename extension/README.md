# Juvin browser extension

Manifest V3 extension for Chrome (and other Chromium browsers) and Firefox, built from one TypeScript source. It captures the selected draft and the surrounding thread, sends them to the Juvin desktop app over native messaging, and applies the result back into the editor. The wire protocol is `docs/connector-protocol.md`.

The extension stores no settings. Everything it needs arrives from the app in the `config` message and lives in memory for the length of the connection.

## Build and test

Requires Node 24.

```sh
npm install
npm run build   # type-check, then bundle dist/chrome and dist/firefox
npm test        # vitest, no browser needed
npm run watch   # rebuild scripts on change (static files are copied once at start)
```

## Pinned IDs

| Browser | ID | Where it comes from |
| --- | --- | --- |
| Chrome, Edge, Chromium | `okfcbcoboinjniofikfnmllkbaeadelo` | the public `key` added to the Chrome manifest in `build.mjs`; the private key is kept outside the repository |
| Firefox | `extension@juvin.app` | `browser_specific_settings.gecko.id` in the Firefox manifest |

The native host manifest allowlists these IDs, so they must not change.

## Run it

1. Register the native host once with `scripts/dev/register-host.sh` from the repository root.
2. Start the Juvin desktop app; the extension connects through `com.juvin.host`, which forwards to the running app.
3. Load the extension:
   - Chrome: open `chrome://extensions`, turn on Developer mode, choose "Load unpacked", and pick `extension/dist/chrome`.
   - Firefox: `npx web-ext run --source-dir dist/firefox` from `extension/`.
   - Zen, to keep it installed: Zen is built without enforced signing, so set `xpinstall.signatures.required` to false in `about:config`, run `npx web-ext build --source-dir dist/firefox` from `extension/`, and pick the `.xpi` with Install Add-on From File in `about:addons`. Flatpak Zen can only read the Downloads folder, so copy the `.xpi` there first. A rebuild with the same id installs over it.
4. Open the Juvin popup, check that it says "Connected to Juvin", and use "Open test page" to try a capture without Gmail. The setup wizard's Try it step opens the same page by sending `testpage.request`.

The rewrite shortcut is `Ctrl+Shift+F` (`Command+Shift+F` on macOS); Ctrl+Shift+Y was dropped because Firefox on Linux binds it to Downloads and its own shortcuts win over extension commands. Change it on the browser's shortcut page; the popup links to it.

`npx web-ext lint --source-dir dist/firefox` checks the Firefox build.

## Layout

- `src/background/`: native messaging connection, routing of `context.request` and `apply.request` to tabs, opening the test page for `testpage.request`, thread trimming to the frame cap.
- `src/content/`: site adapters, editor registry, capture, and apply. Only adapters read a site's DOM.
- `src/popup/`: connection status, connection test, shortcut, and the all-sites permission for the generic adapter.
- `src/test-page/`: a bundled sample thread with a reply editor, handled by the `test-page` adapter.
- `src/shared/`: protocol types and parsing, browser detection, internal message types.
