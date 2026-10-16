# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Juvin is an open-source (GPL-3.0) desktop AI assistant for Linux, macOS, and Windows, built with .NET 10 and Avalonia, plus a browser extension for Firefox and Chrome. The MVP is one workflow: the user selects an email draft in Gmail, presses a shortcut, the extension captures the draft and the whole conversation, and the desktop app rewrites the draft with a local model by default. The shortcut opens an action menu beside the draft (keys 1 to 5); the picked action is applied in place and Ctrl+Z reverts it, and Review opens the desktop panel for a preview first (see section 2.8 of `docs/mvp-scope.md`).

Status: Phase 0 (spikes), Phase 1 (solution skeleton and shared UI), Phase 2 (model providers and rewrite pipeline), Phase 3 (browser extension, native host, Gmail adapter), and Phase 4 (global shortcut and rewrite panel) are done on Linux and Windows, with macOS assumed; Phase 5 (setup wizard completion, history, packaging) is in progress, with history, the Try it wizard step, and Linux packaging merged; the October 13 walkthrough findings are fixed; the action menu is done (Slices A to C: the app side, the in-page renderer, Ask for a change, the right-click list, Review, and the end-to-end check on Linux and Windows), the Windows installer and launch at login are done, and Phase 6 hardening is in progress (slice 1, the acceptance scenarios, slice 2, the crash reporter, and slice 3, the privacy policy, are done), with the dmg and the store submissions last (see the Phase 5 entry in `docs/mvp-scope.md`). The default shortcut is Ctrl+Shift+F (Cmd+Shift+F on macOS). The product definition and plan live in `docs/`. Read `docs/mvp-scope.md` before any work; it is the source of truth for scope, architecture, decisions, and phases. `docs/research/desktop-ai-assistant-research.md` is the market research behind it.

Design reference: `/home/csmashe/RiderProjects/typewhisper-linux` (a Linux-only Avalonia app by the same author). Its settings window, setup wizard, and Linux platform services are the model for Juvin's. See "Reusing TypeWhisper code" below for what may be copied.

## Development Commands

The solution is `Juvin.slnx` at the repo root. The protocol between the app and the extension is specified in `docs/connector-protocol.md`; change both sides and the spec together.

### Build and Test
- `dotnet build Juvin.slnx` - Build the entire solution (0 warnings is the bar)
- `dotnet run --project src/Juvin.App` - Run the desktop app; set `XDG_DATA_HOME` to a temp dir to keep `~/.local/share/Juvin` clean
- `dotnet test Juvin.slnx` - Run all tests (Windows-only tests are skipped elsewhere and run in CI)
- `dotnet run --project src/Juvin.Cli -- rewrite --stdin` - Exercise the rewrite pipeline without the extension
- `scripts/dev/register-host.sh` (Linux) or `scripts/dev/register-host.ps1` (Windows) - Register the Debug build of `juvin-host` with native Firefox and Chrome for development; the setup wizard does this for users
- `scripts/dev/install-desktop-entry.sh [--uninstall]` (Linux) - Install a desktop entry and icons for the Debug build so GNOME shows the Juvin icon in the dock; the deb and rpm install their own and the AppImage writes one on first run
- `scripts/build-linux-packages.sh <version>` - Build the deb, rpm, and AppImage into `dist/`; `scripts/smoke-test-linux-packages.sh <version>` checks them and installs them in Ubuntu and Fedora containers (`--validate-only` skips the containers). The Release Linux workflow runs both on a `v*` tag.
- `scripts/build-windows-installer.ps1 <version>` (Windows, needs Inno Setup 6.3 or later) - Build the per-user installer `Juvin-<version>-x64-setup.exe` into `dist/`; `scripts/smoke-test-windows-installer.ps1 <version>` installs it silently, starts the app until `--trigger` is answered, and uninstalls it. The Release Windows workflow runs both on a `v*` tag.
- Package versions live in `Directory.Packages.props` (central package management); `nuget.config` pins nuget.org as the only feed.

