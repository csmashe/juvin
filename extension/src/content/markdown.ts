// The Markdown subset shared with the app (docs/connector-protocol.md): bold, italic, links with an allowed
// scheme, bulleted and numbered list lines nested by indentation, and newlines as line breaks. Anything else is
// literal text.

import { readPlainText, segmentText } from "./plain-text";

const ALLOWED_SCHEMES = new Set(["http:", "https:", "mailto:"]);
const ESCAPABLE = "\\*_[]-.";
const SPACE = "[ \\t\\u00a0]";
const LIST_LINE = new RegExp(`^(${SPACE}*)(?:([-*])|(-?\\d{1,9})\\.)${SPACE}+(.*)$`);

/** Deeper emphasis stays literal and deeper list items join the deepest list, so the trees built stay shallow. */
const MAX_NESTING = 8;

export function isAllowedHref(href: string): boolean {
  try {
    return ALLOWED_SCHEMES.has(new URL(href).protocol);
  } catch {
    return false;
  }
}

// ---- Capture: the selected range of a rich editor as Markdown ----

interface Style {
  bold: boolean;
  italic: boolean;
  href?: string;
}

interface Piece {
  text: string;
  style: Style;
}

const PLAIN: Style = { bold: false, italic: false };

export function styleOf(node: Node, root: Element): Style {
  const parent = node.parentElement;
  let tagBold = false;
  let tagItalic = false;
  let href: string | undefined;
  for (let element = parent; element && element !== root; element = element.parentElement) {
    tagBold ||= element.tagName === "B" || element.tagName === "STRONG";
    tagItalic ||= element.tagName === "I" || element.tagName === "EM";
    const link = element.tagName === "A" ? element.getAttribute("href") : null;
    if (href === undefined && link !== null && isAllowedHref(link)) {
      href = link;
    }
  }

  // Computed style covers CSS bold and italic and a tag styled back to normal; tags cover a runtime without styles.
  const computed = parent ? parent.ownerDocument.defaultView?.getComputedStyle(parent) : undefined;
  const weight = computed?.fontWeight ?? "";
  const fontStyle = computed?.fontStyle ?? "";
  const bold = weight === "" ? tagBold : weight === "bold" || weight === "bolder" || Number(weight) >= 600;
  const italic = fontStyle === "" ? tagItalic : fontStyle.startsWith("italic") || fontStyle.startsWith("oblique");
  return href === undefined ? { bold, italic } : { bold, italic, href };
}

function intersect(a: Style, b: Style): Style {
  const style = { bold: a.bold && b.bold, italic: a.italic && b.italic };
  return a.href !== undefined && a.href === b.href ? { ...style, href: a.href } : style;
}

function escapeText(text: string): string {
  return text.replace(/[*_[\]]|\\(?=[\\*_[\]\-.]|$)/g, "\\$&");
}

function linkTarget(href: string): string {
  return href.replaceAll("(", "%28").replaceAll(")", "%29").replace(/\s/g, (c) => encodeURIComponent(c));
}

const isBlank = (text: string): boolean => /^\s+$/.test(text);

/** Whitespace takes the style its neighbours share, so a marker never opens or closes next to a space. */
function settleWhitespace(tokens: Piece[]): Piece[] {
  const next: (Style | undefined)[] = new Array(tokens.length);
  for (let i = tokens.length - 1, style: Style | undefined; i >= 0; i--) {
    next[i] = style;
    if (!isBlank(tokens[i]!.text)) {
      style = tokens[i]!.style;
    }
  }

  let previous: Style | undefined;
  return tokens.map((token, i) => {
    if (!isBlank(token.text)) {
      previous = token.style;
      return token;
    }

    const following = next[i];
    return { text: token.text, style: previous && following ? intersect(previous, following) : PLAIN };
  });
}

