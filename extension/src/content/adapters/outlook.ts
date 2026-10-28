import type { ThreadMessage } from "../../shared/protocol";
import { isRendered, readPlainText } from "../plain-text";
import { editorForSelection, type SiteAdapter, type ThreadSnapshot } from "./adapter";
import { genericAdapter, normalize, PAGE_TEXT_LIMIT, truncate } from "./generic";

const HOSTS = new Set(["outlook.live.com", "outlook.office.com", "outlook.office365.com", "outlook.cloud.microsoft"]);

// Structural hooks only: Outlook localizes every label, and its class names are generated.
const SELECTORS = {
  editor: '[id^="editorParent_"] > [contenteditable="true"][role="textbox"]',
  conversation: '[data-app-section="ConversationContainer"]',
  item: "[data-itempart-id]",
  expansion: ":scope > [aria-expanded]",
  body: '[id^="UniqueMessageBody"]',
  sender: '[id$="_FROM"]',
  recipients: '[id$="_TO"] ._EType_RECIPIENT_ENTITY [aria-label]',
  recipientWell: '[data-testid="RecipientWell"]',
  actions: '[role="toolbar"]',
  date: '[data-testid="SentReceivedSavedTime"]',
  subject: '[aria-labelledby$="_SUBJECT"]',
  // The original message a reply or forward quotes, shown read-only under the compose editor.
  quoteHeader: "#divRplyFwdMsg",
};

function isOutlookEditor(element: Element): boolean {
  return element.matches(SELECTORS.editor);
}

/** Top-level conversation items, leaving out empty placeholders. */
function conversationItems(conversation: Element): Element[] {
  return Array.from(conversation.querySelectorAll(SELECTORS.item)).filter(
    (item) => !item.parentElement?.closest(SELECTORS.item) && item.querySelector(SELECTORS.expansion) !== null && isRendered(item),
  );
}

function bodyOf(item: Element, editor: HTMLElement): HTMLElement | null {
  const body = item.querySelector<HTMLElement>(SELECTORS.body);
  return body && !editor.contains(body) && isRendered(body) ? body : null;
}

/** A saved draft shows expanded but without the reply toolbar and recipient line a sent or received message has. */
function isDraft(item: Element, editor: HTMLElement): boolean {
  const outside = (selector: string): boolean => Array.from(item.querySelectorAll(selector)).some((element) => !editor.contains(element));
  return !outside(SELECTORS.actions) && !outside(SELECTORS.recipientWell);
}

/** The count in the container's label ("4 messages"), which includes messages Outlook has not drawn yet. */
function labelledCount(conversation: Element): number | undefined {
  const match = /\d+/.exec(conversation.getAttribute("aria-label") ?? "");
  return match ? Number(match[0]) : undefined;
}

interface DateParts {
  first: number;
  second: number;
  year: number;
  yearFirst: boolean;
  hour: number;
  minute: number;
}

/** The numbers in a date such as "Wed 10/28/2026 3:03 PM", "28.10.2026 15:03", or "2026-10-28 15:03"; undefined without a year and a time. */
function dateParts(text: string): DateParts | undefined {
  const date = /(\d{1,4})[./-](\d{1,2})[./-](\d{1,4})/.exec(text);
  const time = /(\d{1,2}):(\d{2})(?:\s*([AaPp])\.?\s*[Mm]\b)?/.exec(text.slice(date ? date.index + date[0].length : 0));
  if (!date || !time) {
    return undefined;
  }

  const yearFirst = date[1]!.length === 4;
  const year = Number(yearFirst ? date[1] : date[3]);
  if (year < 1000) {
    return undefined;
  }

  let hour = Number(time[1]);
  const meridiem = time[3]?.toLowerCase();
  if (meridiem) {
    hour = (hour % 12) + (meridiem === "p" ? 12 : 0);
  }

  return {
    first: Number(yearFirst ? date[2] : date[1]),
    second: Number(yearFirst ? date[3] : date[2]),
    year,
    yearFirst,
    hour,
    minute: Number(time[2]),
  };
}

