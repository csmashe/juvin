// A lossless record of a rich editor's formatting: unlike the Markdown, which drops what it cannot write
// unambiguously, it tells apart every difference in bold, italic, links, and list structure, and records the
// images, tables, and other content outside the subset. Capture hashes it to detect formatting changed since
// then, and apply compares it to check what an HTML edit produced.

import { itemNumber, styleOf, type ItemNumbers } from "./markdown";
import { emojiText, offsetsToRange, pointToOffset, readPlainText, segmentText } from "./plain-text";

export interface FormatState {
  bold: boolean;
  italic: boolean;
  href: string | null;
  /** Enclosing lists, outermost first: "u" for a bulleted one, "o" and the item's number for a numbered one. */
  lists: string[];
  /** The first visible character of a list item, which tells two items apart from one item with a line break. */
  itemStart: boolean;
}

export interface FormatRun {
  text: string;
  /** Absent for a line break or a tab between table cells. */
  format?: FormatState;
}

const PLAIN: FormatState = { bold: false, italic: false, href: null, lists: [], itemStart: false };

/** The state as a comparable string; without numbers it keeps only the list types. */
function formatKey(format: FormatState | undefined, numbers = true): string {
  if (!format) {
    return "break";
  }

  return JSON.stringify([format.bold, format.italic, format.href, format.itemStart, numbers ? format.lists : format.lists.map((list) => list[0])]);
}

function formatState(node: Node, root: Element, numbers: ItemNumbers): FormatState {
  const { bold, italic } = styleOf(node, root);
  let href: string | null = null;
  const lists: string[] = [];
  let item: Element | undefined;
  for (let element = node.parentElement; element && element !== root; element = element.parentElement) {
    if (href === null && element.tagName === "A" && element.hasAttribute("href")) {
      href = element.getAttribute("href")!;
    }

    if (element.tagName === "LI") {
      item = element;
    } else if (element.tagName === "UL") {
      lists.unshift("u");
    } else if (element.tagName === "OL") {
      lists.unshift(item?.parentElement === element ? `o${itemNumber(item, element, numbers)}` : "o");
    }
  }

  return { bold, italic, href, lists, itemStart: false };
}

function push(runs: FormatRun[], text: string, format?: FormatState): void {
  if (text.length === 0) {
    return;
  }

  const last = runs[runs.length - 1];
  if (last && formatKey(last.format) === formatKey(format)) {
    last.text += text;
  } else {
    runs.push(format ? { text, format } : { text });
  }
}

/** The plain-text range `start` to `end` as runs of characters sharing one formatting state. */
export function formatRuns(root: Element, start: number, end: number): FormatRun[] {
  const runs: FormatRun[] = [];
  const numbers: ItemNumbers = new Map();
  const started = new Set<Element>();
  for (const segment of readPlainText(root).segments) {
    if (segment.kind === "break") {
      if (segment.start >= start && segment.start < end) {
        push(runs, segment.char);
      }

      continue;
    }

    // An item starts at its first visible character, wherever the range begins.
    const data = segmentText(segment);
    const li = segment.node.parentElement?.closest("li");
    let itemStart = -1;
    if (li && root.contains(li) && !started.has(li)) {
      const index = data.search(/\S/);
      if (index >= 0) {
        started.add(li);
        itemStart = segment.start + index;
      }
    }

    const from = Math.max(start, segment.start);
    const to = Math.min(end, segment.start + data.length);
    if (from >= to) {
      continue;
    }

    const format = formatState(segment.node, root, numbers);
    const text = (a: number, b: number): string => data.slice(a - segment.start, b - segment.start);
    if (itemStart >= from && itemStart < to) {
      push(runs, text(from, itemStart), format);
      push(runs, text(itemStart, itemStart + 1), { ...format, itemStart: true });
      push(runs, text(itemStart + 1, to), format);
    } else {
      push(runs, text(from, to), format);
    }
  }

  return runs;
}

/** Content the Markdown subset cannot hold, which replacing a range around it would delete. */
const EMBEDDED = "img, picture, svg, video, audio, canvas, iframe, object, embed, table, hr";

/** Each image, table, rule, or embedded object the range touches, as its tag, source, and offset; emoji images are text. */
export function embeddedContent(root: Element, start: number, end: number): string[] {
  const plain = readPlainText(root);
  const range = offsetsToRange(root, plain, start, end);
  return Array.from(root.querySelectorAll(EMBEDDED))
    .filter((element) => emojiText(element) === null && range.intersectsNode(element))
    .map((element) => {
      const parent = element.parentNode!;
      const offset = pointToOffset(plain, parent, Array.prototype.indexOf.call(parent.childNodes, element));
      return `${element.localName}:${element.getAttribute("src") ?? element.getAttribute("data") ?? ""}@${offset}`;
    });
}