/** Gives every word the style all of its parts share, so markers change only next to whitespace. */
function wordStyles(tokens: Piece[]): Piece[] {
  const result: Piece[] = [];
  for (let i = 0; i < tokens.length; ) {
    let j = i;
    let style = tokens[i]!.style;
    while (j < tokens.length && !isBlank(tokens[j]!.text)) {
      style = intersect(style, tokens[j]!.style);
      j++;
    }

    if (j === i) {
      result.push(tokens[i]!);
      i++;
      continue;
    }

    result.push(...tokens.slice(i, j).map((t) => ({ text: t.text, style })));
    i = j;
  }

  return settleWhitespace(result);
}

function emit(tokens: Piece[]): string {
  let out = "";
  let link: string | undefined;
  const open: ("**" | "*")[] = [];
  const close = (from: number): void => {
    while (open.length > from) {
      out += open.pop();
    }
  };

  for (const { text, style } of tokens) {
    if (style.href !== link) {
      close(0);
      if (link !== undefined) {
        out += `](${linkTarget(link)})`;
      }

      link = style.href;
      if (link !== undefined) {
        out += "[";
      }
    }

    const wanted = (marker: "**" | "*"): boolean => (marker === "**" ? style.bold : style.italic);
    const keep = open.findIndex((marker) => !wanted(marker));
    close(keep === -1 ? open.length : keep);
    // Markers open only before text, never before a space.
    if (!isBlank(text)) {
      for (const marker of ["**", "*"] as const) {
        if (wanted(marker) && !open.includes(marker)) {
          out += marker;
          open.push(marker);
        }
      }
    }

    out += escapeText(text);
  }

  close(0);
  if (link !== undefined) {
    out += `](${linkTarget(link)})`;
  }

  return out;
}

function styledChars(inlines: Inline[], style: Style = PLAIN): { char: string; style: Style }[] {
  return inlines.flatMap((inline) => {
    if (inline.kind === "text") {
      return Array.from(inline.text, (char) => ({ char, style }));
    }

    const inner =
      inline.kind === "link" ? { ...style, href: inline.href } : inline.kind === "strong" ? { ...style, bold: true } : { ...style, italic: true };
    return styledChars(inline.children, inner);
  });
}

/** True when the line parses back to the same text with the same formatting on every non-space character. */
function parsesBack(tokens: Piece[], markdown: string): boolean {
  const expected = tokens.flatMap((t) => Array.from(t.text, (char) => ({ char, style: t.style })));
  const actual = styledChars(parseInline(markdown));
  return (
    actual.length === expected.length &&
    actual.every(({ char, style }, i) => {
      const want = expected[i]!;
      const href = want.style.href === undefined ? undefined : linkTarget(want.style.href);
      return char === want.char && (/\s/.test(char) || (style.bold === want.style.bold && style.italic === want.style.italic && style.href === href));
    })
  );
}

/**
 * Emits one line. Adjacent runs of markers can read back differently than meant, so each form is checked by
 * parsing it back, falling back to formatting whole words and then to plain escaped text.
 */
function lineMarkdown(pieces: Piece[]): string {
  const tokens = settleWhitespace(pieces.flatMap((p) => p.text.split(/(\s+)/).filter((t) => t.length > 0).map((text) => ({ text, style: p.style }))));
  for (const form of [() => tokens, () => wordStyles(tokens)]) {
    const candidate = form();
    const markdown = emit(candidate);
    if (parsesBack(candidate, markdown)) {
      return markdown;
    }
  }

  return emit(tokens.map((t) => ({ text: t.text, style: PLAIN })));
}

/** Item numbers already worked out, per list item. */
export type ItemNumbers = Map<Element, number>;

const parseNumber = (value: string | null): number | undefined => {
  const number = Number.parseInt(value ?? "", 10);
  return Number.isNaN(number) ? undefined : number;
};

/**
 * The number a browser shows for an item of an ordered list, by the HTML rules (`start`, `reversed`,
 * `li[value]`). Each list is walked once, on its first item asked for, so a long list stays linear.
 */