/** Whether the browser's locale writes the month before the day, the order Outlook uses for numeric dates. */
function localeMonthFirst(): boolean {
  const parts = new Intl.DateTimeFormat(undefined, { year: "numeric", month: "numeric", day: "numeric" }).formatToParts(new Date(2026, 10, 25));
  return parts.findIndex((part) => part.type === "month") < parts.findIndex((part) => part.type === "day");
}

/** Month-first unless a date in the thread proves otherwise; with no proof, the browser locale's order. */
function monthFirst(all: readonly DateParts[]): boolean {
  if (all.some((parts) => !parts.yearFirst && parts.first > 12)) {
    return false;
  }

  if (all.some((parts) => !parts.yearFirst && parts.second > 12)) {
    return true;
  }

  return localeMonthFirst();
}

function toIso(parts: DateParts, isMonthFirst: boolean): string | undefined {
  const [month, day] = parts.yearFirst || isMonthFirst ? [parts.first, parts.second] : [parts.second, parts.first];
  if (month < 1 || month > 12 || day < 1 || day > 31 || parts.hour > 23 || parts.minute > 59) {
    return undefined;
  }

  const date = new Date(parts.year, month - 1, day, parts.hour, parts.minute);
  return date.getMonth() === month - 1 ? date.toISOString() : undefined;
}

function recipients(item: Element): string[] {
  return Array.from(item.querySelectorAll(SELECTORS.recipients), (element) => element.getAttribute("aria-label")!.trim()).filter((value) => value.length > 0);
}

function captureConversation(conversation: Element, editor: HTMLElement): ThreadSnapshot | undefined {
  const items = conversationItems(conversation);
  const rows = items.flatMap((item) => {
    const body = bodyOf(item, editor);
    if (body && isDraft(item, editor)) {
      return [];
    }

    return [{ item, body, date: dateParts(item.querySelector(SELECTORS.date)?.textContent ?? "") }];
  });

  const shown = rows.filter((row) => row.body !== null);
  // Bodies gone while items remain means the body hook moved, not that every message is collapsed.
  if (shown.length === 0 && !items.some((item) => item.querySelector(SELECTORS.body))) {
    return undefined;
  }

  const order = monthFirst(rows.flatMap((row) => (row.date ? [row.date] : [])));
  let cut = false;
  const messages: { message: ThreadMessage; time?: number }[] = shown.map(({ item, body, date }) => {
    const text = normalize(readPlainText(body!, editor).text);
    cut ||= text.length > PAGE_TEXT_LIMIT;
    const sent = date ? toIso(date, order) : undefined;
    const to = recipients(item);
    return {
      message: { from: (item.querySelector(SELECTORS.sender)?.textContent ?? "").trim(), ...(to.length > 0 ? { to } : {}), sent, body: truncate(text, PAGE_TEXT_LIMIT) },
      time: sent === undefined ? undefined : Date.parse(sent),
    };
  });

  // Outlook lists the newest message first by default; the reverse order is a setting.
  const times = messages.flatMap((entry) => (entry.time === undefined ? [] : [entry.time]));
  const differing = times.find((time) => time !== times[0]);
  const oldestFirst = differing !== undefined && differing > times[0]!;
  if (!oldestFirst) {
    messages.reverse();
  }

  const collapsed = rows.length - shown.length;
  // Once saved, the reply being written counts in the label without an item of its own.
  const undrawn = Math.max(0, (labelledCount(conversation) ?? items.length) - items.length - 1);
  const hidden = collapsed + undrawn;
  const heading = conversation.querySelector(SELECTORS.subject)?.getAttribute("aria-labelledby");
  const subjectElement = heading ? conversation.ownerDocument.getElementById(heading) : null;
  const subject = subjectElement ? readPlainText(subjectElement).text.trim() || undefined : undefined;
  return {
    subject,
    messages: messages.map((entry) => entry.message),
    completeness: cut ? "truncated" : hidden > 0 ? "loaded-messages-only" : "entire-thread",
    collapsedCount: hidden,
  };
}

