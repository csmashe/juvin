# Acceptance checklist

The seven scenarios from `docs/mvp-scope.md`, Phase 6, are the definition of done for the MVP. Each one lists the unit tests that pin the behavior and the steps that only a real browser, model, or network can show. Run the steps on each OS before the first release and after any change to capture, apply, the providers, or the action menu. The log lines carry counts and codes only, never content; use synthetic drafts.

## Scenarios

### 1. Only the draft changes

The prior messages stay context; a rewrite replaces the selection in the draft and nothing else on the page.

- Tests: `applyReplacement` "replaces the captured range when the draft is unchanged" and "applies the offsets of the request, not of the latest capture"; Gmail adapter "applies a rewrite into the reply draft, leaving its hidden quoted text alone"; `RewritePromptBuilderTests.Build_WrapsThreadAndDraftAsDataAndEndsWithTheInstruction`.
- Steps: in a three-message Gmail conversation, type a two-line reply, select one line, and run Make clearer. Only the selected line changes; the other line, the quoted history, and the earlier messages are untouched. Ctrl+Z (Cmd+Z on macOS) restores the line. Already covered by the connector checklist's Gmail steps 1 and 4.

### 2. A collapsed message with a deadline is retrieved or the thread is labeled incomplete

- Tests: Gmail adapter "clicks Expand all and waits for the collapsed messages to open", "counts the bundle's messages as hidden when they never arrive", "reports truncated over loaded-messages-only and keeps the hidden count", "gives up at the deadline when Expand all opens nothing", and "never reports the whole thread on a page in a language it has no labels for"; `RewritePromptBuilderTests.Build_MentionsCollapsedMessages`; `RewritePanelViewModelTests.TheContextChips_DescribeTheCapture`.
- Steps: in a conversation long enough for an "N older messages" bundle, put a deadline in one of the bundled messages, reply, select, and run What am I missing?. Gmail expands the thread, the log shows `EntireThread`, and the answer names the deadline. Then block the expansion (a conversation whose bundle does not open within the deadline, or the adapter on a page in another language) and capture again: the log shows `LoadedMessagesOnly` with the hidden count, the right-click list's context fact says so, and the prompt tells the model messages are missing. The expansion half is covered by the connector checklist's Gmail step 2.

### 3. Two open drafts never cross-apply

- Tests: Gmail adapter "captures the draft that holds the selection when two are open"; `EditorTabs` "returns nothing for an unknown editor rather than another tab"; `MenuRenderer` "answers editor-gone for an unknown editor"; `applyReplacement` "reports editor-gone for an unknown or removed editor"; `RewritePanelViewModelTests.Recapture_AsksForTheEditorOfTheLatestCapture`.
- Steps: open a reply and a new message window at the same time. Select text in the new message and run an action: the result lands in the new message. Select text in the reply and run an action: the result lands in the reply. Close the reply while its action runs: the apply is refused with `editor-gone` and the new message is untouched. The first two are covered by the connector checklist's Gmail step 3.

### 4. Editing the draft during generation makes Apply refuse

- Tests: `applyReplacement` "refuses with draft-changed when the hash does not match" and "refuses with draft-changed when the draft is edited while the hash is computed"; `ActionMenuServiceTests.ADraftChangedApply_SendsTheResult`; `RewritePanelViewModelTests.ADraftChangedApply_OffersCopyAndRecapture`.
- Steps: run an action and type one character in the draft before it finishes. The menu shows the draft-changed card with Copy and Recapture, the draft stays as typed, and Copy puts the result on the clipboard. Covered by the connector checklist's Gmail step 5 and the action menu results.

### 5. Cancel or error leaves the original draft untouched