export function itemNumber(li: Element, list: Element, numbers: ItemNumbers): number {
  if (!numbers.has(li)) {
    const items = Array.from(list.children).filter((c) => c.tagName === "LI");
    const reversed = list.hasAttribute("reversed");
    let next = parseNumber(list.getAttribute("start")) ?? (reversed ? items.length : 1);
    for (const item of items) {
      next = parseNumber(item.getAttribute("value")) ?? next;
      numbers.set(item, next);
      next += reversed ? -1 : 1;
    }
  }

  return numbers.get(li) ?? 0;
}

/** The item's marker, indented two spaces for each list it is nested in. */
function listPrefix(li: Element, root: Element, numbers: ItemNumbers): string {
  const list = li.parentElement;
  let depth = 0;
  for (let element = list?.parentElement; element && element !== root; element = element.parentElement) {
    if (element.tagName === "UL" || element.tagName === "OL") {
      depth++;
    }
  }

  return "  ".repeat(Math.min(depth, MAX_NESTING - 1)) + (list?.tagName === "OL" ? `${itemNumber(li, list, numbers)}. ` : "- ");
}

/**
 * The plain-text range `start` to `end` of a rich editor as Markdown. The line structure is the one
 * readPlainText gives, so the Markdown's text lines up with the plain selection.
 */
export function selectionMarkdown(root: Element, start: number, end: number): string {
  const plain = readPlainText(root);
  const lines: { start: number; pieces: Piece[]; node?: Node }[] = [{ start, pieces: [] }];
  for (const segment of plain.segments) {
    if (segment.kind === "break") {
      if (segment.start >= start && segment.start < end) {
        if (segment.char === "\t") {
          lines[lines.length - 1]!.pieces.push({ text: "\t", style: PLAIN });
        } else {
          lines.push({ start: segment.start + 1, pieces: [] });
        }
      }

      continue;
    }

    const from = Math.max(start, segment.start);
    const data = segmentText(segment);
    const to = Math.min(end, segment.start + data.length);
    if (from >= to) {
      continue;
    }

    // A text node can hold newlines of its own, under white-space: pre-wrap for example.
    const style = styleOf(segment.node, root);
    let offset = from;
    data
      .slice(from - segment.start, to - segment.start)
      .split("\n")
      .forEach((part, i) => {
        if (i > 0) {
          lines.push({ start: offset, pieces: [] });
        }

        const current = lines[lines.length - 1]!;
        current.node ??= segment.node;
        if (part.length > 0) {
          current.pieces.push({ text: part, style });
        }

        offset += part.length + 1;
      });
  }

  // Where each list item's text starts, found in one pass so long lists stay linear.
  const itemStarts = new Map<Element, number>();
  const numbers: ItemNumbers = new Map();
  for (const segment of plain.segments) {
    const li = segment.kind === "break" ? null : segment.node.parentElement?.closest("li");
    if (li && !itemStarts.has(li)) {
      itemStarts.set(li, segment.start);
    }
  }

  return lines
    .map((line) => {
      const body = lineMarkdown(line.pieces);
      const li = line.node?.parentElement?.closest("li");
      if (li && root.contains(li) && itemStarts.get(li) === line.start) {
        return listPrefix(li, root, numbers) + body;
      }

      return body
        .replace(new RegExp(`^(${SPACE}*)-(?=${SPACE})`), "$1\\-")
        .replace(new RegExp(`^(${SPACE}*-?\\d{1,9})\\.(?=${SPACE})`), "$1\\.");
    })
    .join("\n");
}

// ---- Apply: Markdown to DOM nodes, rendered plain text, and stripped text ----

type Inline = { kind: "text"; text: string } | { kind: "strong" | "em"; children: Inline[] } | { kind: "link"; href: string; children: Inline[] };

interface Delimiter {
  kind: "delim";
  char: "*" | "_";
  count: number;
  /** The run's length before any of it was matched. */
  length: number;
  canOpen: boolean;
  canClose: boolean;
}

type Token = Inline | Delimiter;

interface ListItem {
  depth: number;
  ordered: boolean;
  number: number;
  inlines: Inline[];
}

/** A list's items in order; an item one level deeper than the one before it starts a list nested in that item. */
type Block = { kind: "line"; inlines: Inline[] } | { kind: "list"; items: ListItem[] };

