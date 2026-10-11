# Juvin MVP scope: context-aware email rewriting

Date: October 8, 2026. Status: proposed scope, no implementation yet.

Companion document: [desktop-ai-assistant-research.md](research/desktop-ai-assistant-research.md) (market research and product definition). This document narrows that research to a buildable first release and splits the work into phases.

## 1. What the MVP is

A cross-platform desktop app (Linux, macOS, Windows) built with .NET and Avalonia, plus a browser extension for Firefox and Chrome. Together they deliver one workflow end to end:

1. The user is writing an email reply in webmail. They press Ctrl+A (Cmd+A on Mac) to select their draft, or select part of it.
2. They press the Juvin rewrite shortcut, which is editable in Juvin's settings.
3. The browser extension captures the selected draft and the rest of the email conversation from the page and hands both to the desktop app.
4. The desktop app shows a small panel with the context status ("Draft selected · 6 messages · Local model"), rewrite actions (Soften, Clearer, Shorter, Grammar, custom instruction), and a streamed preview.
5. The user can ask follow-ups ("keep the deadline but sound less annoyed") or ask whether the draft misses anything the thread asked for.
6. **Apply** replaces the original selection in the original editor, only if the editor and draft are unchanged. Otherwise the result is offered as Copy. The user sends the email themselves.

Processing runs on a local model by default. External providers are an explicit opt-in.

The settings window and first-run setup wizard follow the TypeWhisper for Linux design (dark theme, left sidebar, card sections, checklist-driven wizard). The MVP needs design work only for those two surfaces plus the rewrite panel.

### Not in the MVP

- Screen conversation ("What is this?" about a photo). This is the second product workflow and comes after the MVP. The architecture leaves room for it (see section 5).
- Native mail clients (Thunderbird, Apple Mail, Outlook desktop). Browser webmail only.
- Rewriting in non-browser apps. A clipboard-only fallback is a stretch goal for Phase 4, not a commitment.
- Voice input, read-aloud, persistent memory, plugins marketplace, creative tools.
- Mail provider APIs (Gmail API, Graph). The extension reads the loaded page. API retrieval is a post-MVP option for guaranteed completeness.

## 2. Key design decisions

### 2.1 The browser extension does capture and replacement, not the desktop app

This is the single most important decision. The research document's hardest problem is reading the selection and pasting back on GNOME Wayland, where there is no input injection and TypeWhisper has to rely on ydotool and a udev rule. For webmail, the draft lives in a browser `contenteditable`, and a WebExtension content script can:

- read the current selection and the editor element it belongs to,
- walk the page DOM to collect every loaded message in the conversation,
- replace the selection in place through standard editing APIs, so the editor's own Undo stack keeps working,
- verify before applying that the editor is still focused and the draft text still matches what was captured.

None of this needs OS-level accessibility, clipboard simulation, or key injection. It behaves the same on all three operating systems and both browsers. The desktop app owns the shortcut, the settings, the model routing, the panel, and local storage.

This also follows the closest commercial reference. Clico's email tool is a Chrome extension: select text, press Ctrl+O, ask for a change, and the rewrite happens in place with nothing touching the draft until the user accepts. Its context comes from the selection and an "@" picker for other tabs. It does not read the thread, which is the gap Juvin fills.

### 2.2 Desktop app and extension talk over native messaging

Chrome and Firefox both support native messaging: the browser launches a registered host executable and talks to it over stdin/stdout with length-prefixed JSON. The host is a small Juvin process (`juvin-host`) that forwards to the running desktop app over a local socket, the same pattern TypeWhisper uses for its control socket and CLI. Nothing goes through a vendor server.

The connection is long-lived and two-way: once the extension opens it, either side can send at any time. The extension opens it at browser startup and reconnects if it drops.

**The extension has no settings of its own.** On connect it sends a `hello`, and the desktop app replies with everything the extension needs: available actions, supported site adapters, the shortcut to mirror, and connector health. The extension stores nothing locally. All configuration lives in the Juvin desktop app. When one side needs something only the other can do (the page DOM on the browser side; hotkeys, models, secrets, and history on the desktop side), it asks over this channel.

Registration differs per browser and OS and is a setup-wizard task:

| OS | Chrome | Firefox |
| --- | --- | --- |
| Linux | `~/.config/google-chrome/NativeMessagingHosts/<name>.json` (plus Chromium, Brave, Edge dirs) | `~/.mozilla/native-messaging-hosts/<name>.json` |
| macOS | `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` | `~/Library/Application Support/Mozilla/NativeMessagingHosts/` |
| Windows | `HKCU\Software\Google\Chrome\NativeMessagingHosts\<name>` pointing at the manifest | `HKCU\Software\Mozilla\NativeMessagingHosts\<name>` |

Known risk: sandboxed browsers. Flatpak and Snap Firefox cannot launch arbitrary host binaries without extra steps. The wizard must detect the install kind and report it rather than silently failing. This is a Phase 0 spike item.

### 2.3 The shortcut lives in two places, and the desktop app is the source of truth

The user edits the shortcut in Juvin's settings. Two triggers map to it:

