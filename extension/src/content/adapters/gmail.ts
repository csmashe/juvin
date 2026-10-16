import type { ThreadMessage } from "../../shared/protocol";
import { isRendered, readPlainText } from "../plain-text";
import { editorForSelection, type SiteAdapter, type ThreadSnapshot } from "./adapter";
import { genericAdapter, normalize, PAGE_TEXT_LIMIT, truncate } from "./generic";

// Gmail localizes these; add locales here. A page in a language without an entry is never reported as the whole thread.
const LABELS = {
  messageBody: "Message Body",
  expandAll: "Expand all",
  olderMessages: /^(\d+)\s+older messages?\b/i,
};

// Gmail's obfuscated class names; captureThread falls back to the generic capture when the body class is gone.
const SELECTORS = {
  main: 'div[role="main"]',
  editor: 'div[contenteditable="true"][role="textbox"]',
  expandAll: `[aria-label="${LABELS.expandAll}"], [data-tooltip="${LABELS.expandAll}"]`,
  message: 'div[role="listitem"]',
  olderMessages: '[role="button"], [role="link"], span',
  subject: "h2[data-thread-perm-id], h2.hP",
  sender: "span.gD[email], span[email]",
  date: "span.g3[title]",
  body: "div.a3s",
};

/** Polls after "Expand all" until the thread stops changing for this many polls or the deadline passes. */
const STABLE_POLLS = 3;

export interface GmailTiming {
  sleep(ms: number): Promise<void>;
  now(): number;
  pollMs: number;
  deadlineMs: number;
}

const DEFAULT_TIMING: GmailTiming = {
  sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
  now: () => performance.now(),
  pollMs: 150,
  deadlineMs: 3000,
};

interface ThreadState {
  rows: Element[];
  collapsed: number;
  older: number;
}

function isGmailEditor(element: Element): boolean {
  return element.matches(SELECTORS.editor)
    && (element.getAttribute("g_editable") === "true" || element.getAttribute("aria-label") === LABELS.messageBody);
}

function bodyOf(row: Element, editor: HTMLElement): HTMLElement | null {
  const body = row.querySelector<HTMLElement>(SELECTORS.body);
  return body && !editor.contains(body) && isRendered(body) ? body : null;
}

function olderMessages(main: Element, editor: HTMLElement): number {
  for (const element of main.querySelectorAll(SELECTORS.olderMessages)) {
    const match = LABELS.olderMessages.exec((element.getAttribute("aria-label") ?? element.textContent ?? "").trim());
    if (match && !editor.contains(element) && !element.closest(SELECTORS.body) && isRendered(element)) {
      return Number(match[1]);
    }
  }

  return 0;
}

/** Gmail nests an inline reply inside the row of the message it answers, so a row with the draft and no date header is the draft alone. */
function isDraftOnlyRow(row: Element, editor: HTMLElement): boolean {
  return row.contains(editor) && !Array.from(row.querySelectorAll(SELECTORS.date)).some((date) => !editor.contains(date));
}

/** Message rows oldest first, leaving out rows nested in another row and a row that is only the draft. */
function scan(main: Element, editor: HTMLElement): ThreadState {
  const rows = Array.from(main.querySelectorAll(SELECTORS.message)).filter(
    (row) => !row.parentElement?.closest(SELECTORS.message) && isRendered(row) && !isDraftOnlyRow(row, editor),
  );
  return { rows, collapsed: rows.filter((row) => !bodyOf(row, editor)).length, older: olderMessages(main, editor) };
}

const hiddenCount = (state: ThreadState): number => state.collapsed + state.older;
const stateKey = (state: ThreadState): string => `${state.rows.length}/${state.collapsed}/${state.older}`;

/** Hidden messages after Expand all: collapsed rows plus bundle messages still shown or never delivered. */
function hiddenAfter(state: ThreadState, expectedRows: number): number {
  return state.collapsed + Math.max(state.older, expectedRows - state.rows.length);
}

interface DraftPlace {
  top: number;
  focused: boolean;
  ranges: Range[];
}

function draftPlace(editor: HTMLElement): DraftPlace {
  const doc = editor.ownerDocument;
  const selection = doc.getSelection();
  const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i).cloneRange()) : [];
  return { top: editor.getBoundingClientRect().top, focused: doc.activeElement === editor, ranges };
}

function isScrollable(element: Element): boolean {
  const style = element.ownerDocument.defaultView?.getComputedStyle(element);
  return element.scrollHeight > element.clientHeight && /auto|scroll/.test(style?.overflowY ?? "");
}

/** Gmail's Expand all scrolls to the top and focuses the subject, then the opened messages push the draft down; this puts it back. */
function keepDraftInPlace(editor: HTMLElement, place: DraftPlace): void {
  const doc = editor.ownerDocument;
  if (place.focused && doc.activeElement !== editor) {
    // Firefox moves the caret to the start of a refocused editor, so the selection goes back afterwards.
    editor.focus({ preventScroll: true });
    const selection = doc.getSelection();
    if (selection && place.ranges.length > 0) {
      selection.removeAllRanges();
      for (const range of place.ranges) {
        selection.addRange(range.cloneRange());
      }
    }
  }

  let delta = editor.getBoundingClientRect().top - place.top;
  for (let element = editor.parentElement; element && Math.abs(delta) >= 1; element = element.parentElement) {
    if (isScrollable(element)) {
      element.scrollTop += delta;
      delta = editor.getBoundingClientRect().top - place.top;
    }
  }

  if (Math.abs(delta) >= 1) {
    doc.defaultView?.scrollBy(0, delta);
  }
}

