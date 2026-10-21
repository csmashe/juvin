// Plain text of an editor and the mapping between its character offsets and DOM positions.
//
// innerText cannot be used for this: it gives no way back from an offset to a DOM point, and jsdom does not
// implement it. This walker follows the innerText rules that matter for a draft editor: `<br>` is a line break,
// a block element starts and ends on a new line, with no trailing newline at the end of the root, and a table
// cell followed by another cell in its row ends with a tab. An emoji image reads as its emoji, as Gmail draws
// emoji as `<img>`.

const BLOCK_TAGS = new Set([
  "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DD", "DIV", "DL", "DT", "FIELDSET", "FIGCAPTION", "FIGURE",
  "FOOTER", "FORM", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER", "HR", "LI", "MAIN", "NAV", "OL", "P", "PRE",
  "SECTION", "TABLE", "TR", "UL",
]);

const SKIPPED_TAGS = new Set(["SCRIPT", "STYLE", "TEMPLATE", "NOSCRIPT"]);

const CELL_TAGS = new Set(["TD", "TH"]);

const BLOCK_DISPLAYS = new Set(["block", "flex", "grid", "list-item", "table", "table-row", "table-caption", "flow-root"]);
const INLINE_DISPLAYS = new Set(["inline", "inline-block", "inline-flex", "inline-grid", "inline-table"]);

interface Point {
  node: Node;
  offset: number;
}

type Segment =
  | { kind: "text"; start: number; node: Text }
  /** An emoji image, read as `text`, with the DOM points just before and just after it. */
  | { kind: "emoji"; start: number; node: Element; text: string; before: Point; after: Point }
  /** A synthesized newline, or tab between table cells, at `start`, with the DOM points just before and just after it. */
  | { kind: "break"; char: "\n" | "\t"; start: number; before: Point; after: Point };

export interface PlainText {
  text: string;
  segments: Segment[];
}

/** The text of a non-break segment. */
export const segmentText = (segment: Exclude<Segment, { kind: "break" }>): string => (segment.kind === "text" ? segment.node.data : segment.text);

/**
 * The emoji an image stands for, or null when it is not an emoji: Gmail's carry `data-emoji`, and an image whose
 * short `alt` has no letters or digits is taken as one too.
 */
export function emojiText(element: Element): string | null {
  if (element.tagName !== "IMG") {
    return null;
  }

  // Only a real emoji sequence counts, whichever attribute carries it; a decorative image with a
  // whitespace, punctuation, or descriptive alt stays an image even when it claims data-emoji.
  const candidates = [element.getAttribute("alt") ?? "", element.getAttribute("data-emoji") ?? ""];
  return candidates.find((text) => text.length <= 16 && EMOJI_SEQUENCE.test(text)) ?? null;
}

const EMOJI_SEQUENCE = /^(?=.*[\p{Extended_Pictographic}\p{Regional_Indicator}])[\p{Extended_Pictographic}\p{Regional_Indicator}\p{Emoji_Modifier}\u200D\uFE0F]+$/u;

/** A <br> that is the only content of its parent. */
function isFillerBreak(br: Element): boolean {
  const blank = (node: Node | null): boolean => node !== null && node.nodeType === Node.TEXT_NODE && (node as Text).data === "";
  let previous = br.previousSibling;
  while (blank(previous)) {
    previous = previous!.previousSibling;
  }

  let next = br.nextSibling;
  while (blank(next)) {
    next = next!.nextSibling;
  }

  return previous === null && next === null;
}

/** False when the element or an ancestor is display:none, which innerText leaves out. */
export function isRendered(element: Element): boolean {
  for (let current: Element | null = element; current; current = current.parentElement) {
    if (!visibility(current).rendered) {
      return false;
    }
  }

  return true;
}