- **Desktop global hotkey.** Owned by the Juvin app. On Linux this reuses TypeWhisper's backend chain (evdev on Wayland with the uaccess udev rule, SharpHook on X11, desktop-environment shortcut writers for GNOME/KDE/Hyprland/Sway). On Windows and macOS, SharpHook provides a global hook, with macOS needing Accessibility permission. On press, the app asks the extension for the context of the active tab in the focused browser window.
- **Extension command shortcut.** WebExtensions have a `commands` API with a user-configurable shortcut that works whenever the browser has focus, on every OS, with no Wayland caveats. The extension forwards the press to the desktop app. Juvin's settings show both and deep-link to the browser's shortcut page (`chrome://extensions/shortcuts`, Firefox "Manage Extension Shortcuts") when the two drift.

The default is Ctrl+Shift+F (Cmd+Shift+F on macOS). Ctrl+Shift+Y was the first choice and was dropped on October 10, 2026: Firefox binds it to Downloads on Linux and a browser's own shortcuts win over extension commands, so the key had to be one no browser binds on any OS.

Recommendation: ship both. The extension command is the one that always works; the global hotkey is the one the user asked for and keeps the door open for non-browser apps later. If Phase 0 shows the global hotkey is unreliable on one platform, the extension command carries that platform alone.

### 2.4 Thread capture uses site adapters with a generic fallback

- **Gmail adapter first.** Gmail is the most common webmail and the research already validated its structure. The adapter collects each expanded message's sender, recipients, timestamp, and body, detects collapsed messages, and reports the completeness state.
- **Outlook on the web second** (post-MVP unless Phase 0 shows it is cheap).
- **Generic adapter** for any other page: selection plus a readability-style extraction of the main content, labeled "Visible text only".

Completeness states shown to the user: `Entire thread loaded`, `Loaded messages only (N collapsed)`, `Visible text only`, `Draft only`, `Truncated for model limit`. Attachments are never claimed as read.

Decided: Gmail is the first and only adapter in the MVP.

### 2.5 Local-first processing with explicit modes

Decided: the MVP ships two providers, **local** and **Codex subscription**. No API-key providers in the first release.

| Mode | Where the draft and thread go | Default |
| --- | --- | --- |
| Local | An OpenAI-compatible server on this machine or on the user's own network (Ollama, LM Studio, llama.cpp). The user enters the base URL; `localhost` is labeled "this device", anything else "private server" | Yes |
| Codex | The signed-in Codex CLI, which sends the request to OpenAI under the user's ChatGPT subscription. No API key is stored by Juvin | Opt-in, shown before first use |

No automatic fallback between modes. A local failure is reported, never silently retried through Codex. The setup wizard detects a running Ollama or LM Studio and recommends a model, and detects an installed, signed-in Codex CLI.

**How the Codex provider works.** It runs the installed `codex` executable as a child process, the same approach TypeWhisper's Authenticated CLIs plugin uses:

```
codex exec --json --ephemeral --sandbox read-only --skip-git-repo-check -m <model> -
```

The prompt goes in on stdin and the response comes back as JSONL events on stdout. Juvin parses the final agent message and ignores everything else. `--sandbox read-only` and `--ephemeral` keep Codex from touching files or persisting a session. The wizard verifies the binary with `codex exec --help` and surfaces sign-in state. Codex also accepts `--image`, which matters for the screen-conversation workflow later. Spike result (October 8, 2026): the JSONL stream carries only the completed message, no per-token deltas, so Codex mode shows progress rather than a live preview.

Providers implement an interface shaped like TypeWhisper's `ILlmProviderRole` so Anthropic, OpenAI, and other API-key providers, or a plugin loader, can be added later without changing callers.

### 2.6 Technology