const isSpace = (c: string | undefined): boolean => c === undefined || /\s/.test(c);
const isWordChar = (c: string | undefined): boolean => c !== undefined && /[\p{L}\p{N}]/u.test(c);

// CommonMark's rule of three: a run that can both open and close does not pair with one whose combined
// length is a multiple of 3, so the `*` of `**US*D*100**` closes the inner italic, not the outer bold.
function canPair(opener: Delimiter, closer: Delimiter): boolean {
  const both = opener.canClose || closer.canOpen;
  return !both || (opener.length + closer.length) % 3 !== 0 || (opener.length % 3 === 0 && closer.length % 3 === 0);
}

/**
 * Matches emphasis delimiters the way CommonMark does, simplified to the hug rule. Matches are recorded rather
 * than spliced in, and a failed search for an opener is never repeated over the same delimiters (CommonMark's
 * openers bottom), so a line full of unmatched markers stays linear.
 */
function resolveEmphasis(tokens: Token[]): Inline[] {
  type Kind = "strong" | "em";
  const opens = new Map<number, Kind[]>();
  const closes = new Map<number, Kind[]>();
  const record = (map: Map<number, Kind[]>, index: number, kind: Kind): void => {
    const kinds = map.get(index);
    if (kinds) {
      kinds.push(kind);
    } else {
      map.set(index, [kind]);
    }
  };

  // Indices of delimiters that may still open, in order.
  const stack: number[] = [];
  const bottoms = new Map<string, number>();
  tokens.forEach((closer, i) => {
    if (closer.kind !== "delim") {
      return;
    }

    const key = `${closer.char}${closer.length % 3}${closer.canOpen}`;
    while (closer.canClose && closer.count > 0) {
      const bottom = bottoms.get(key) ?? 0;
      let p = stack.length - 1;
      while (p >= bottom && !((tokens[stack[p]!] as Delimiter).char === closer.char && canPair(tokens[stack[p]!] as Delimiter, closer))) {
        p--;
      }

      if (p < bottom) {
        bottoms.set(key, stack.length);
        break;
      }

      const opener = tokens[stack[p]!] as Delimiter;
      const kind: Kind = opener.count >= 2 && closer.count >= 2 ? "strong" : "em";
      opener.count -= kind === "strong" ? 2 : 1;
      closer.count -= kind === "strong" ? 2 : 1;
      record(opens, stack[p]!, kind);
      record(closes, i, kind);
      // Delimiters between the pair stay literal.
      stack.length = opener.count > 0 ? p + 1 : p;
      for (const [k, value] of bottoms) {
        bottoms.set(k, Math.min(value, stack.length));
      }
    }

    if (closer.canOpen && closer.count > 0) {
      stack.push(i);
    }
  });

  // Matches nest properly, so one pass builds the tree: a delimiter closes its inner matches first, leaves its
  // unused markers as text, then opens its outer matches first. A match nested deeper than MAX_NESTING keeps its
  // markers as text in its parent's children.
  const root: Inline[] = [];
  const frames: { kind: Kind; children: Inline[]; literal: boolean }[] = [];
  let depth = 0;
  const current = (): Inline[] => frames[frames.length - 1]?.children ?? root;
  const marker = (kind: Kind, char: string): Inline => ({ kind: "text", text: char.repeat(kind === "strong" ? 2 : 1) });
  tokens.forEach((token, i) => {
    if (token.kind !== "delim") {
      current().push(token);
      return;
    }

    for (let n = closes.get(i)?.length ?? 0; n > 0; n--) {
      const frame = frames.pop()!;
      if (frame.literal) {
        current().push(marker(frame.kind, token.char));
      } else {
        depth--;
        current().push({ kind: frame.kind, children: frame.children });
      }
    }

    if (token.count > 0) {
      current().push({ kind: "text", text: token.char.repeat(token.count) });
    }

    for (const kind of [...(opens.get(i) ?? [])].reverse()) {
      if (depth >= MAX_NESTING) {
        current().push(marker(kind, token.char));
        frames.push({ kind, children: current(), literal: true });
      } else {
        depth++;
        frames.push({ kind, children: [], literal: false });
      }
    }
  });

  return root;
}