- Tests: `RewritePanelViewModelTests.Cancel_StopsTheRunAndOffersRetry` and `AFailedRun_KeepsNothing` (nothing is applied); `ActionMenuServiceTests.ACloseFromTheExtension_CancelsTheRun` and `AProviderFailure_SendsARetryableErrorAndNeverFallsBack` (no `apply.request` goes out); `applyReplacement` "rolls back and reports internal when the editor ends up with other text"; `RewritePanelViewModelTests.Copy_WritesTheResultAndShowsTheHint`.
- Steps: run an action and press Escape while it runs: the menu closes and the draft is unchanged. Point Local mode at a server that is not running and run an action: the error card says the local model server at that address could not be reached, offers Try again, and the draft is unchanged. Put text on the clipboard, run an action, and let it apply: the clipboard still holds that text, since Apply goes through the editor and never through the clipboard (on GNOME over SSH, `wl-copy` and `wl-paste` hang waiting for a focused surface; write and read the clipboard from the page with `navigator.clipboard` after granting the permission over the DevTools protocol). Copy from a result card replaces the clipboard only then.

### 6. A local-model failure never triggers an external request

- Tests: `RewriteSessionTests.Session_UsesOnlyTheConfiguredModeEvenWhenItFails`, `Selector_RejectsAFactoryThatReturnsAnotherMode`; `ActionMenuServiceTests.AProviderFailure_SendsARetryableErrorAndNeverFallsBack`; `RewritePanelViewModelTests.AProviderFailure_ShowsTheMessageAndIsNotRetried`.
- Steps: in Local mode with the server address pointed at a port nothing listens on, start a capture of every packet the machine sends except SSH, over TCP and UDP on IPv4 and IPv6 (`sudo tcpdump -i any -nn -l 'not port 22'`), so a request over an already open connection or over QUIC is caught as well as a new connection. Start the app after the capture, so it has no connection open from before. Attach `sudo bpftrace scripts/dev/trace-connects.bt <juvin pid>` (Linux) as soon as the app has started and before any action runs, to list every connect and every addressed datagram send the app makes from then on, which together name every destination it reaches, since data on a connected socket only goes to the peer the connect named (a socket connected before the trace attached shows only in the packet capture, which is why the capture starts first); `ss` sampling misses a refused loopback connect, and strace stalls the .NET runtime. Run an action, wait for the error card, press Try again, and wait for the second error card. The trace lists the configured address only, and every other destination in the capture belongs to another process (the browser's own traffic, DNS, NTP, the desktop). Nothing from Juvin goes to any other host, with or without Codex installed on the machine.

### 7. An offline run in Local mode completes a rewrite

- Tests: none; this is a network scenario.
- Steps: with a model server on the machine or on the LAN, remove the IPv4 and IPv6 default routes (`sudo ip route del default` and, where one exists, `sudo ip -6 route del default` on Linux) so the machine has no default route and the internet is out of reach, confirm with `ip route` and `ip -6 route` showing no default and `curl -m 5 https://example.com` failing, start the same capture as scenario 6, and run Make clearer. The rewrite applies and the capture shows connections to the model server only. Restore the routes afterwards.

## Results

| Date | OS | Browser and page | Model | 1 Only the draft | 2 Collapsed thread | 3 Two drafts | 4 Edited draft | 5 Cancel and error | 6 Local failure, no external request | 7 Offline |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| October 15, 2026 | Ubuntu 25.04, GNOME 48 Wayland (VM 820) | Chrome 155, test page, 3 messages, EntireThread | qwen3.5:27b on the LAN server | Covered by the Gmail rows of the connector checklist (October 10) | Covered by the Gmail rows of the connector checklist (October 10) | Covered by the Gmail rows of the connector checklist (October 10) | Covered by the Gmail rows and the action menu rows of the connector checklist (October 10 and 14) | Pass: Escape 160 ms after Running closed the menu and the editor's HTML and text were byte-identical, then and 26 s later; a server on a closed port gave the error card with Try again and the editor unchanged; a sentinel written to the clipboard was still there after an apply that changed the editor | Pass: with the server at 127.0.0.1:11435, a capture of all TCP and UDP traffic over the action, the error, Try again, and the second error held two connection attempts to that port and nothing else from Juvin; the kernel trace of the Juvin process listed exactly two connect calls, both to 127.0.0.1:11435, and no addressed sends; the capture's other destinations were Chrome's (Google hosts, attributed by `ss -tunp`), the JetBrains Toolbox, GNOME Shell, DNS, mDNS, and NTP | Pass: with the IPv4 default route removed (example.com unreachable, the LAN server reachable; the VM had no IPv6 default route), Make clearer applied in 9.3 s and the capture held one SYN, to 10.0.3.20:11434 |
