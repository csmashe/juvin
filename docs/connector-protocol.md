# Browser connector protocol

Date: October 9, 2026. Implements section 4.5 of `mvp-scope.md`. This is the contract between the extension (TypeScript) and the desktop app (C#); both sides are written against this file and tested with its examples.

## Transport

1. The browser launches `juvin-host` (native messaging host name `com.juvin.host`) and talks to it over stdin and stdout using the browser's native messaging framing: a 4-byte little-endian unsigned length followed by that many bytes of UTF-8 JSON. One JSON object per frame.
2. `juvin-host` connects to the app's **connector endpoint** and copies bytes verbatim in both directions. It parses nothing. When either side closes, it closes the other and exits.
3. The connector endpoint uses the same framing as native messaging, so the app reads exactly what the extension wrote. It is separate from the one-shot control socket: a connection stays open for the life of the browser session and either side may send at any time.

| OS | Connector endpoint |
| --- | --- |
| Linux | `$XDG_RUNTIME_DIR/juvin/connector.sock`, falling back to `<data>/Runtime/connector.sock` like the control socket. Inside a Flatpak sandbox (`FLATPAK_ID` set) the host uses `$XDG_RUNTIME_DIR/app/<FLATPAK_ID>/juvin/connector.sock` instead. The app also listens on that path for every Flatpak browser it has registered, binding it at startup and right after a registration. |
| macOS | `<data>/Runtime/connector.sock` |
| Windows | Named pipe `juvin-connector-<user SID>-<session id>` |

Frame cap: 1,048,576 bytes (the browsers' own limit for host-to-browser messages). A frame over the cap closes the connection on the app side; the extension never sends one, trimming the thread (oldest messages first) to stay about 1 KB under it. The generic adapter's single message is also capped at 20,000 characters.

The extension opens the connection on browser startup and install and again on any user action (shortcut, popup button) when it is not connected. It counts as connected once `config` arrives; with no `config` after 30 seconds it disconnects. There is no retry loop: a failed or dropped connection shows a badge and the next user action reconnects. The first connection on a portal-based Linux browser can show a consent dialog, which is why user actions must be able to connect.

If the app is not running, the host fails to connect and exits non-zero; the extension sees the port disconnect with an error.

## Envelope

Every message is a JSON object with:

- `v`: protocol version, the integer `1`.
- `type`: one of the types below.
- `id`: for requests, a string the sender chooses (unique per connection); the matching response echoes it. Notifications have no `id`.

Unknown properties are ignored. A message that is not an object, lacks `type`, has a missing or non-numeric `v`, or has a null in a required field gets `error` with code `bad-message`. A numeric `v` other than `1` gets `unsupported-version`, after which the app closes the connection (the extension does not close on its own). An `error` reply is never answered with another `error`. Requests sent in the wrong direction (for example `config` from the extension) get `unknown-type`. The app ignores an unmatched response and logs only its type and code; an `error` that echoes a pending request's id fails that request.

Required fields: `id` on every request and response; `editorId`, `expectedHash`, `start`, `end`, and `text` on `apply.request`, with `0 <= start <= end`; `capture` when `ok` is true and `error` when it is false; in a capture, `adapter`, `editor`, `selection`, and `thread` with `completeness` and `messages`. Optional: `pageUrl`, `capturedAt`, `subject`, `collapsedCount` (default 0), and `to` and `sent` on a message. A `sent` value that does not parse becomes null; one with no offset is read as UTC.

Error payload used everywhere: `"error": {"code": "<kebab-case>", "message": "<English text for logs, never email content>"}`.

## Messages

### `hello` (extension to app, first message on a connection)

```json
{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}
```

`browser.name` is one of `firefox` (any Gecko browser, including Zen), `chrome`, `edge`, `chromium` (any other Chromium brand, such as Brave), `unknown`. The app answers with `config`. Any other message before `hello` gets `error` with code `hello-required`.

### `config` (app to extension)

Sent in reply to `hello` and again whenever settings change.

```json
{"v":1,"type":"config",
 "actions":[{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"}],
 "adapters":["gmail","generic","test-page"],
 "shortcut":"Ctrl+Shift+Y",
 "health":{"mode":"local","modelReachable":null}}
```

`health.mode` is `local` or `codex`; `modelReachable` is `true`, `false`, or `null` when the app has not probed yet. The extension stores nothing from `config` beyond the lifetime of the connection.

### `ping` and `pong` (either direction)

```json
{"v":1,"type":"ping","id":"p1"}
{"v":1,"type":"pong","id":"p1"}
```

### `shortcut.pressed` (extension to app, notification)

```json
{"v":1,"type":"shortcut.pressed"}
```

Sent when the extension's command fires. The app then sends `context.request`.

### `context.request` (app to extension)

```json
{"v":1,"type":"context.request","id":"c1"}
```

The extension captures from the tab that was active when the shortcut fired; if that tab is gone it answers `no-active-tab` rather than reading another tab. A request that no shortcut preceded uses the active tab of the last focused window.

### `context.response` (extension to app)

Success:

```json
{"v":1,"type":"context.response","id":"c1","ok":true,
 "capture":{
   "adapter":"gmail",
   "pageUrl":"https://mail.google.com/mail/u/0/",
   "capturedAt":"2026-10-09T14:03:21.512Z",
   "subject":"Invoice for September",
   "editor":{"id":"e-7f3a","hash":"sha256:9f86d0..."},
   "selection":{"text":"Hi team, ...","start":0,"end":118},
   "thread":{
     "completeness":"entire-thread",
     "collapsedCount":0,
     "messages":[{"from":"Ana <ana@example.com>","to":["me@example.com"],"sent":"2026-10-08T09:12:00Z","body":"..."}]
   }}}
```

- `editor.id` is a token the content script assigns to the editor element for the life of the page; the app passes it back in `apply.request`.
- `editor.hash` is `sha256:` plus the lowercase hex SHA-256 of the editor's full plain text at capture time.
- `selection.text` is the text to rewrite; `start` and `end` are its character offsets within the editor's plain text.
- The generic adapter sends one message whose `from` is the page's hostname and whose body is the page's main readable text with the draft removed; `subject` is the document title.
- `thread.completeness` is one of `entire-thread`, `loaded-messages-only`, `visible-text-only`, `draft-only`, `truncated`. `truncated` means the extension dropped messages to stay under the frame cap. `messages` are oldest first; `to` and `sent` may be empty or absent.

Failure:

```json
{"v":1,"type":"context.response","id":"c1","ok":false,"error":{"code":"no-selection","message":"Nothing is selected in the editor."}}
```

Codes: `no-active-tab`, `no-adapter` (no site adapter matched and the generic one found no editor; Gmail reports this until its adapter lands), `no-editor` (a site adapter matched but found no editor), `no-selection`, `page-not-ready` (the content script could not be reached or injected), `internal` (including a selection too large for one frame).

### `apply.request` (app to extension)

```json
{"v":1,"type":"apply.request","id":"a1","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi team, thank you for the update..."}
```

`start` and `end` are the `selection` offsets from the capture being applied, so an older rewrite can never land on a later selection of the same editor. The content script finds the editor by id, recomputes the hash of its full text, and only when it equals `expectedHash` selects `start` to `end` and replaces it through the editor's own editing command so Undo keeps working. It never writes `innerHTML` or `textContent`.

### `apply.response` (extension to app)

```json
{"v":1,"type":"apply.response","id":"a1","ok":true}
{"v":1,"type":"apply.response","id":"a1","ok":false,"error":{"code":"draft-changed","message":"The draft changed since it was captured."}}
```

Codes: `draft-changed`, `editor-gone` (unknown editor id, the editor left the page, or the tab that captured it is closed or unreachable), `internal`.

### `error` (either direction)

```json
{"v":1,"type":"error","id":"c1","error":{"code":"unknown-type","message":"Unknown message type."}}
```

Codes: `bad-message`, `unsupported-version`, `unknown-type`, `hello-required`.

## Privacy

Neither side logs `selection.text`, `thread.messages[].body`, `subject`, `apply.request.text`, or any part of them. Logs carry message types, ids, byte lengths, message counts, and completeness only.