/** For every `[`, the index of its matching `]`, or none; escapes honoured. One pass, so long lines stay linear. */
function matchBrackets(source: string): Map<number, number> {
  const matches = new Map<number, number>();
  const open: number[] = [];
  for (let i = 0; i < source.length; i++) {
    const c = source[i];
    if (c === "\\") {
      i++;
    } else if (c === "[") {
      open.push(i);
    } else if (c === "]" && open.length > 0) {
      matches.set(open.pop()!, i);
    }
  }

  return matches;
}

const LINK_TARGET = /\(([^\s()]*)\)/y;

/** The `(url)` right after the `]` at `close`, matched in place so long lines are not copied. */
function targetAfter(source: string, close: number | undefined): RegExpExecArray | null {
  if (close === undefined) {
    return null;
  }

  LINK_TARGET.lastIndex = close + 1;
  return LINK_TARGET.exec(source);
}

function parseInline(source: string, inLink = false): Inline[] {
  const tokens: Token[] = [];
  const brackets = matchBrackets(source);
  let text = "";
  const flush = (): void => {
    if (text.length > 0) {
      tokens.push({ kind: "text", text });
      text = "";
    }
  };

  for (let i = 0; i < source.length; ) {
    const c = source[i]!;
    if (c === "\\" && i + 1 < source.length && ESCAPABLE.includes(source[i + 1]!)) {
      text += source[i + 1];
      i += 2;
      continue;
    }

    // Images are not in the subset: the whole construct stays literal instead of becoming "!" and a link.
    if (c === "!" && source[i + 1] === "[") {
      const close = brackets.get(i + 1);
      const target = targetAfter(source, close);
      if (close !== undefined && target) {
        const end = close + 1 + target[0].length;
        text += source.slice(i, end);
        i = end;
        continue;
      }
    }

    if (c === "[" && !inLink) {
      const close = brackets.get(i);
      const target = targetAfter(source, close);
      if (close !== undefined && target && isAllowedHref(target[1]!)) {
        flush();
        tokens.push({ kind: "link", href: target[1]!, children: parseInline(source.slice(i + 1, close), true) });
        i = close + 1 + target[0].length;
        continue;
      }
    }

    if (c === "*" || c === "_") {
      let runEnd = i;
      while (source[runEnd] === c) {
        runEnd++;
      }

      const before = source[i - 1];
      const after = source[runEnd];
      flush();
      tokens.push({
        kind: "delim",
        char: c,
        count: runEnd - i,
        length: runEnd - i,
        canOpen: !isSpace(after) && (c === "*" || !isWordChar(before)),
        canClose: !isSpace(before) && (c === "*" || !isWordChar(after)),
      });
      i = runEnd;
      continue;
    }

    text += c;
    i++;
  }

  flush();
  return resolveEmphasis(tokens);
}

/**
 * A list line indented past the item before it nests one level under that item; one indented less goes back to
 * the level whose indent it reaches. Consecutive top-level lines of one kind form one list.
 */
function parseBlocks(markdown: string): Block[] {
  const blocks: Block[] = [];
  // The indent of each open level of the current list, outermost first.
  let indents: number[] = [];
  for (const line of markdown.replace(/\r\n?/g, "\n").split("\n")) {
    const match = LIST_LINE.exec(line);
    if (!match) {
      blocks.push({ kind: "line", inlines: parseInline(line) });
      continue;
    }

    const indent = match[1]!.replaceAll("\t", "    ").length;
    const ordered = match[3] !== undefined;
    const item = { depth: 0, ordered, number: ordered ? Number(match[3]) : 0, inlines: parseInline(match[4]!) };
    const last = blocks[blocks.length - 1];
    if (last?.kind === "list") {
      while (indents.length > 1 && indent < indents[indents.length - 1]!) {
        indents.pop();
      }

      // Dedented below the list's first item: this indent becomes the top level, so deeper items nest under it.
      indents[0] = Math.min(indents[0]!, indent);

      if (indent > indents[indents.length - 1]! && indents.length < MAX_NESTING) {
        indents.push(indent);
      }

      item.depth = indents.length - 1;
      if (item.depth > 0 || last.items[0]!.ordered === ordered) {
        last.items.push(item);
        continue;
      }
    }

    indents = [indent];
    blocks.push({ kind: "list", items: [item] });
  }

  return blocks;
}

