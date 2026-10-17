# Browser connector protocol

Date: October 9, 2026; the action menu messages were added October 13, 2026. Implements section 4.5 of `mvp-scope.md`. This is the contract between the extension (TypeScript) and the desktop app (C#); both sides are written against this file and tested with its examples.

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

Unknown properties are ignored. A message that is not an object, lacks `type`, has a missing or non-numeric `v`, or has a null in a required field or in an array gets `error` with code `bad-message`. A numeric `v` other than `1` gets `unsupported-version`, after which the app closes the connection (the extension does not close on its own). An `error` with an unsupported `v` gets no reply; the app still closes the connection. An `error` is never answered with another `error`, even when it is malformed or has an unsupported `v`. Requests sent in the wrong direction (for example `config` or `testpage.request` from the extension) get `unknown-type`. The app ignores an unmatched response, such as one that arrives after its request timed out or a second one for the same id, and logs only its type and code (a code this spec does not list is logged as `unknown`); an `error` that echoes a pending request's id fails that request.

Required fields: `id` on every request and response; `editorId`, `expectedHash`, `start`, `end`, and `text` on `apply.request`, with `0 <= start <= end`; on `context.response`, `capture` when `ok` is true and `error` when it is false; on `apply.response`, `testpage.response`, and `menu.response`, `ok`, plus `error` when it is false; in a capture, `adapter`, `editor`, `selection` with `0 <= start <= end`, and `thread` with `completeness` and `messages`; `menuId` on every `menu.*` message except `menu.response`, plus `editorId`, `slots` (exactly five actions), `more`, and `status` on `menu.show`, `action` on `menu.choose` (with `instruction` when it is `custom`, and only then), and `state` on `menu.update` with the fields its state requires. Optional: `result` on a `menu.update` error, `editorId` and `requireFocus` on `context.request`, `format` and `expectedFormatHash` on `apply.request` (`expectedFormatHash` required when `format` is set), `pageUrl`, `capturedAt`, `subject`, `selection.markdown` and `selection.formatHash` (sent together), `selection.hasEmbeddedContent` (default false), `collapsedCount` (default 0, never negative), and `to` and `sent` on a message. A `sent` value that does not parse becomes null; one with no offset is read as UTC.

Error payload used everywhere: `"error": {"code": "<kebab-case>", "message": "<English text for logs, never email content>"}`.

## Messages

### `hello` (extension to app, first message on a connection)

```json
{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}
```

`browser.name` is one of `firefox` (any Gecko browser, including Zen), `chrome`, `edge`, `chromium` (any other Chromium brand, such as Brave), `unknown`. The app answers with `config`. Any other message before `hello` gets `error` with code `hello-required`, except an `error`, which is ignored.

### `config` (app to extension)

Sent in reply to `hello` and again whenever settings change.

```json
{"v":1,"type":"config",
 "actions":[{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"}],
 "adapters":["gmail","generic","test-page"],
 "shortcut":"Ctrl+Shift+F",
 "health":{"mode":"local","modelReachable":null}}
```

`actions` is the user's configured catalog: the built-ins, then their custom actions, in the order the panel lists them. `health.mode` is `local` or `codex`; `modelReachable` is `true`, `false`, or `null` when the app has not probed yet. The extension stores nothing from `config` beyond the lifetime of the connection.

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
{"v":1,"type":"context.request","id":"c2","editorId":"e-7f3a"}
{"v":1,"type":"context.request","id":"c3","requireFocus":true}
```

Without `editorId`, the extension captures from the tab that was active at the latest shortcut press; if that tab is gone it answers `no-active-tab` rather than reading another tab. A request that no shortcut preceded uses the active tab of the last focused window. Only the latest press is remembered, and one the app has not asked about within 2 seconds is forgotten, because the app drops a `shortcut.pressed` that arrives within 300 ms of another capture start: on Windows and X11 one key press reaches both the desktop hotkey and the extension command, and the press it dropped must not hand its tab to the next one.

`requireFocus: true` marks a request from the desktop global hotkey, which has no originating browser, so the app sends it to every connected extension at once. The extension drops the pending shortcut press, checks that one of its windows has focus (`windows.getLastFocused()`), and otherwise answers `browser-not-focused` instead of reading a tab the user is not looking at. The app reports the first successful capture; failing that, an error other than `browser-not-focused` when any browser gave one. Requests without the flag, such as a Recapture from the panel, never check focus, because the panel itself has focus then.

With `editorId` (a Recapture from the panel), it captures from the tab that owns that editor and does not consume a shortcut's tab. If the editor id is unknown or its tab is closed it answers `editor-gone`, never falling back to the active tab. The content script resolves the id to its editor element: `editor-gone` when that editor has left the page, `no-selection` when the selection is not inside it.

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
   "selection":{"text":"Hi team, ...","start":0,"end":118,"markdown":"Hi **team**, ...","formatHash":"sha256:5d41a1..."},
   "thread":{
     "completeness":"entire-thread",
     "collapsedCount":0,
     "messages":[{"from":"Ana <ana@example.com>","to":["me@example.com"],"sent":"2026-10-08T09:12:00Z","body":"..."}]
   }}}
```

- `pageUrl` is the page's origin and path only, never its query or fragment, which on Gmail carry the search terms and the open view.
- `editor.id` is a token the content script assigns to the editor element for the life of the page; the app passes it back in `apply.request`.
- `editor.hash` is `sha256:` plus the lowercase hex SHA-256 of the editor's full plain text at capture time. The plain text follows `innerText`: `<br>` is a line break, a block starts and ends a line, and a table cell followed by another cell in its row ends with a tab. An emoji image reads as its emoji: an `img` whose `alt`, or failing that its `data-emoji`, is at most 16 characters made only of emoji characters (pictographs, regional indicators, skin-tone modifiers, the zero-width joiner, and variation selectors) contributes that text, as Gmail draws emoji that way. Such an image is text, never embedded content; an image whose `alt` is whitespace, punctuation, or a description stays embedded content, whatever its `data-emoji` says.
- `selection.text` is the text to rewrite; `start` and `end` are its character offsets within the editor's plain text.
- `selection.markdown` is the same range written in the Markdown subset below, sent only for a rich (contenteditable) editor, never for a textarea or a `contenteditable="plaintext-only"` host, which keep no formatting and are applied as plain text. Bold comes from `b`, `strong`, or a computed `font-weight` of 600 or more; italic from `i`, `em`, or a computed italic `font-style`; lists from `ul`, `ol`, and `li`; links from `a[href]` with an allowed scheme; a nested list item is indented two spaces per level. Its lines follow the same block and `<br>` rules as the plain text. Other formatting (underline, colours, fonts, headings, tables) is dropped to its text, with the tab between table cells kept. `text`, `start`, `end`, and `editor.hash` keep their plain-text meaning either way.
- `selection.formatHash` is sent with `selection.markdown` and is opaque to the app: `sha256:` plus the hex SHA-256 of the extension's lossless fingerprint of the range's formatting (runs of characters sharing bold, italic, link target, and list nesting with each numbered item's effective number, plus where each list item starts, and each image, table, horizontal rule, or embedded object the range touches, with its source and position). It exists because the Markdown drops formatting it cannot write unambiguously, so two drafts can share Markdown and still differ.
- `selection.hasEmbeddedContent` is true when the selected range of a contenteditable editor touches content Apply would delete (the same images, tables, rules, and embedded objects that make `apply.request` answer `unsupported-content`), and is left out otherwise. The app reads it before any model run: the panel says so at once and keeps Apply off, and the action menu shows its result as an error card with Copy instead of applying it. `apply.request` still refuses such a range on its own.
- The generic adapter sends one message whose `from` is the page's hostname and whose body is the page's main readable text with the draft removed; `subject` is the document title.
- The Gmail adapter clicks "Expand all" when messages are collapsed (waiting up to about 3 seconds), then sends one message per expanded row with `from` as `Name <address>`, `sent` when the date parses, and the body without hidden quoted text; `subject` is the conversation heading. Rows left collapsed and any "N older messages" bundle count toward `collapsedCount` and make it `loaded-messages-only`. A new message outside a conversation is `draft-only`; a conversation it cannot parse gets the generic adapter's capture.
- `thread.completeness` is one of `entire-thread`, `loaded-messages-only`, `visible-text-only`, `draft-only`, `truncated`. `truncated` means the extension dropped messages to stay under the frame cap or cut a message body at its per-message limit. Only `entire-thread` becomes `truncated`; a thread that is already partial (`loaded-messages-only`, `visible-text-only`) keeps its completeness when messages are dropped, and the app follows the same rule when it trims for the model. `loaded-messages-only` also covers a Gmail page in a language the adapter has no labels for, since it cannot tell whether messages are hidden. `messages` are oldest first; `to` and `sent` may be empty or absent.

Failure:

```json
{"v":1,"type":"context.response","id":"c1","ok":false,"error":{"code":"no-selection","message":"Nothing is selected in the editor."}}
```

Codes: `no-active-tab`, `editor-gone` (a `context.request` with `editorId` whose editor or tab is gone), `browser-not-focused` (a `requireFocus` request while no browser window has focus), `no-adapter` (no site adapter matched and the generic one found no editor), `no-editor` (a site adapter matched but found no editor), `no-selection`, `page-not-ready` (the content script could not be reached or injected), `internal` (including a selection too large for one frame).

### `apply.request` (app to extension)

```json
{"v":1,"type":"apply.request","id":"a1","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi team, thank you for the update..."}
```

```json
{"v":1,"type":"apply.request","id":"a2","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi **team**, thank you for the update...","format":"markdown","expectedFormatHash":"sha256:5d41a1..."}
```

`format` is `"markdown"` exactly when the capture being applied had `selection.markdown`; absent means plain text. Any other value is `bad-message`. With `"markdown"`, `expectedFormatHash` is required (`bad-message` without it) and carries that capture's `selection.formatHash`: because the hash covers plain text only, the content script also recomputes the formatting fingerprint of `start` to `end` in a rich editor and answers `draft-changed` when it differs, so a link, bold, or image changed since the capture is never overwritten. With `"markdown"`, a rich editor gets HTML the extension builds itself from the subset, as DOM nodes with the text in text nodes, inserted with the editor's `insertHTML` command; model text is never inserted as HTML. A textarea or plaintext-only host gets the Markdown stripped to plain text: list markers kept as `- ` and `1. `, indented two spaces per nesting level, emphasis markers and link syntax removed, link targets dropped. After a Markdown edit in a rich editor, the extension compares the editor's text with the expected text with every whitespace run (newlines and no-break spaces included) collapsed to one space, because the editor may restructure the inserted blocks. It also compares the formatting of every non-space character (bold, italic, link target, list nesting, item starts, and item numbers) in the edited editor's own terms: text before the range must keep its formatting; the inserted text must match the HTML, taken either as built or inside whatever encloses the insertion point (a list item, a bold run, a link), since the editor may carry that over; and text after the range must keep its formatting and list structure, with item numbers left free because adding or removing items renumbers them. So an edit that changed only formatting, or lost it, is not reported as a success. Either mismatch is undone when the editor changed and answered with `internal`.

`start` and `end` are the `selection` offsets from the capture being applied, so an older rewrite can never land on a later selection of the same editor. The content script finds the editor by id, recomputes the hash of its full text, and only when it equals `expectedHash` selects `start` to `end` and replaces it through the editor's own editing command so Undo keeps working. It never writes `innerHTML` or `textContent`. When `start` to `end` of a contenteditable editor touches an image other than an emoji image, a table, a horizontal rule, or an embedded object (`picture`, `svg`, `video`, `audio`, `canvas`, `iframe`, `object`, `embed`), which the replacement would delete, it answers `unsupported-content` without editing, whether or not that content was there at capture.

### Markdown subset

The only formats in either direction:

- Bold `**text**`, italic `*text*` (`_text_` is also accepted on apply, but not inside a word), and both `***text***`.
- Links `[text](url)` whose URL is `http:`, `https:`, or `mailto:`; any other link stays literal text. On capture, `(`, `)`, and whitespace in a URL are percent-encoded.
- Bulleted list lines starting `- ` or `* `, numbered list lines starting with a number (which may be zero or negative) and `. `. Consecutive top-level lines of one kind form one list. A list line indented further than the item before it nests one level under that item, of either kind; one indented less goes back to the level whose indent it reaches. Capture indents two spaces per level, apply accepts any width, and nesting stops at eight levels, deeper items joining the deepest list. Numbers are the ones a browser shows, following `start`, `reversed`, and `li[value]` on capture; on apply the list's `start` is the first number and an item whose number does not follow the one before gets `value`, so a list keeps its numbering both ways.
- Every newline is a line break; an empty line is an empty line.

Emphasis markers count only when they hug non-space text, so `5 * 3 * 2` stays literal, and emphasis nested more than eight deep keeps its markers as text. On capture, literal `*`, `_`, `[`, and `]`, a `\` that would otherwise read as an escape, and a plain line that starts like a list item (`- ` or `1. `), are escaped with a backslash; apply honours a backslash before `\ * _ [ ] - .`. Headings, tables, code, images, raw HTML, and any other Markdown syntax are literal text on apply.

### `apply.response` (extension to app)

```json
{"v":1,"type":"apply.response","id":"a1","ok":true}
{"v":1,"type":"apply.response","id":"a1","ok":false,"error":{"code":"draft-changed","message":"The draft changed since it was captured."}}
```

Codes: `draft-changed`, `editor-gone` (unknown editor id, the editor left the page, or the tab that captured it is closed or unreachable), `unsupported-content` (the range holds an image or other content that is not text, which the panel treats like `draft-changed`: Copy stays, Apply is off), `internal`.

### `testpage.request` (app to extension)

```json
{"v":1,"type":"testpage.request","id":"t1"}
```

Sent by the setup wizard's Try it step. The extension opens its bundled sample thread page (`test-page.html`) in a new active tab of its last focused window and brings that window to the front, ignoring a failure to focus it, then answers. The app cannot open the page itself because it is an extension page.

### `testpage.response` (extension to app)

```json
{"v":1,"type":"testpage.response","id":"t1","ok":true}
{"v":1,"type":"testpage.response","id":"t1","ok":false,"error":{"code":"internal","message":"The sample page could not be opened."}}
```

Codes: `internal`.

### `menu.show` (app to extension)

```json
{"v":1,"type":"menu.show","id":"m1","menuId":"menu-4","editorId":"e-7f3a",
 "slots":[{"id":"missing","title":"What am I missing?"},{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"},{"id":"shorter","title":"Make shorter"},{"id":"grammar","title":"Fix grammar"}],
 "more":[{"id":"custom-3f2a9b","title":"Pirate voice"}],
 "status":["6 messages","Entire thread","Private server: qwen3.5:27b"]}
```

Sent after every successful capture from the shortcut or the desktop hotkey (section 2.8 of `mvp-scope.md`). The app owns the menu; the extension only draws what this message carries and reports what the user does. `menuId` names this menu in every later `menu.*` message. `slots` are the five bubbles in key order, 1 to 5; `more` is the right-click list: the whole catalog in key order, then any custom actions, so it is never empty; `status` is the status line, already localized, in display order. Titles and status strings are display text and contain no email content.

The extension finds the editor's tab by `editorId` the way `apply.request` does, has that tab's content script draw the menu beside the editor without taking focus from it, and answers `menu.response`. The extension shows one menu at a time: a new `menu.show` replaces whatever menu is open, in any tab, without reporting the old one closed. The app sends `menu.close` for the old menu first anyway, and before a `menu.show` it also cancels any action the old menu was running.

### `menu.response` (extension to app)

```json
{"v":1,"type":"menu.response","id":"m1","ok":true}
{"v":1,"type":"menu.response","id":"m1","ok":false,"error":{"code":"unsupported","message":"This page cannot show the menu."}}
```

Codes: `editor-gone` (unknown editor id, the editor left the page, or its tab is closed or unreachable), `page-not-ready` (the content script could not be reached), `unsupported` (the extension or the page cannot draw the menu), `internal`. On any error, and when an older extension answers `error` with `unknown-type`, the app opens the rewrite panel with the capture instead, idle until an action is picked there.

### `menu.choose` (extension to app, notification)

```json
{"v":1,"type":"menu.choose","menuId":"menu-4","action":"soften"}
{"v":1,"type":"menu.choose","menuId":"menu-4","action":"custom","instruction":"Make it sound less formal."}
{"v":1,"type":"menu.choose","menuId":"menu-4","action":"review"}
{"v":1,"type":"menu.choose","menuId":"menu-4","action":"recapture"}
```

`action` is an id from `slots` or `more`, or one of the reserved words `review` and `recapture`, which are never action ids. `custom` is Ask for a change and carries the user's `instruction`, at most 2000 characters once trimmed; the app answers a longer or blank one with a non-retryable error. While the menu shows an `answer`, `custom` continues that conversation and gets another `answer`; nothing is applied until a different action is picked. Try again after an error sends the same choice again. The app ignores a choice for a `menuId` it no longer holds and a choice that arrives while this menu's action is running.

- An action id runs that action over the capture and reports through `menu.update`. The app applies the result itself, through `apply.request` with the capture's hash and format checks, so the editor is written only when it still matches the capture.
- `review` hands the capture to the desktop rewrite panel, which opens idle until an action is picked there. The extension closes the menu as soon as it sends this; the app sends no `menu.close` for it.
- `recapture` reads the editor again (a `context.request` with this capture's `editorId`). On success the app closes this menu and shows a new one, with a new `menuId`, over the fresh capture; on failure it reports `error` on this menu with `canRetry` false.

### `menu.update` (app to extension, notification)

```json
{"v":1,"type":"menu.update","menuId":"menu-4","state":"running","action":"soften"}
{"v":1,"type":"menu.update","menuId":"menu-4","state":"applied"}
{"v":1,"type":"menu.update","menuId":"menu-4","state":"answer","text":"- The draft does not answer the question about the deadline."}
{"v":1,"type":"menu.update","menuId":"menu-4","state":"draft-changed","text":"Hi team, thank you for the update..."}
{"v":1,"type":"menu.update","menuId":"menu-4","state":"error","text":"The model did not answer in time.","canRetry":true}
{"v":1,"type":"menu.update","menuId":"menu-4","state":"error","text":"The draft editor is no longer on the page. Copy the rewrite instead.","canRetry":false,"result":"Hi team, thank you for the update..."}
```

`state` is one of:

- `running`, with `action`: that bubble shows progress and the others dim; Escape cancels through `menu.close`.
- `applied`: the result replaced the selection. The extension closes the menu and shows a short note beside the editor that the draft was rewritten and Ctrl+Z (Cmd+Z on macOS) undoes it.
- `answer`, with `text`: the action returned advice rather than a draft (What am I missing?, and the follow-ups to it). Nothing was applied; the card shows the text and the menu stays open.
- `draft-changed`, with `text`: the result could not be applied because the editor no longer matches the capture. The card shows the result with Copy and Recapture.
- `error`, with `text` and `canRetry`: `text` is the message to show the user, already localized, which makes it the one error text in this protocol that is not just for logs. With `canRetry` the card offers Try again. When a finished rewrite could not be applied for a reason other than `draft-changed` (the editor is gone, the browser timed out or disconnected), `result` carries it so the card can offer Copy; a rewrite is never lost to an apply failure. A failure in local mode is reported here and never retried through another provider.

A `menuId` the extension no longer shows is ignored.

### `menu.close` (either direction, notification)

```json
{"v":1,"type":"menu.close","menuId":"menu-4"}
```

From the extension: the user dismissed the menu (Escape or a click outside it), or the editor or its tab went away. The app cancels the action that menu was running and forgets the menu. From the app: a new capture superseded the menu, or the app is exiting; the extension removes the menu. An unknown `menuId` is ignored in both directions.

### `error` (either direction)

```json
{"v":1,"type":"error","id":"c1","error":{"code":"unknown-type","message":"Unknown message type."}}
```

Codes: `bad-message`, `unsupported-version`, `unknown-type`, `hello-required`.

## Privacy

Neither side logs `selection.text`, `selection.markdown`, `thread.messages[].body`, `subject`, `apply.request.text`, `menu.choose.instruction`, `menu.update.text`, `menu.update.result`, or any part of them. Logs carry message types, ids, byte lengths, message counts, and completeness only.
