# Spike: Gmail DOM capture, replacement, and Undo

Date: October 8, 2026. Phase 0 item 2 of `docs/mvp-scope.md`. Result: **go**. Every question the spike asked has a working answer, and the timings are well inside what the rewrite flow needs.

Throwaway code lives in `spikes/gmail-dom/` (see its README to rerun). No message text was recorded; the reports hold counts, lengths, flags, and hash prefixes only.

## Setup

- Zen 1.22.3b (Gecko 156), launched by web-ext as a second instance on a temporary copy of the signed-in profile, so the user's live browser and profile were never touched. The copy is deleted when web-ext exits.
- A temporary extension with one content script on `mail.google.com`. It picked a thread from the inbox by message count, ran the capture, and posted the result to a local collector.
- Three runs: one full run on a single-message thread (editor path), two thread-only runs on a 25-message thread (capture path). The full run created one reply draft and discarded it; nothing was sent.

## Results

| Question | Answer | Time |
| --- | --- | --- |
| Find the reply editor | `div[contenteditable="true"][role="textbox"]` with `aria-label="Message Body"` and `g_editable="true"` | immediate |
| Open a reply when no editor is open | the visible "Reply" link at the end of the thread; editor appears within 300 ms | under 1 s |
| Read the selection | `window.getSelection()` inside the editor; a select-all covered the whole draft exactly, hash matched | immediate |
| Replace the selection | `document.execCommand("insertText")` on the editor; text matched the replacement exactly | 300 ms wait |
| Undo | `execCommand("undo")` restored the original draft byte for byte (same hash); Redo re-applied the replacement | 300 ms wait |
| Discard the test draft | `[aria-label^="Discard draft"]` click; editor gone | about 1 s |
| Collect a 25-message thread | one click on "Expand all" went from 3 visible messages and a "22 older messages" bundle to 25 expanded messages | 2.1 s total |
| Per-message metadata | every message had a sender element with an `email` attribute and a date element | immediate |

## Findings

1. **Replacement through the editor's own command keeps Undo intact.** `insertText` on the contenteditable goes through Gmail's editing stack, so Undo and Redo behave as if the user had typed. This is the mechanism the adapter must use; never set `innerHTML` or `textContent`.
2. **Gmail hides most of a long thread.** A 25-message thread rendered 3 or 4 message rows plus an "N older messages" bundle. Clicking the bundle reveals all rows but leaves them collapsed, and each collapsed header needs its own click (about one message per second in the loop). The "Expand all" control in the thread toolbar (`[aria-label="Expand all"]`) does the whole thread in one click. The adapter should use it, then snapshot. If the control is absent, fall back to the bundle and header clicks and label the thread incomplete when any row stays collapsed.
3. **Expanding changes the page.** Expand all leaves the thread fully expanded for the user. That is visible but harmless; the capture should happen once per rewrite and the completeness state should say when it had to expand.
4. **Quoted and trimmed content is in the DOM but hidden.** Expanded messages carried quoted sections and a "Show trimmed content" toggle. `innerText` excludes the hidden parts, so the capture sees each message's own text, not the quoted history, which is what the rewrite wants.
5. **The inbox row count is in the sender cell.** The message count for a conversation row appears as a trailing number in `td.yX`. A first attempt matched a phone number in the row text, so the adapter self-check must assert the count comes from that cell, not from the row.
6. **Selectors are Gmail's obfuscated class names** (`tr.zA`, `td.yX`, `div.a3s`, `span.gD`, `span.g3`) plus stable ARIA attributes. The ARIA attributes carried every step that matters: editor, Reply, Expand all, Discard. The adapter should prefer those and treat the class names as version-gated hints with the self-check against the bundled test page, as the scope document already says.

## Consequences for the design

- The Gmail adapter's capture is: find the editor, read the selection and hash the draft, click Expand all if present, snapshot `div[role="main"] div[role="listitem"]` rows into sender, date, and body text, and report `complete` only when no row is collapsed and no bundle label remains.
- Apply is `insertText` into the editor over the current selection, after the editor identity and draft hash check. Undo stays with the user.
- The ARIA labels are localized. The MVP ships English only; the adapter should keep the labels in one table so other locales can be added without touching the logic.

## Not covered by this spike

- Chrome. The editor and thread DOM are the same, but the run was Gecko only; a Chrome pass belongs in the Phase 3 adapter self-check.
- Gmail in a non-English locale, Gmail's basic HTML view, and the pop-out compose window.
- Two open drafts at once. The acceptance scenario about cross-apply needs the hash check, which is a Phase 3 concern.