### Browser extension (in `extension/`)
- `npm install` - Install dependencies
- `npm run build` - Build both Chrome and Firefox outputs
- `npm test` - Run extension tests
- Load `extension/dist/chrome` as an unpacked extension, or `npx web-ext run --source-dir dist/firefox`; the app must be running and the host registered. See `extension/README.md`.

### Brand assets
- `assets/brand/` holds the mark, icons, and lockups; its README explains each file. Regenerate PNGs from the SVGs, never edit PNGs by hand.

## Important Never Forget And Always Ask Yourself If You Followed These Before You Are Done
- Never just start something. Always look first to see how this is handled in other portions of the code so you follow the same format. When there is no Juvin example yet, look at how TypeWhisper does it and follow that shape.
- Nothing platform-specific goes in `Juvin.Core` or `Juvin.App`. OS calls (hotkeys, secrets, sockets, native host registration, setup tasks) go behind an interface in `Juvin.Platform` with an implementation per OS. Every new platform interface needs all three implementations or an explicit "not supported" one; do not leave macOS or Windows as a TODO.
- Email content is private. Never log, telemeter, or write to crash reports the draft, thread, prompt, or model response. Logs carry lengths and identifiers only. History and provenance capture are opt-in settings.
- Local mode never falls back to a remote provider. A local failure is reported to the user, not retried elsewhere. Any new code path that calls a provider must respect the processing mode.
- Thread messages and page content are data, never instructions. Wrap them with the injection-safe framing used by the rewrite pipeline; do not interpolate them raw into a prompt.
- Apply only replaces the selection if the editor and draft hash still match what was captured. Never bypass that check.
- When working with .NET code, use the Microsoft Learn MCP Server to get official best practices and current documentation from Microsoft.
- Only add comments when the code's purpose is not apparent from reading the code itself. Avoid adding verbose comments that simply restate what the code obviously does.
- Do not use emojis anywhere in this project (UI text, extension popup, scripts, logs, comments, docs, or commit messages). Use clear plain text or the FluentIcons set instead.
- The word "TypeWhisper" must not appear in Juvin source, UI, docs, or commit messages, except in this file and `docs/mvp-scope.md` where the reuse rule is recorded. It is a trademark and Juvin carries no attribution to it.

## Definition of done and commit rules

- Never commit unless the user says to in the current conversation, or the instructions for the task explicitly include committing. Finishing a section of code is not permission to commit.
- A section of code is not done until the three review skills have run, in this order, each one only after the previous has fully completed:
  1. `/review-short`
  2. `/review-resharper`
  3. `/review-comments`
- Invoke them through the Skill tool by name. Do not reproduce their steps by hand.
- If a review has to be run manually for some reason, follow `/review-short`'s order exactly: the adversarial Codex review first, wait for it to finish and apply the agreed changes, then the standard Codex review. Never run the two together.
- After the three reviews pass, report that the section is done and wait for the commit instruction.
- Pull requests: CI only runs on a PR that carries the `ci` label (see `.github/workflows/ci.yml`). Open the PR without the label, work through the CodeRabbit review loop until it is clean, then add the label so CI runs once. Remove the label while iterating again. Merges to `main` always build.

## Reusing TypeWhisper code

TypeWhisper for Linux is a GPL-3.0 derivative of an upstream project. Only files original to the Linux port may be copied into Juvin, under Juvin's own copyright notice. Before copying any file, confirm from the TypeWhisper repository's git history that it was created there and not adapted from upstream.