- .NET 10, Avalonia 12, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, SharpHook, Tmds.DBus.Protocol on Linux. Same stack as TypeWhisper so patterns carry over.
- WebExtension Manifest V3 with `webextension-polyfill`, one codebase producing a Chrome build and a Firefox build. TypeScript, no framework in the content script.
- Settings in JSON with atomic writes and a backup copy, secrets encrypted at rest with a per-install key (TypeWhisper's `ApiKeyProtection` pattern; on Windows and macOS use DPAPI and Keychain instead).

### 2.7 Licensing

Decided: Juvin is open source under **GPL-3.0** with no attribution to TypeWhisper. Clico and the other products in the research are UX references only; no code or assets come from them.

**Rule for reusing TypeWhisper for Linux code.** The GPL lets anyone copy GPL code, but it also requires keeping the copyright notices on what is copied. TypeWhisper for Linux is a derivative of the upstream TypeWhisper project, and its NOTICE file splits copyright: Excel on the Web owns the Linux-specific additions, TypeWhisper owns the upstream code the port incorporates. So:

- **Files original to the Linux port** are Excel on the Web's copyright alone. They can be copied into Juvin under Juvin's own notice with no reference to TypeWhisper. A per-file git history check on October 8, 2026 (following renames, see `docs/spikes/copy-history.md`) confirms these are Linux-only: the hotkey backends and `ShortcutMatcher`/`ShortcutDispatcher`, the desktop-environment shortcut writers, `ISetupTask` and the setup tasks, the control socket and JSON protocol, `AtomicJsonStore`, `ApiKeyProtection`, the LLM streaming helpers (`SseEventDecoder`, `ThinkingBlockFilter`, `LlmStreamPump`), `PromptProcessingService`, `LlmCallProvenance`, the AT-SPI and Wayland detection code, and the Avalonia UI including `MainWindow`, the wizard, and the section `.axaml` files. Not Linux-only, because git traces them to the upstream Windows files: `ILlmProviderRole.cs` and the code-behind files of the General, History, Appearance, Shortcuts, and Advanced sections. Juvin writes those fresh; the provider interface may follow the same idea without copying the file.
- **Files that also exist upstream** carry TypeWhisper's copyright and copying them would require keeping that notice. These are `SettingsService`, `AppSettings`, `AtomicFileWrite`, `TypeWhisperEnvironment`, `OpenAiChatHelper`, `Loc.cs`, and the Authenticated CLI plugin (`AuthenticatedCliPlugin`, `CliProcessRunner`, `CliProviderContracts`). **Do not copy these.** Write Juvin's equivalents fresh. They are small and generic (a JSON settings store, an OpenAI chat client, a child-process runner, a string table), so rewriting costs little.
- The file-name check is a first pass, not proof. Before copying any file, confirm from its git history that it was created in the Linux repository rather than renamed or adapted from an upstream file.
- "TypeWhisper" is a trademark and must not appear in Juvin's name, UI, or branding regardless of code reuse.

This is engineering guidance, not legal advice.

## 3. What is reused from TypeWhisper, and how

The TypeWhisper code is Linux-only and has no OS abstraction layer. Juvin needs one from day one. The table shows what carries over and what changes.

| TypeWhisper piece | Reuse | Change for Juvin |
| --- | --- | --- |
| Main window: 272px sidebar, nav groups, `ContentControl` section switching | Design and structure | Move the copy-pasted card styles into one shared resource dictionary. Keep the dark palette (`#0E131B`, `#141D26`, `#283644`, accent `#1593F5`). |
| `ISetupTask` and the wizard's checklist step | Interface as-is | Add per-OS task sets. Wizard steps change (see section 4.2). |
| `Loc` JSON localization and `{loc:Str}` markup | Pattern only (`Loc.cs` exists upstream) | Rewrite; English only for MVP; keep the mechanism so strings are not hard-coded. |
| `IGlobalShortcutBackend`, evdev backend, SharpHook backend, `ShortcutMatcher`, DE shortcut writers | Linux implementations | Put behind a platform package. Windows and macOS get SharpHook-only implementations. Drop push-to-talk; only a single press is needed. |
| Control socket (Unix socket, JSON lines, single-instance guard) | Pattern | Windows uses a named pipe. The native messaging host is a new client of this socket. |
| `AtomicJsonStore`, `ApiKeyProtection` | Code | `SettingsService` and `AppSettings` exist upstream, so Juvin's settings model and service are written fresh. Platform-specific secret storage on Windows and macOS. |
| `ILlmProviderRole`, SSE decoder, `ThinkingBlockFilter`, `LlmStreamPump` | Code | In-process, not plugins. `OpenAiChatHelper` exists upstream; write a new chat client. |
| Authenticated CLI plugin (Codex invocation) | Pattern only (exists upstream) | Write Juvin's Codex provider fresh using the command line in section 2.5. |
| `PromptProcessingService` JSON-wrapping of user text, `AppendReferenceContext` injection framing | Pattern | Thread messages are data, never instructions. Reuse the defang and cap logic. |
| `LlmCallProvenance` and History "Inspect" panel | Design | Becomes the Context panel: exactly what was sent, which provider, local or not. |
| Clipboard-simulated selection capture, ydotool/wtype insertion, AT-SPI paste confirmation | Not in MVP | Only needed for non-browser apps. Post-MVP. |
| Audio, transcription engines, dictionary, snippets, profiles, HTTP API | No | Out of scope. |

## 4. MVP feature list

### 4.1 Rewrite workflow

| Feature | Detail |
| --- | --- |
| Trigger | Global hotkey or extension command, both configurable, default Ctrl+Shift+F (Cmd+Shift+F on macOS) |
| Capture | Selection, editor identity, thread messages, participants, subject, page URL, capture time, completeness state |
| Actions | Soften, Clearer, Shorter, Fix grammar, Custom instruction, "What am I missing?" (checks the draft against requests in the thread) |
| Preview | Streamed result, side-by-side or inline diff toggle, follow-up input retaining context |
| Apply | Replaces original selection only if editor and draft hash still match. Otherwise shows "Draft changed" with Copy and Recapture |
| Copy | Always available |
| Undo | Editor-native Undo works after Apply because replacement goes through the editing API |
| Guardrails in prompt | Preserve deadlines, amounts, names, commitments, and firmness. Quoted thread is data, not instruction |
| Context panel | Chips for each source, message count, completeness state, provider, local/cloud label, expandable raw context |
| History | Opt-in local history of rewrites with delete and export |

### 4.2 Settings window sections

| Section | Contents |
| --- | --- |
| Home | Status: browser connector connected, model reachable, shortcut registered. Recent rewrites. "Open setup wizard" |
| Shortcuts | Rewrite shortcut (global), extension shortcut mirror, backend status, desktop-integration setup on Linux |
| Rewrite actions | Built-in actions with editable prompts, custom actions, default action, length guidance |
| Browser connector | Detected browsers, registration status per browser, extension install links, sandboxed-browser warnings, connection test |
| Models | Processing mode (Local / Codex), local server URL and model picker with capability check, Codex binary and sign-in status, connection test |
| Privacy and data | History on/off and retention, provenance capture, crash reporting (off by default), data folder, delete all |
| General | Launch at login, close to tray, language, updates |
| About | Version, licenses, diagnostics export, settings backup/restore |

### 4.3 Setup wizard steps

1. **Welcome and processing mode.** Choose Local or Codex. Detect Ollama and LM Studio and offer a recommended model download if nothing is installed; detect the Codex CLI and its sign-in state.
2. **Browser connector.** Detect installed browsers, register the native messaging host for each, link to the extension in each store, wait for the extension to connect, show green when it does.
3. **Shortcut.** Set the global hotkey; on Linux, run the DE shortcut writer or evdev access task.
4. **Setup checklist.** Per-OS `ISetupTask` rows: evdev udev rule (Linux Wayland), Accessibility permission (macOS), nothing extra on Windows, sandboxed-browser warnings.
5. **Try it.** A sample email thread opens in the browser (a bundled local HTML page the extension treats as a test adapter). The user selects the draft, presses the shortcut, and sees a rewrite.
6. **Done.**

### 4.4 Browser extension

| Part | Detail |
| --- | --- |
| Manifest | MV3, permissions: `nativeMessaging`, `activeTab`, `scripting`, `storage`, `commands`. Host permissions only for supported webmail origins plus optional all-sites for the generic adapter |
| Background | Connects to the native host on demand, relays requests and results, surfaces connection errors as a badge |
| Content script | Adapter registry: Gmail, generic, test page. Captures selection and thread; applies replacement; verifies draft hash before apply |
| Popup | Connection status, "Open Juvin", shortcut reminder. No chat UI in the extension; the panel is the desktop app |
| Builds | One source, two outputs. Firefox needs `browser_specific_settings.gecko.id`; Chrome needs a stable extension ID (publish or pin a `key`) because the host manifest allowlists it |

### 4.5 Native messaging protocol

JSON messages over the native host, forwarded verbatim to the desktop app socket:

- `hello` with extension and browser identity; the desktop replies with `config` (actions, adapters, shortcut, health). `config` is re-sent whenever settings change.
- `context.request` (desktop to extension) and `context.response` with the capture payload.
- `apply.request` with the replacement text and the expected draft hash; `apply.response` with success or `draft-changed`.
- `shortcut.pressed` (extension to desktop) when the extension command fires.
- `ping` for the connection test.

## 5. Architecture

```
juvin/
├── src/
│   ├── Juvin.Core/             # settings, models, rewrite pipeline, providers, protocol types (no UI, no OS calls)
│   ├── Juvin.App/              # Avalonia UI: settings window, wizard, rewrite panel, tray
│   ├── Juvin.Platform/         # IHotkeyBackend, ISecretStore, ILocalSocket, INativeHostRegistrar, ISetupTask
│   ├── Juvin.Platform.Linux/   # evdev + SharpHook hotkeys, DE writers, Unix socket, file-key secrets
│   ├── Juvin.Platform.Mac/     # SharpHook hotkeys, Keychain, Accessibility permission task
│   ├── Juvin.Platform.Windows/ # SharpHook hotkeys, DPAPI, named pipe, registry host registration
│   ├── Juvin.NativeHost/       # stdio native messaging host, forwards to the app socket
│   └── Juvin.Cli/              # optional: `juvin rewrite --stdin` for scripting and tests
├── extension/                  # TypeScript WebExtension, builds for Chrome and Firefox
├── tests/
├── scripts/                    # packaging: deb/rpm/AppImage, dmg, msix or Inno
└── docs/
```

Platform packages are selected at startup by `OperatingSystem.IsLinux()` and friends; the App project references all three and registers the right one. The screen-conversation workflow later adds `IScreenCapture` to `Juvin.Platform` and an image parameter to the provider interface, with no change to the rewrite pipeline.

## 6. Phases

Estimates assume one developer working with AI assistance and are rough.

### Phase 0: decisions and spikes (1 to 2 weeks)

Goal: remove the unknowns before building anything permanent. Each spike is a throwaway project with a written result.

- Native messaging round trip on Linux (GNOME Wayland, Firefox and Chrome, including Flatpak Firefox), macOS, and Windows.
- Gmail DOM: collect a thread with collapsed messages, read the selection in the reply editor, replace it, confirm Undo works.
- Global hotkey press delivered to the app on all three OSes; on GNOME Wayland via evdev and via a GNOME custom shortcut.
- Avalonia overlay panel: always-on-top, positioned top-right of the display, on GNOME Wayland, macOS, and Windows.
- Local model quality check: three candidate models on a sample thread for rewrite quality and latency.
- Codex CLI: confirm the JSONL event stream from `codex exec --json` yields usable deltas for live preview, and measure latency against the local path.
- Per-file git-history check of every TypeWhisper file slated for copying, confirming it originated in the Linux repository.

Exit: a written go/no-go per platform and the decisions recorded in this document.

Result (October 8, 2026): done. All seven spikes returned go with written results in `docs/spikes/`; the per-platform summaries are in the "Phase 0 results so far" list in section 7.

### Phase 1: solution skeleton and shared UI (2 weeks)

- Solution layout from section 5, CI build on all three OSes.
- Settings persistence, secret storage per OS, localization scaffold.
- Main window with sidebar and the sections from 4.2 as empty shells; shared style resources extracted from the TypeWhisper design.
- Wizard shell with `ISetupTask` and the checklist step.
- Tray icon, single instance, local socket on Unix and named pipe on Windows.

Exit: app launches on all three OSes with the settings window navigable and settings persisting.

Result (October 8, 2026, CI green October 9): done. `Juvin.slnx` builds with 0 warnings and 73 tests pass on Ubuntu, Windows, and macOS in CI (the first Windows CI run exposed two named-pipe bugs on elevated tokens, fixed the same day); the app launched on Fedora (GNOME Wayland), Ubuntu 25.04 (GNOME Wayland) and Windows 11 with the sidebar navigable, the wizard opening on first launch, the tray icon shown where a host exists, and the control socket or named pipe answering `ping`. Settings round-tripped on Fedora. macOS is verified only as far as CI goes: the Unix socket tests pass there, but the Keychain store has no test and the app has never been launched on a Mac (none is available). A shared `Juvin.Platform.Unix` package holds the socket code Linux and macOS share. Launch-at-login is saved but not yet registered with any OS.

### Phase 2: model providers and rewrite pipeline (2 weeks)

- Provider interface, OpenAI-compatible (local) implementation with streaming, Codex CLI implementation with process supervision and output parsing.
- Processing modes and the no-fallback policy.
- Rewrite prompt set with guardrails, "What am I missing?" action, follow-up turns retaining context.
- Provenance record per call and the Context panel data.
- CLI `juvin rewrite` so the pipeline is testable without the extension.
- Models settings section and wizard step 1.

Exit: a thread and draft on stdin produce a streamed rewrite from a local model, with provenance logged.

Result (October 9, 2026): done. `juvin-cli rewrite --stdin` streams a rewrite of the sample thread from the LAN Ollama server (qwen3.5:27b, first words after 9 s, complete in 12 s) and returns one from the signed-in Codex CLI, with the provenance record on stderr. Local mode refuses Ollama cloud models and embedding-only models, fails on replies the server cut short, and sizes the Ollama context window to the conversation. The Models section and the wizard's processing-mode step detect Ollama, LM Studio, and the Codex CLI and recommend a 20B-class model. 218 tests pass.

### Phase 3: browser extension and native host (3 weeks)

- Extension skeleton, MV3, dual build, background relay, popup.
- Native host executable, per-OS registration, wizard step 2 and the Browser connector section.
- Protocol from 4.5 implemented on both sides with a connection test.
- Gmail adapter, generic adapter, bundled test page adapter.
- Completeness states, draft hash, replacement, Undo verification.

Exit: with the desktop app running, the extension command captures a Gmail thread and replaces the draft, end to end, on all three OSes.

Progress (October 10, 2026): all three slices are merged. Phase 3 exit is met on Linux and Windows; macOS remains assumed (no machine).

- Slice 1, connector backbone (PR #4, October 9): `docs/connector-protocol.md` is the contract; `Juvin.Core.Connector` has the framing, message types, `ConnectorSession`, and relay; `IConnectorSocketServer` and client have Unix socket and named pipe implementations; `juvin-host` is a pure byte relay; `ConnectorService` pushes config and captures on the extension shortcut. The extension is MV3 TypeScript with Chrome and Firefox builds, generic and test-page adapters, draft hash and apply guard, and a popup with a connection test. Verified end to end on Fedora, Windows, and Ubuntu (Snap Firefox through the portal); results in `docs/testing/connector-checklist.md`.
- Slice 2, registrar and setup UI (PR #5, October 10): `INativeHostRegistrar` with Linux, macOS, and Windows implementations over a shared `NativeHostManifest`. Linux handles native browsers, Snap Firefox (portal permission pre-approved), and Flatpak Firefox and Zen (host copied into the sandbox; the app binds each registered Flatpak's per-app runtime socket through `IConnectorSocketServer.AddAddressAsync`); Snap Chromium browsers are detected but refused with a message. Detection keys on executables and installations, never on leftover profile directories. A shared `BrowserConnectorPanel` backs the wizard step "Connect your browser" (step 3 of 5) and the Browser connector section: rows with Register or Update, host-missing warning, extension install link, connected extensions, connection test. `juvin-host` ships next to the app through a build-only project reference, and the registrars resolve it on every call. Verified: Firefox and Chrome registered from both the wizard and the section on Fedora; the Windows registrar tests pass against the real registry on the Windows VM; CI green on all three OSes with 367 tests. CI also gained a CodeQL job (PR #6) under the same `ci` label gate; results stay as an artifact until the repository has code scanning.
- Slice 3, Gmail adapter (PR #8, October 10): `gmail.ts` captures the conversation behind a reply draft. It clicks Expand all when rows are collapsed and waits on a wall-clock deadline of about 3 seconds, reads one message per expanded row (sender as `Name <address>`, the date title parsed to ISO 8601, the body without hidden quoted text), and reports `loaded-messages-only` with a `collapsedCount` when rows or an older-messages bundle stay hidden. A new message outside a conversation is `draft-only`; a conversation without the expected structure falls back to the generic capture. Real Gmail taught two things the spike had not: the inline reply editor sits inside the row of the message it answers, so that row stays a message, and a background tab's throttled timers make a sleep count useless as a deadline. `SiteAdapter.captureThread` became async. Verified against the user's mailbox with synthetic drafts on Fedora (Flatpak Zen, Gecko 156) and Windows (Chrome 155): full thread, collapsed thread, two open drafts, Apply with Undo, and a stale apply refused; a real Ctrl+Shift+Y press fires the command on Windows (the default later moved to Ctrl+Shift+F, see the Result). 90 extension tests. Results and the confirmed selectors are in `docs/testing/connector-checklist.md`.
- Known limits carried forward: Flatpak registration needs a self-contained single-file host, which Phase 5 packaging produces (a Debug build reports this on Register); the macOS registrar is verified only by its path tests; the default shortcut Ctrl+Shift+Y collided with Firefox's Downloads binding on Linux (settled in the Result: the default is now Ctrl+Shift+F); the extension is not in any store, so the install link points at `extension/README.md`.

Result (October 10, 2026): done on Linux and Windows; macOS assumed. With the app running, the extension command captures a real Gmail conversation and the draft replacement lands in the editor with Undo intact on Fedora (Flatpak Zen, Gecko 156), Ubuntu 25.04 (Flatpak Zen, Gecko 157, from a copy of the signed-in profile because no browser on that VM held a Gmail session), and Windows 11 (Chrome 155): full thread, collapsed thread expanded within half a second, two open drafts routed by selection, Apply with Undo, and a stale apply refused with `draft-changed`. The connector itself connected, pinged, and captured in Firefox and Chrome on Fedora and in Chrome and Edge on Windows and Ubuntu; Windows Firefox and Snap Firefox on Ubuntu (through the WebExtensions portal) are verified as far as the connection, and no browser has run on macOS. Results, confirmed selectors, and the automation recipes are in `docs/testing/connector-checklist.md`. The review pass after the exit test hardened the adapter: a thread counts as whole only once the rows an older-messages bundle promised have arrived, a capture is refused when the draft editor leaves the page during the wait, a page in a language without labels is reported as `loaded-messages-only`, a body over the 20,000-character limit marks the thread `truncated`, a date title without a year and time yields no `sent`, and a page whose rows have no body element at all falls back to the generic capture. 367 .NET tests and 99 extension tests pass. Carried into Phase 4: the rewrite panel is the first consumer of captures and applies (until then they are exercised from the extension's background console), the macOS registrar is verified only by its path tests, a real key press settled the Linux collision: Firefox's own Ctrl+Shift+Y (Downloads) wins over the extension command, so the default shortcut is now Ctrl+Shift+F (Cmd+Shift+F on macOS) in the manifest and the app settings, verified with real presses in Firefox on Fedora and Chrome on Windows (where Ctrl+Shift+Y no longer fires) and still to be pressed for real on macOS, and Zen's initial empty tab is invisible to `tabs.query` so a capture from it is declined with `no-active-tab`.

### Phase 4: shortcut and rewrite panel (2 weeks)

- Global hotkey per platform, Shortcuts section, wizard step 3, DE shortcut writers on Linux.
- Rewrite panel window: actions, streaming preview, diff toggle, follow-ups, Apply/Copy/Recapture, context chips.
- Apply safety: editor and hash verification, "Draft changed" path.
- Rewrite actions section with editable prompts.
- Stretch: clipboard-only capture for non-browser apps with Copy as the only output.

Exit: the full workflow from section 1 works from the global hotkey.

Progress (October 11, 2026): slice 1, the rewrite panel, is merged (PR #10). Slice 2, the global hotkey, is next.

- Slice 1, rewrite panel: the panel is the first consumer of captures. `RewritePanelService` shows it for every capture the extension's shortcut produces and keeps the connection that sent it, so Apply and Recapture go back to the same tab (through `IRewriteTarget`, which reports a closed connection as "browser disconnected"). Recapture sends the last captured editor id in `context.request`, so the extension reads that editor's tab or answers `editor-gone` instead of reading whichever tab is active; Close or a new capture cancels a Recapture in flight and drops its late answer. The window is a borderless topmost Avalonia window owned by no other window, placed at the top-right of the primary screen's working area like the overlay spike, made sticky on X11 through the new `IPanelWindowSupport` (an `_NET_WM_STATE_STICKY` client message on Linux, explicit no-ops on macOS and Windows), and driven with Show and Hide. Show after Hide was checked on Fedora GNOME Wayland: the window returns to the right place, but Mutter drops the above, skip-taskbar, and sticky states on hide and Avalonia does not resend them, so the panel re-asserts all three after every show. A capture runs `DefaultRewriteAction` (a new setting, Clearer by default) and streams the preview; action chips start a new session over the same capture, a follow-up continues the same session, Cancel and provider failures show an error with Try again and never fall back to the other mode, and capture errors show the extension's code as a message with Recapture. Context chips show the adapter, message count, completeness (with "+N collapsed"), and "Local", "Private server", or "Codex: cloud" with the model; an expander lists the subject and messages that were sent. Apply is the only orange button, is disabled for "What am I missing?" and its follow-ups, and on `draft-changed` shows "Draft changed" with Copy and Recapture. The diff is a Rewrite and Changes toggle rather than side by side, because the panel is 460 pixels wide; `TextDiff` is a word-level diff that groups each changed stretch into one removal and one addition. Verified on Fedora with Firefox 156 under XWayland, the bundled test page, and qwen3.5:27b on the LAN server: the extension command (fired through Marionette) opened the panel and streamed the result, and the buttons, driven through AT-SPI, covered action switch, follow-up, Changes, Copy, Cancel, Recapture, a no-selection capture, Apply with Undo, and an edited draft refused. Logs carried lengths, counts, action ids, and error codes only.
- Still to come in Phase 4: the global hotkey slice (per-OS backends, Shortcuts section, wizard step, DE shortcut writers), the Rewrite actions section with editable prompts and the default action picker, and the Hyprland pin rule for the panel. Windows 11 with Chrome 155 passed the same checks on October 11, 2026 with real Ctrl+Shift+F, Ctrl+Z, and Escape presses from the interactive session: the panel sat topmost over a maximized Chrome at the top-right of the working area and came back there after Escape. macOS behaviour of the panel (floating level, Spaces) is unverified.

### Phase 5: setup wizard completion, history, packaging (2 weeks)

- Remaining wizard steps and per-OS setup tasks, including sandboxed-browser detection.
- History with retention and delete, Privacy and data section.
- Packaging: deb, rpm, AppImage; dmg with notarization; Windows installer. Native host bundled in each.
- Extension store submissions for Chrome Web Store and Firefox Add-ons, which also pins the IDs the host manifests need.

Exit: a fresh machine on each OS can install Juvin, run the wizard, and complete a rewrite.

### Phase 6: hardening and acceptance (1 to 2 weeks)

Run the acceptance scenarios from the research document, section 12:

- Only the draft changes; prior messages stay as context.
- A collapsed message containing a deadline is retrieved or the thread is labeled incomplete.
- Two open drafts never cross-apply.
- Editing the draft during generation makes Apply refuse and offer Copy.
- Cancel or error leaves the original draft untouched.
- Local-model failure never triggers an external request (verified by network capture).
- Offline run in Local mode completes a rewrite.

Plus a privacy policy modeled on TypeWhisper's, and crash reporting that never carries email content.

### After the MVP

In rough order: screen conversation with a vision model (the second research workflow), Outlook on the web adapter, non-browser rewriting through AT-SPI, UI Automation, and macOS Accessibility, Thunderbird, Gmail API retrieval for guaranteed completeness, voice input, plugin loader.

## 6a. Brand

- **Mark:** two flat solid shapes, a large navy form with a pointed upper-right corner and a smaller orange quarter-pill beside it. No gradients or translucent overlap (the earlier two-leaf sprout was rejected for resembling the Univision mark). Color and single-color versions verified at 16, 22, and 32 pixels, including a 16 pixel monochrome render on a real dark panel next to system icons. **Locked October 8, 2026.**
- **Assets:** in `assets/brand/` (see its README). SVG mark in color, white, and black on a square canvas; PNG exports at 16, 22, 24, 32, 48, 128, 256, and 512 pixels; a GNOME `juvin-symbolic.svg`; horizontal and stacked lockups with the text outlined to paths. The vectors were redrawn from the approved board on October 8, 2026 because the first supplied vectors did not match it.
- **Wordmark and tagline:** "Juvin" with "Works beside you".
- **Palette:** primary navy `#0F2D5B` (RGB 15, 45, 91) for the mark, headers, and wordmark; accent orange `#F5A623` (RGB 245, 166, 35) for the Apply button, selected sidebar item border, links, and highlights. Orange is the only accent. Background palette stays dark navy (`#0E131B`, `#141D26`, `#283644`).

## 7. Decisions

Settled on October 8, 2026:

1. **First mail adapter:** Gmail.
2. **License:** GPL-3.0, open source, no TypeWhisper attribution. Only files original to the Linux port may be copied (section 2.7); nothing is copied from commercial references.
3. **Providers in the MVP:** local OpenAI-compatible server and the Codex CLI subscription. API-key providers come later.
4. **Panel location:** a desktop window owned by Juvin, not injected into the page.
5. **Product name:** Juvin.

Settled on October 10, 2026:

6. **Default shortcut:** Ctrl+Shift+F (Cmd+Shift+F on macOS), for both the extension command and the global hotkey, after a real key press showed Firefox's Downloads binding wins over Ctrl+Shift+Y on Linux (section 2.3).

No decisions remain open.

Phase 0 results so far:

- Native messaging: go on Fedora (native Firefox, Chrome, Flatpak Firefox, Flatpak Zen), Ubuntu on GNOME, KDE Plasma, and Hyprland (Snap Firefox through the WebExtensions portal, Chrome), and Windows (Firefox, Chrome, Edge). Fedora has no WebExtensions portal, so its sandboxed browsers run the host inside the sandbox and reach the app over a Unix socket in the per-app runtime directory; Ubuntu's portal launches the host outside the sandbox but shows a consent dialog only to the focused browser, so the extension must connect from a user action. Windows registers per-user registry keys with no elevation. macOS is assumed to work and deferred until a Mac is available; it must be verified before packaging. Details in [spikes/native-messaging.md](spikes/native-messaging.md).
- Gmail DOM: go. The reply editor is found by ARIA attributes, replacement through `insertText` keeps Undo and Redo working, and "Expand all" expands a 25-message thread in one click. Details in [spikes/gmail-dom.md](spikes/gmail-dom.md).
- Global hotkey: go on GNOME, KDE Plasma, and Hyprland (Wayland) and Windows. On Linux, evdev delivers the press with the `input` group or a `uaccess` udev rule (confirmed on Ubuntu without the group, no logout), and each desktop's own shortcut mechanism (GNOME custom shortcut, KDE kglobalaccel entry, Hyprland bind) routed to the local socket works with no privileges; SharpHook sees nothing on Wayland. On Windows, SharpHook's low-level hook delivers with no elevation. macOS deferred. Details in [spikes/global-hotkey.md](spikes/global-hotkey.md).
- Overlay panel: go on GNOME Wayland (Fedora and Ubuntu), KDE Plasma Wayland, Hyprland, and Windows. Avalonia 12 has no Wayland backend, so Linux desktops run the app through XWayland, where a topmost window can be placed at the top-right of the primary screen's working area; Mutter and KWin keep it above focused native Wayland windows, and Hyprland floats it above the tiled layer without a window rule (a pin rule is still wanted for workspace switching). macOS deferred. Details in [spikes/overlay-panel.md](spikes/overlay-panel.md).
- Local model quality: go with a 20B-class model and thinking disabled. On the user's Ollama server, qwen3.5:27b and gemma4:26b rewrote the sample draft faithfully in 13 to 16 seconds; qwen3:8b without thinking barely changed the draft, and with thinking it invented a commitment. Ollama's OpenAI-compatible route streams reasoning in a separate `reasoning` field and ignores the thinking switch, so the provider needs a per-server thinking control and a parser that keeps reasoning out of the preview. Details in [spikes/local-model.md](spikes/local-model.md).
- Codex CLI stream: go for the provider, no for live preview. `codex exec --json` emits the whole agent message in one `item.completed` event with no deltas; 13 seconds wall clock for a short rewrite. The provider is non-streaming and the panel shows progress instead of words in Codex mode. Details in [spikes/codex-stream.md](spikes/codex-stream.md).
- Copy origin check: 89 of 95 files confirmed original to the Linux repository (the main window was missed by the first run's path pattern and added on October 9, 2026 with the same verdict). Six trace back to upstream Windows code through renames (`ILlmProviderRole.cs` and the General, History, Appearance, Shortcuts, and Advanced section code-behinds) and are removed from the reuse list below. Details in [spikes/copy-history.md](spikes/copy-history.md).

## 8. Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Gmail DOM changes break the adapter | Capture fails silently | Adapter self-check with the bundled test page, version-gated selectors, generic fallback with "Visible text only" label |
| Sandboxed Firefox or Chrome cannot launch the native host | No connector on some Linux installs | Detect in wizard, document the workaround, extension command plus clipboard path as degraded mode |
| Global hotkey unreliable on GNOME Wayland | Trigger fails | Extension command is the guaranteed trigger; evdev path reuses proven TypeWhisper code |
| Avalonia always-on-top panel misbehaves on Wayland | Panel hidden behind browser | Phase 0 spike; fallback is a normal window raised by the compositor |
| Local models produce weak rewrites | Core value unmet | Phase 0 model check; prompt set tuned on real threads; a LAN server for bigger hardware; Codex as the explicit higher-quality option |
| Codex CLI changes its flags or output format | Codex provider breaks on update | Pin the verified version range, probe with `codex exec --help` at startup, show a clear "Codex needs an update" state instead of failing mid-rewrite |
| Chrome extension ID instability during development | Host manifest allowlist mismatch | Pin a `key` in the dev manifest from day one |