/** Builds the list's items as nested `ul` and `ol` elements under `container`. */
function appendList(doc: Document, container: Node, items: ListItem[]): void {
  // The list open at each depth, with its last item and that item's number.
  const levels: { list: HTMLElement; ordered: boolean; item: HTMLElement; number: number }[] = [];
  for (const item of items) {
    levels.length = Math.min(levels.length, item.depth + 1);
    const li = doc.createElement("li");
    const level = levels[item.depth];
    if (level && level.ordered === item.ordered) {
      // Each item keeps the number it was given, so a captured list comes back with its own numbering.
      if (item.ordered && item.number !== level.number + 1) {
        li.setAttribute("value", String(item.number));
      }

      level.list.appendChild(li);
      level.item = li;
      level.number = item.number;
    } else {
      const list = doc.createElement(item.ordered ? "ol" : "ul");
      if (item.ordered && item.number !== 1) {
        list.setAttribute("start", String(item.number));
      }

      list.appendChild(li);
      (item.depth === 0 ? container : levels[item.depth - 1]!.item).appendChild(list);
      levels[item.depth] = { list, ordered: item.ordered, item: li, number: item.number };
    }

    appendInlines(doc, li, item.inlines);
  }
}

function appendInlines(doc: Document, parent: Node, inlines: Inline[]): void {
  for (const inline of inlines) {
    if (inline.kind === "text") {
      // A run of spaces would collapse in HTML; a no-break space keeps each one visible.
      parent.appendChild(doc.createTextNode(inline.text.replace(/ (?= )/g, " ")));
      continue;
    }

    const element = doc.createElement(inline.kind === "link" ? "a" : inline.kind === "strong" ? "b" : "i");
    if (inline.kind === "link") {
      element.setAttribute("href", inline.href);
    }

    appendInlines(doc, element, inline.children);
    parent.appendChild(element);
  }
}

/** Builds the subset as DOM nodes under a detached container; page text only ever becomes text nodes. */
export function markdownToElement(doc: Document, markdown: string): HTMLElement {
  const container = doc.createElement("div");
  // Rich editors already space a list from the lines around it, so an empty line next to one would double the gap.
  const blocks = parseBlocks(markdown).filter(
    (block, index, all) => block.kind === "list" || block.inlines.length > 0 || (all[index - 1]?.kind !== "list" && all[index + 1]?.kind !== "list"),
  );
  blocks.forEach((block, index) => {
    if (block.kind === "list") {
      appendList(doc, container, block.items);
      return;
    }

    appendInlines(doc, container, block.inlines);
    const next = blocks[index + 1];
    // A list starts its own line, so only an empty line needs a break before one.
    if (next && (next.kind === "line" || block.inlines.length === 0)) {
      container.appendChild(doc.createElement("br"));
    }
  });

  return container;
}

function inlineText(inlines: Inline[]): string {
  return inlines.map((inline) => (inline.kind === "text" ? inline.text : inlineText(inline.children))).join("");
}

/** The Markdown as plain text for an editor without formatting: list markers and nesting kept, other syntax and link targets removed. */
export function stripMarkdown(markdown: string): string {
  return parseBlocks(markdown)
    .flatMap((block) =>
      block.kind === "line"
        ? [inlineText(block.inlines)]
        : block.items.map((item) => "  ".repeat(item.depth) + (item.ordered ? `${item.number}. ` : "- ") + inlineText(item.inlines)),
    )
    .join("\n");
}