/** Whether the user has scrolled, clicked, typed, or focused elsewhere since the call; after that the draft stays where they put it. */
function userInteraction(editor: HTMLElement, signal: AbortSignal): { happened: () => boolean } {
  let happened = false;
  const win = editor.ownerDocument.defaultView;
  const mark = (): void => {
    happened = true;
  };
  for (const type of ["wheel", "touchstart", "pointerdown", "keydown", "input"]) {
    win?.addEventListener(type, mark, { capture: true, passive: true, signal });
  }

  win?.addEventListener("focusin", (event) => event.target !== editor && mark(), { capture: true, signal });
  return { happened: () => happened };
}

/** Clicks Expand all when messages are hidden and waits for them; returns how many are still hidden. */
async function expandAll(main: Element, editor: HTMLElement, timing: GmailTiming): Promise<number> {
  const button = Array.from(main.querySelectorAll<HTMLElement>(SELECTORS.expandAll)).find(isRendered);
  const initial = scan(main, editor);
  if (!button || button.getAttribute("aria-disabled") === "true" || hiddenCount(initial) === 0) {
    return hiddenCount(initial);
  }

  const listeners = new AbortController();
  try {
    const place = draftPlace(editor);
    button.click();
    keepDraftInPlace(editor, place);
    const interaction = userInteraction(editor, listeners.signal);
    return await waitForExpansion(main, editor, timing, initial, () => {
      if (!interaction.happened()) {
        keepDraftInPlace(editor, place);
      }
    });
  } finally {
    listeners.abort();
  }
}

/** Polls until the hidden messages open or the thread settles; `onPoll` runs after each wait. */
async function waitForExpansion(main: Element, editor: HTMLElement, timing: GmailTiming, initial: ThreadState, onPoll: () => void): Promise<number> {
  // The bundle label can vanish before its rows arrive, so the rows it promised must show up before the thread is whole.
  const expectedRows = initial.rows.length + initial.older;
  const initialKey = stateKey(initial);
  let lastKey = initialKey;
  let stablePolls = 0;
  let state = initial;
  // Wall clock, not a sleep count: a throttled tab stretches each sleep well past pollMs.
  const deadline = timing.now() + timing.deadlineMs;
  while (timing.now() < deadline) {
    await timing.sleep(timing.pollMs);
    onPoll();
    state = scan(main, editor);
    if (hiddenAfter(state, expectedRows) === 0) {
      break;
    }

    const key = stateKey(state);
    stablePolls = key === lastKey ? stablePolls + 1 : 0;
    lastKey = key;
    if (key !== initialKey && stablePolls >= STABLE_POLLS && state.rows.length >= expectedRows) {
      break;
    }
  }

  return hiddenAfter(state, expectedRows);
}

function sender(row: Element): string {
  const element = row.querySelector(SELECTORS.sender);
  if (!element) {
    return "";
  }

  const email = element.getAttribute("email")?.trim() ?? "";
  const name = (element.getAttribute("name") ?? element.textContent ?? "").trim();
  return name && email && name !== email ? `${name} <${email}>` : name || email;
}

/** The date element's title ("Wed, Oct 8, 2026, 9:12 AM", local time) as ISO 8601, or undefined. */
function sentAt(row: Element): string | undefined {
  const title = row.querySelector(SELECTORS.date)?.getAttribute("title");
  if (!title) {
    return undefined;
  }

  // Date.parse turns a bare "Oct 7" into a date in 2001, so only a title with a year and a time counts.
  if (!/\d{4}/.test(title) || !/\d{1,2}:\d{2}/.test(title)) {
    return undefined;
  }

  const time = Date.parse(title.replace(/[  ]/g, " ").replace(" at ", " "));
  return Number.isNaN(time) ? undefined : new Date(time).toISOString();
}

export function createGmailAdapter(timing: GmailTiming = DEFAULT_TIMING): SiteAdapter {
  return {
    id: "gmail",
    matches: (location) => location.protocol === "https:" && location.hostname === "mail.google.com",
    findEditor(doc) {
      const selected = editorForSelection(doc);
      if (selected && isGmailEditor(selected)) {
        return selected;
      }

      // Without a selection in a draft, any visible draft lets the capture report no-selection rather than no-editor.
      return Array.from(doc.querySelectorAll<HTMLElement>(SELECTORS.editor)).find((e) => isGmailEditor(e) && isRendered(e)) ?? null;
    },
    async captureThread(doc, editor): Promise<ThreadSnapshot> {
      const main = editor.closest(SELECTORS.main);
      if (!main) {
        return { messages: [], completeness: "draft-only", collapsedCount: 0 };
      }

      const hidden = await expandAll(main, editor, timing);
      const state = scan(main, editor);
      let cut = false;
      const messages: ThreadMessage[] = state.rows.flatMap((row) => {
        const body = bodyOf(row, editor);
        if (!body) {
          return [];
        }

        const text = normalize(readPlainText(body, editor).text);
        cut ||= text.length > PAGE_TEXT_LIMIT;
        return [{ from: sender(row), sent: sentAt(row), body: truncate(text, PAGE_TEXT_LIMIT) }];
      });

      // Rows without even a hidden body element mean the body class moved, not that the messages are collapsed.
      if (messages.length === 0 && !state.rows.some((row) => row.querySelector(SELECTORS.body))) {
        return genericAdapter.captureThread(doc, editor);
      }

      const heading = main.querySelector(SELECTORS.subject);
      const subject = heading ? readPlainText(heading).text.trim() || undefined : undefined;
      const lang = doc.documentElement.lang;
      const unchecked = hidden > 0 || (lang !== "" && !/^en(-|$)/i.test(lang));
      return { subject, messages, completeness: cut ? "truncated" : unchecked ? "loaded-messages-only" : "entire-thread", collapsedCount: hidden };
    },
  };
}

export const gmailAdapter = createGmailAdapter();
