# Privacy Policy

Juvin does not collect telemetry or analytics. The one thing it can send to its author is a crash report, described below, and that report never carries email content.

Everything else Juvin does with your email happens on your computer, or on a machine you chose, under a setting you can see.

## What Juvin reads

When you press the shortcut on a draft, the browser extension reads the draft, the subject, and the messages of the conversation it replies to (sender, recipients, date, and body). It reads nothing until you ask, and nothing from pages it is not allowed on: the extension runs on Gmail only unless you grant it access to another site yourself.

The extension keeps no settings and no history, and talks to no server. It hands the capture to the desktop app through the browser's native messaging host, `juvin-host`, which relays the bytes to the running app over a local socket and never parses, logs, or stores them.

## What leaves your computer

Where the draft and conversation go depends on the processing mode you chose in setup. In either mode the content sent is the draft, the conversation, the rewrite instruction, and the sender name you entered in Juvin's settings.

- **Local mode** (the default) sends the content to the OpenAI-compatible server at the address in your settings, `http://localhost:11434` by default. If you point Juvin at a server on your network, the content goes to that machine instead. Juvin never falls back to any other provider: if the server fails, it shows the error and sends nothing elsewhere.
- **Codex mode** passes the content to the Codex command-line tool installed on your computer, which sends it to OpenAI under your own Codex sign-in. Juvin runs Codex in a read-only sandbox, from an empty temporary folder so it starts with no project files or instructions, and asks it not to keep a session. The sandbox stops Codex from changing files; it does not stop the model from reading files on your computer if it decides to. Juvin stores no key or token for Codex. What OpenAI retains is governed by OpenAI's terms and your Codex account settings.

Juvin makes no other network connections. There are no update checks, license checks, or usage pings.

## What Juvin keeps on your computer

Juvin's data folder is `~/.local/share/Juvin` on Linux and `%LOCALAPPDATA%\Juvin` on Windows; Privacy and data shows the exact path on your system and opens the folder. The folder is readable only by your user account.

- **Settings** (`settings.json`): the processing mode, server address, model names, your sender name, the shortcut, custom actions and instruction edits, and the switches below. Juvin keeps a last-known-good copy as `settings.json.bak` and quarantines an unreadable copy as `settings.json.broken-*`.
- **Rewrite history** (`history.json`), off by default: with the switch on, each completed rewrite keeps the draft, the result, the subject, the action, the mode, provider, and model, the message count, and timing. Entries older than the retention period (30 days by default) are removed when you change the period and each time Juvin starts. The same backup and quarantine copies exist as for settings.
- **Provenance capture**, off by default: adds the exact prompt and messages sent for each rewrite to its history entry.
- **Secrets** (`Secrets/` and `secret.key`): the folder and the key exist for provider credentials. No provider in the current version needs one, so they hold nothing.
- **Runtime** (`Runtime/`): the local socket the native host connects to. It holds no content.

Juvin writes no log files. Diagnostic output goes to the terminal that started it and carries message types, lengths, identifiers, and error types, never the draft, the conversation, a prompt, or a response.

Exporting history writes a Markdown or JSON file wherever you choose; that file is yours to keep or delete.

**Clear history** removes every entry and the backup and quarantine copies of the history file. **Delete all data** removes history, the secrets, the launch-at-login entry, and resets settings with their copies, and setup runs again at the next start.

## What Juvin writes outside its data folder

Setup registers the native messaging host with Firefox and Chrome by writing a small manifest in each browser's per-user folder (a registry key on Windows), and registers the shortcut with your desktop environment where one is needed. Launch at login, off by default, adds an autostart entry, a launch agent, or a Run key, and turning the switch off removes it. None of these files holds email content.

## Crash reports

Crash reporting is on by default. The choice is the first thing the setup wizard shows, and it can be changed at any time under Privacy and data. No report leaves before you have seen that choice. Reports go to a Sentry project owned by Excel on the Web. Sentry is a hosted service of Functional Software, Inc. d/b/a Sentry (sentry.io, US region), which receives and stores the reports on Excel on the Web's behalf.

A report includes:

- The Juvin version and the build's environment.
- The operating system name and version and the .NET runtime version.
- The exception types and the stack frames of Juvin's own code, as module and function names only.
- Which part of Juvin was running when it crashed, and error facts such as an HTTP status or socket error code where one applies.

A report never includes:

- The draft, the conversation, a prompt, a model response, or any error message text. Every message is replaced with `<redacted>` before the report is built.
- File names or paths, your user name, home folder, machine name, or time zone.
- Keys, tokens, URLs, or server addresses.
- Any persistent install or user identifier. Reports carry no IP-address field and the SDK's automatic IP capture is off; like any HTTPS service, Sentry's servers still see the connection's source address. IP storage is turned off in the project, so Sentry does not keep it, though it may derive a coarse location from it when the report arrives.

No data is written to disk for this feature. Turning the switch off stops reporting immediately and drops any report still waiting to be sent. A build made without a crash reporting endpoint cannot send reports, and its switch stays disabled.

## Questions

Open an issue at <https://github.com/csmashe/juvin/issues>.
