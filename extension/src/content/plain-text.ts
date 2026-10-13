// Plain text of an editor and the mapping between its character offsets and DOM positions.
//
// innerText cannot be used for this: it gives no way back from an offset to a DOM point, and jsdom does not
// implement it. This walker follows the innerText rules that matter for a draft editor: `<br>` is a line break,
// and a block element starts and ends on a new line, with no trailing newline at the end of the root.

const BLOCK_TAGS = new Set([
  "ADDRESS", "ARTICLE", "ASIDE", "BLOCKQUOTE", "DD", "DIV", "DL", "DT", "FIELDSET", "FIGCAPTION", "FIGURE",
  "FOOTER", "FORM", "H1", "H2", "H3", "H4", "H5", "H6", "HEADER", "HR", "LI", "MAIN", "NAV", "OL", "P", "PRE",
  "SECTION", "TABLE", "TR", "UL",
]);

const SKIPPED_TAGS = new Set(["SCRIPT", "STYLE", "TEMPLATE", "NOSCRIPT"]);

const BLOCK_DISPLAYS = new Set(["block", "flex", "grid", "list-item", "table", "table-row", "table-caption", "flow-root"]);
const INLINE_DISPLAYS = new Set(["inline", "inline-block", "inline-flex", "inline-grid", "inline-table"]);

interface Point {
  node: Node;
  offset: number;
}

type Segment =
  | { kind: "text"; start: number; node: Text }
  /** A synthesized newline at `start`, with the DOM points just before and just after it. */
  | { kind: "break"; start: number; before: Point; after: Point };

export interface PlainText {
  text: string;
  segments: Segment[];
}

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
}

function visibility(element: Element): Rendering {
  if (element instanceof HTMLElement && element.hidden) {
    return { rendered: false, visible: false, block: false };
  }

  const style = element.ownerDocument.defaultView?.getComputedStyle(element);
  const display = style?.display ?? "";
  const block = BLOCK_DISPLAYS.has(display) || (!INLINE_DISPLAYS.has(display) && BLOCK_TAGS.has(element.tagName));
  return { rendered: display !== "none", visible: style?.visibility !== "hidden" && style?.visibility !== "collapse", block };
}

/** Reads the plain text under `root`, leaving out the subtree at `exclude`. */
export function readPlainText(root: Node, exclude?: Node): PlainText {
  let text = "";
  const segments: Segment[] = [];
  // The newline a block owes after itself, added only when more text follows, as innerText drops a trailing one.
  // A block whose only content is a <br> is an empty line, so its break is kept even right after another one.
  let pendingBreak: { before: Point; after: Point; emptyLine: boolean } | undefined;

  const addBreak = (before: Point, after: Point): void => {
    segments.push({ kind: "break", start: text.length, before, after });
    text += "\n";
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

  const breakBefore = plain.segments.find((s) => s.kind === "break" && s.start === offset - 1);
  if (breakBefore?.kind === "break") {
    return breakBefore.after;
  }

  const breakAt = plain.segments.find((s) => s.kind === "break" && s.start === offset);
  if (breakAt?.kind === "break") {
    return breakAt.before;
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

export function offsetsToRange(root: Node, plain: PlainText, start: number, end: number): Range {
  const range = (root.ownerDocument ?? (root as Document)).createRange();
  const startPoint = offsetToPoint(root, plain, start);
  const endPoint = offsetToPoint(root, plain, end);
  range.setStart(startPoint.node, startPoint.offset);
  range.setEnd(endPoint.node, endPoint.offset);
  return range;
}