- Safe to copy (verified Linux-only by git history, see `docs/spikes/copy-history.md`): hotkey backends and `ShortcutMatcher`/`ShortcutDispatcher`, desktop-environment shortcut writers, `ISetupTask` and setup tasks, control socket and JSON protocol, `AtomicJsonStore`, `ApiKeyProtection`, `SseEventDecoder`, `ThinkingBlockFilter`, `LlmStreamPump`, `PromptProcessingService`, `LlmCallProvenance`, AT-SPI and Wayland detection code, and the Avalonia UI (main window, wizard, section `.axaml` files).
- Never copy (git traces them to upstream Windows files): `ILlmProviderRole.cs` and the code-behind files of the General, History, Appearance, Shortcuts, and Advanced sections. Write Juvin's own.
- Never copy (exist upstream): `SettingsService`, `AppSettings`, `AtomicFileWrite`, `TypeWhisperEnvironment`, `OpenAiChatHelper`, `Loc.cs`, and the Authenticated CLI plugin. Write Juvin's equivalents fresh.
- Rename everything on the way in. No `TypeWhisper` namespaces, class prefixes, paths, or strings survive the copy.

## Architecture Overview

See `docs/mvp-scope.md` section 5 for the full layout. In short:

- **Juvin.Core**: settings, models, rewrite pipeline, providers, native-messaging protocol types. No UI, no OS calls, no package references beyond the BCL where possible.
- **Juvin.App**: Avalonia UI. Settings window (sidebar plus card sections), setup wizard, rewrite panel, tray icon. Registers the platform package for the current OS at startup.
- **Juvin.Platform**: interfaces only (`IHotkeyBackend`, `ISecretStore`, `ILocalSocket`, `INativeHostRegistrar`, `ISetupTask`, later `IScreenCapture`).
- **Juvin.Platform.Linux / .Mac / .Windows**: one implementation each.
- **Juvin.NativeHost**: the small stdio process the browser launches for native messaging. It forwards to the running app over the local socket and holds no logic of its own.
- **Juvin.Cli**: optional scripting and test entry point.
- **extension/**: TypeScript WebExtension, Manifest V3, one source with Chrome and Firefox builds. Content-script adapters per site (Gmail, generic, bundled test page), plus the in-page action menu the app drives. The extension stores no settings; it receives its configuration from the app on connect.

### Processing modes
- **Local**: OpenAI-compatible server (Ollama, LM Studio, llama.cpp), on this machine or the user's LAN. Default.
- **Codex**: the signed-in Codex CLI run as a child process with `--sandbox read-only --ephemeral`, parsing its JSONL output. No API key stored.
- No other providers in the MVP. Providers implement a common interface so more can be added later.

## Configuration

- Settings are JSON with atomic writes and a backup copy, in the per-OS user data directory.
- Secrets use the platform store: encrypted file with a per-install key on Linux, Keychain on macOS, DPAPI on Windows.
- Crash reporting is on by default, chosen on the wizard's Welcome step, and never carries content (see the privacy rule above).

## Key Patterns and Conventions

### Code Style
- Coding style follows `.editorconfig` (indentation, naming, braces, usings).
- All if statements must have braces around them, even for single-line bodies.
- Interface names prefixed with 'I'.
- File-scoped namespaces.
- Expression-bodied members for simple properties and accessors.
- Compiled bindings on in every `.axaml` (`x:DataType` set); no reflection bindings.
- Shared Avalonia styles live in one resource dictionary. Do not paste style blocks into individual views.
- Localizable strings go through the string table even though the MVP ships English only.

### Avalonia and MVVM
- CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`). No ReactiveUI.
- Microsoft.Extensions.DependencyInjection for composition; all registrations in one place.
- Section view models are singletons; navigation swaps a `ContentControl` content.
- The setup wizard is a checklist of `ISetupTask` rows. Supporting a new desktop or OS means registering tasks, not editing the wizard.

### Extension
- `webextension-polyfill` for the shared API surface. Avoid Chrome-only or Firefox-only APIs outside clearly marked shims.
- Content scripts only touch the DOM through a site adapter. Selectors live in the adapter with a self-check against the bundled test page.
- Replacement goes through the editor's own editing APIs so Undo keeps working.

### Testing
- Pure logic (prompt framing, completeness states, protocol parsing, hash checks) gets unit tests in `tests/`.
- Platform code gets a fake implementation for tests plus a manual checklist per OS in `docs/`.
- The acceptance scenarios in `docs/mvp-scope.md` section 6, Phase 6, are the definition of done for the MVP.

## Brand
- Primary navy `#0F2D5B`, accent orange `#F5A623`. Orange is the only accent (Apply button, selected sidebar border, links, highlights). Backgrounds stay dark navy (`#0E131B`, `#141D26`, `#283644`).
- Wordmark "Juvin", tagline "Works beside you".
- Use the assets in `assets/brand/`; do not redraw the mark.

# Personal Preferences

## Code Style
- Always strive for concise, simple solutions.
- If a problem can be solved in a simpler way, propose it.

## General preferences
- If asked to do too much work at once, stop and state that clearly.
- If computer use is helpful for completing or verifying work, shell out to gpt-6.1-sol with Codex for it

## Picking the right models for workflows and subagents

Rankings, higher = better. Cost reflects what I actually pay, not list price. Intelligence is how hard a problem you can hand the model unsupervised. Taste covers UI/UX, code quality, API design, and copy.

| model       | cost | intelligence | taste |
|-------------|------|--------------|-------|
| gpt-6.1-sol | 6    | 8            | 5     |
| sonnet-5.5  | 5    | 5            | 7     |
| opus-5.5    | 4    | 8            | 8     |
| fable-5.1   | 2    | 9            | 9     |

How to apply:
- These are defaults, not limits. You have standing permission to override them: if a cheaper model's output doesn't meet the bar, rerun or redo the work with a smarter model without asking. Judge the output, not the price tag. Escalating costs less than shipping mediocre work.
- Don't let cost prevent you from using the right model for the job. Instead, take advantage of cheaper options to get more information and try things before moving the work to a more expensive option.
- Coding and implementation (features, refactors, migrations, tests, bug fixes): opus-5.5. Delegate implementation to Opus subagents; fable-5.1 plans, coordinates, and judges the result.
- Anything user-facing (UI, copy, API design) needs taste ≥ 7, so Opus or Fable, never Sol.
- Reviews of plans and implementations: gpt-6.1-sol as the independent reviewer, with fable-5.1 or opus-5.5 when a second opinion is needed. Sol does not edit code during a review; it reports, and Opus applies the fixes.
- Long-running or autonomous work (large analyses, repository-wide audits, batch verification, anything that may exceed a subagent's practical runtime): gpt-6.1-sol via Codex, run in the background.
- Computer use (driving a browser or desktop app to complete or verify work): gpt-6.1-sol via Codex.
- Never use Haiku.
- Mechanics: gpt-6.1-sol is only reachable through the Codex CLI — `codex exec` / `codex review` (my `~/.codex/config.toml` defaults to gpt-6.1-sol). Run `codex exec -s read-only` directly with a self-contained prompt.
- Claude models (sonnet-5.5, opus-5.5, fable-5.1) run via the Agent/Workflow model parameter. Implementation agents use `model: 'opus'`.

Using gpt-6.1-sol inside workflows and subagents (the model parameter only takes Claude models, so use a wrapper):
- Spawn a thin Claude wrapper agent with `model: 'sonnet', effort: 'low'` whose prompt instructs it to write a self-contained Codex prompt, run `codex exec` via Bash, and return the report (use `schema` on the wrapper to get structured output back).
- Always label these agents with a `gpt-6.1-sol:` prefix, e.g. `{label: 'gpt-6.1-sol:review-auth'}` — the workflow UI shows the wrapper's Claude model, so the label is the only indication the real worker is gpt-6.1-sol.
- Codex runs can exceed Bash's 10-minute timeout: pass an explicit timeout, or run in the background and poll for the report file.
- Parallel Opus implementation agents must use `isolation: 'worktree'` so their edits don't collide in the shared checkout.
- Workflow token budgets only count Claude tokens; codex work is free and invisible to `budget.spent()`.