/** The field after the label on a header line ("From: Ana" gives "Ana"), whatever the label's language. */
function headerValue(line: string | undefined): string {
  const colon = line?.indexOf(":") ?? -1;
  return colon >= 0 ? line!.slice(colon + 1).trim() : "";
}

function headerDate(line: string | undefined): string | undefined {
  const value = headerValue(line);
  if (!/\d{4}/.test(value) || !/\d{1,2}:\d{2}/.test(value)) {
    return undefined;
  }

  const time = Date.parse(value.replace(/[\u00a0\u202f]/g, " ").replace(/^[^,\d]+,\s*/, "").replace(" at ", " "));
  return Number.isNaN(time) ? undefined : new Date(time).toISOString();
}

/** The compose editor's container holding the quoted original, searched outward but never past a conversation item. */
function quoteRoot(editor: HTMLElement): HTMLElement | null {
  for (let element = editor.parentElement; element; element = element.parentElement) {
    const header = element.querySelector<HTMLElement>(SELECTORS.quoteHeader);
    if (header && !editor.contains(header)) {
      const root = header.parentElement!;
      return root.contains(editor) || !isRendered(root) ? null : root;
    }

    if (element.matches(SELECTORS.item) || element.matches(SELECTORS.conversation)) {
      return null;
    }
  }

  return null;
}

/** The quoted original as one message, its body carrying the older history it quotes. */
function captureQuote(root: HTMLElement, editor: HTMLElement): ThreadSnapshot {
  const header = root.querySelector<HTMLElement>(SELECTORS.quoteHeader)!;
  const headerText = normalize(readPlainText(header).text);
  const lines = headerText.split("\n").filter((line) => line.trim().length > 0);
  const full = normalize(readPlainText(root, editor).text);
  const at = full.indexOf(headerText);
  const text = (at >= 0 ? full.slice(at + headerText.length) : full).trim();
  const subject = lines.length >= 4 ? headerValue(lines[lines.length - 1]) || undefined : undefined;
  return {
    subject,
    messages: text ? [{ from: headerValue(lines[0]), sent: headerDate(lines[1]), body: truncate(text, PAGE_TEXT_LIMIT) }] : [],
    // The quote is whatever the previous sender kept, so it may not reach back to the first message.
    completeness: text.length > PAGE_TEXT_LIMIT ? "truncated" : text ? "loaded-messages-only" : "draft-only",
    collapsedCount: 0,
  };
}

export const outlookAdapter: SiteAdapter = {
  id: "outlook",
  matches: (location) => location.protocol === "https:" && HOSTS.has(location.hostname),
  findEditor(doc) {
    const selected = editorForSelection(doc);
    if (selected && isOutlookEditor(selected)) {
      return selected;
    }

    return Array.from(doc.querySelectorAll<HTMLElement>(SELECTORS.editor)).find(isRendered) ?? null;
  },
  async captureThread(doc, editor): Promise<ThreadSnapshot> {
    const conversation = editor.closest(SELECTORS.conversation);
    if (conversation) {
      return captureConversation(conversation, editor) ?? genericAdapter.captureThread(doc, editor);
    }

    const quote = quoteRoot(editor);
    return quote ? captureQuote(quote, editor) : { messages: [], completeness: "draft-only", collapsedCount: 0 };
  },
  // Outlook's editor records an undo point only on some keys, so after an edit command alone Ctrl+Z also takes the
  // words typed since the last one. A key press over the selection records one; a synthetic press types nothing.
  recordUndoPoint(editor) {
    for (const type of ["keydown", "keyup"]) {
      editor.dispatchEvent(new KeyboardEvent(type, { key: "a", code: "KeyA", bubbles: true, cancelable: true }));
    }
  },
};