interface Rendering {
  /** False for display:none subtrees. */
  rendered: boolean;
  /** False while visibility hides the element's own text. */
  visible: boolean;
  /** True when the element starts and ends a line: by computed display, or by tag where styles are unavailable. */
  block: boolean;
  /** True for a table cell, the same way. */
  cell: boolean;
}

function visibility(element: Element): Rendering {
  if (element instanceof HTMLElement && element.hidden) {
    return { rendered: false, visible: false, block: false, cell: false };
  }

  const style = element.ownerDocument.defaultView?.getComputedStyle(element);
  const display = style?.display ?? "";
  const byTag = !BLOCK_DISPLAYS.has(display) && !INLINE_DISPLAYS.has(display);
  const block = BLOCK_DISPLAYS.has(display) || (byTag && BLOCK_TAGS.has(element.tagName));
  const cell = display === "table-cell" || (byTag && CELL_TAGS.has(element.tagName));
  return { rendered: display !== "none", visible: style?.visibility !== "hidden" && style?.visibility !== "collapse", block, cell };
}

/** True when the element starts and ends a line of its own. */
export function isBlock(element: Element): boolean {
  return visibility(element).block;
}

/** True when another rendered table cell follows the cell in its row. */
function hasNextCell(cell: Element): boolean {
  for (let next = cell.nextElementSibling; next; next = next.nextElementSibling) {
    const shown = visibility(next);
    if (shown.rendered && shown.cell) {
      return true;
    }
  }

  return false;
}

/** Reads the plain text under `root`, leaving out the subtree at `exclude`. */
export function readPlainText(root: Node, exclude?: Node): PlainText {
  let text = "";
  const segments: Segment[] = [];
  // The newline a block owes after itself, added only when more text follows, as innerText drops a trailing one.
  // A block whose only content is a <br> is an empty line, so its break is kept even right after another one.
  let pendingBreak: { before: Point; after: Point; emptyLine: boolean } | undefined;

  const addBreak = (before: Point, after: Point, char: "\n" | "\t" = "\n"): void => {
    segments.push({ kind: "break", char, start: text.length, before, after });
    text += char;
  };

  const flushBreak = (): void => {
    if (pendingBreak && (pendingBreak.emptyLine || (text.length > 0 && !text.endsWith("\n")))) {
      addBreak(pendingBreak.before, pendingBreak.after);
    }

    pendingBreak = undefined;
  };

  const walk = (node: Node, visible: boolean): void => {
    for (const [index, child] of Array.from(node.childNodes).entries()) {
      if (child === exclude) {
        continue;
      }

      if (child.nodeType === Node.TEXT_NODE) {
        const data = (child as Text).data;
        if (visible && data.length > 0) {
          flushBreak();
          segments.push({ kind: "text", start: text.length, node: child as Text });
          text += data;
        }

        continue;
      }

      if (child.nodeType !== Node.ELEMENT_NODE) {
        continue;
      }

      const element = child as Element;
      const shown = visibility(element);
      if (SKIPPED_TAGS.has(element.tagName) || !shown.rendered) {
        continue;
      }

      const emoji = emojiText(element);
      if (emoji !== null) {
        if (shown.visible) {
          flushBreak();
          segments.push({ kind: "emoji", start: text.length, node: element, text: emoji, before: { node, offset: index }, after: { node, offset: index + 1 } });
          text += emoji;
        }

        continue;
      }

      if (element.tagName === "BR") {
        flushBreak();
        const points = [{ node, offset: index }, { node, offset: index + 1 }] as const;
        if (isFillerBreak(element)) {
          pendingBreak = { before: points[0], after: points[1], emptyLine: true };
        } else {
          addBreak(points[0], points[1]);
        }

        continue;
      }

      const block = shown.block;
      if (block) {
        flushBreak();
        if (text.length > 0 && !text.endsWith("\n")) {
          addBreak({ node, offset: index }, { node: element, offset: 0 });
        }
      }

      walk(element, shown.visible);
      if (shown.cell && hasNextCell(element)) {
        // A block that ends the cell owes no newline: innerText puts only the tab between cells.
        pendingBreak = undefined;
        addBreak({ node: element, offset: element.childNodes.length }, { node, offset: index + 1 }, "\t");
        continue;
      }

      if (block && !pendingBreak?.emptyLine) {
        pendingBreak = { before: { node: element, offset: element.childNodes.length }, after: { node, offset: index + 1 }, emptyLine: false };
      }
    }
  };

  if (root instanceof Element) {
    const shown = visibility(root);
    if (shown.rendered) {
      walk(root, shown.visible);
    }
  } else {
    walk(root, true);
  }

  return { text, segments };
}