/** Run lengths and states of the range, and its embedded content; capture hashes it to detect formatting changed since then. */
export function formatSignature(root: Element, start: number, end: number): string {
  const runs = formatRuns(root, start, end).map((run) => [run.text.length, formatKey(run.format)]);
  return JSON.stringify([runs, embeddedContent(root, start, end)]);
}

/** The runs without whitespace, which an editor may restructure. */
function visibleRuns(runs: FormatRun[]): FormatRun[] {
  const visible: FormatRun[] = [];
  for (const run of runs) {
    const text = run.text.replace(/\s+/g, "");
    if (text.length > 0) {
      push(visible, text, run.format);
    }
  }

  return visible;
}

const visibleLength = (runs: FormatRun[]): number => runs.reduce((sum, run) => sum + run.text.length, 0);

/** The visible characters `from` to `to` with their states, as a comparable string. */
function describe(runs: FormatRun[], from: number, to: number, numbers: boolean): string {
  const parts: [string, string][] = [];
  let position = 0;
  for (const run of runs) {
    const end = position + run.text.length;
    const text = run.text.slice(Math.max(0, from - position), Math.max(0, to - position));
    position = end;
    if (text.length === 0) {
      continue;
    }

    const key = formatKey(run.format, numbers);
    const last = parts[parts.length - 1];
    if (last?.[1] === key) {
      last[0] += text;
    } else {
      parts.push([text, key]);
    }
  }

  return JSON.stringify(parts);
}

/** Inserted text keeps its own formatting inside whatever encloses the insertion point. */
function withContext(format: FormatState, context: FormatState): FormatState {
  return {
    bold: format.bold || context.bold,
    italic: format.italic || context.italic,
    href: format.href ?? context.href,
    lists: [...context.lists, ...format.lists],
    itemStart: format.itemStart,
  };
}

/** The enclosing formatting at `offset`: of the character there, or the one just before it at the end of a text node. */
function contextAt(root: Element, offset: number): FormatState {
  const texts = readPlainText(root).segments.filter((s) => s.kind !== "break");
  const segment =
    texts.find((s) => offset >= s.start && offset < s.start + segmentText(s).length) ??
    texts.find((s) => offset >= s.start && offset <= s.start + segmentText(s).length);
  return segment ? formatState(segment.node, root, new Map()) : PLAIN;
}

/** A comparable snapshot of the whole editor, to tell whether a failed edit changed anything. */
export function formattingSnapshot(root: Element): string {
  const runs = visibleRuns(formatRuns(root, 0, readPlainText(root).text.length));
  return describe(runs, 0, visibleLength(runs), true);
}

/**
 * Prepares the check of an edit that replaces `start` to `end` of `root` with `fragment`, judged in the edited
 * editor's own terms. Text before the range must keep its formatting exactly. The inserted text must carry the
 * fragment's formatting, either as built or inside what encloses the insertion point (a list item, a bold run,
 * a link), since the editor may carry that over. Text after the range must keep its formatting and list
 * structure; its item numbers may shift when the edit added or removed items, so they are not compared.
 */
export function expectFormatting(root: Element, start: number, end: number, length: number, fragment: FormatRun[]): (edited: Element) => boolean {
  const prefix = visibleRuns(formatRuns(root, 0, start));
  const suffix = visibleRuns(formatRuns(root, end, length));
  const inserted = visibleRuns(fragment);
  const context = contextAt(root, start);
  const startsItem = visibleRuns(formatRuns(root, start, end))[0]?.format?.itemStart ?? false;
  const inContext = visibleRuns(
    inserted.flatMap((run, i) => {
      const format = run.format ? withContext(run.format, context) : undefined;
      if (i > 0 || !format || !startsItem) {
        return [format ? { text: run.text, format } : { text: run.text }];
      }

      return [
        { text: run.text.slice(0, 1), format: { ...format, itemStart: true } },
        { text: run.text.slice(1), format },
      ];
    }),
  );

  const prefixLength = visibleLength(prefix);
  const insertedLength = visibleLength(inserted);
  const suffixLength = visibleLength(suffix);
  const expectedPrefix = describe(prefix, 0, prefixLength, true);
  const expectedInserted = [inserted, inContext].map((runs) => describe(runs, 0, insertedLength, true));
  const expectedSuffix = describe(suffix, 0, suffixLength, false);
  return (edited) => {
    const runs = visibleRuns(formatRuns(edited, 0, readPlainText(edited).text.length));
    const middle = prefixLength + insertedLength;
    return (
      visibleLength(runs) === middle + suffixLength &&
      describe(runs, 0, prefixLength, true) === expectedPrefix &&
      expectedInserted.includes(describe(runs, prefixLength, middle, true)) &&
      describe(runs, middle, middle + suffixLength, false) === expectedSuffix
    );
  };
}