/** The DOM point for a character offset in the plain text. */
export function offsetToPoint(root: Node, plain: PlainText, offset: number): Point {
  for (const segment of plain.segments) {
    if (segment.kind === "text" && offset >= segment.start && offset <= segment.start + segment.node.data.length) {
      return { node: segment.node, offset: offset - segment.start };
    }
  }

  // A synthesized character or an emoji is never split: an offset inside it maps to the point after it.
  const length = (s: Segment): number => (s.kind === "break" ? s.char.length : segmentText(s).length);
  const before = plain.segments.find((s) => s.kind !== "text" && s.start < offset && offset <= s.start + length(s));
  if (before && before.kind !== "text") {
    return before.after;
  }

  const at = plain.segments.find((s) => s.kind !== "text" && s.start === offset);
  if (at && at.kind !== "text") {
    return at.before;
  }

  return offset <= 0 ? { node: root, offset: 0 } : { node: root, offset: root.childNodes.length };
}

/** The character offset in the plain text for a DOM point inside the root. */
export function pointToOffset(plain: PlainText, node: Node, offset: number): number {
  if (node.nodeType === Node.TEXT_NODE) {
    const segment = plain.segments.find((s) => s.kind === "text" && s.node === node);
    if (segment) {
      return segment.start + Math.min(offset, (node as Text).data.length);
    }
  }

  const point = (node.ownerDocument ?? (node as Document)).createRange();
  point.setStart(node, offset);
  for (const segment of plain.segments) {
    const start = segment.kind === "text" ? { node: segment.node, offset: 0 } : segment.before;
    if (point.comparePoint(start.node, start.offset) >= 0) {
      return segment.start;
    }
  }

  return plain.text.length;
}

/** The point where the text or emoji ending at `offset` ends, or null when none ends there. */
function endOfSegmentAt(plain: PlainText, offset: number): Point | null {
  const segment = plain.segments.find((s) => s.kind !== "break" && s.start + segmentText(s).length === offset && s.start < offset);
  if (!segment || segment.kind === "break") {
    return null;
  }

  return segment.kind === "text" ? { node: segment.node, offset: segment.node.data.length } : segment.after;
}

/** The point where the text or emoji starting at `offset` begins, or null when none starts there. */
function startOfSegmentAt(plain: PlainText, offset: number): Point | null {
  const segment = plain.segments.find((s) => s.kind !== "break" && s.start === offset && segmentText(s).length > 0);
  if (!segment || segment.kind === "break") {
    return null;
  }

  return segment.kind === "text" ? { node: segment.node, offset: 0 } : segment.before;
}

/**
 * The range for `start` to `end`. On a boundary between two segments the start takes the later point and the end
 * the earlier one, so an image sitting exactly on the boundary stays outside the range.
 */
export function offsetsToRange(root: Node, plain: PlainText, start: number, end: number): Range {
  const range = (root.ownerDocument ?? (root as Document)).createRange();
  const endPoint = (start < end ? endOfSegmentAt(plain, end) : null) ?? offsetToPoint(root, plain, end);
  const startPoint = (start < end ? startOfSegmentAt(plain, start) : null) ?? offsetToPoint(root, plain, start);
  range.setStart(startPoint.node, startPoint.offset);
  range.setEnd(endPoint.node, endPoint.offset);
  return range;
}
