import { inGecko } from "../shared/engine";
import type { ApplyErrorCode } from "../shared/protocol";
import type { ApplyResult } from "../shared/runtime-messages";
import { editorText } from "./capture";
import { isEditable, isEditableHost, isPlainEditor, isTextArea, type Editor, type EditorRegistry, type Offsets } from "./editor-registry";
import { hashText } from "./hash";
import { embeddedContent, expectFormatting, formatRuns, formatSignature, formattingSnapshot, type FormatRun } from "./formatting";
import { markdownToElement, stripMarkdown } from "./markdown";
import { isBlock, offsetsToRange, readPlainText, type PlainText } from "./plain-text";

/** Runs an editing command; injectable because jsdom has no execCommand. */
export type ExecCommand = (doc: Document, command: "insertText" | "insertHTML" | "delete" | "undo" | "redo", value?: string) => boolean;

export const browserExecCommand: ExecCommand = (doc, command, value) =>
  typeof doc.execCommand === "function" && doc.execCommand(command, false, value);

function failure(code: ApplyErrorCode, message: string): ApplyResult {
  return { ok: false, error: { code, message } };
}

const collapseWhitespace = (text: string): string => text.replace(/\s+/g, " ").trim();

// A rich editor keeps a run of inserted spaces visible by storing some of them as no-break spaces, and may
// restructure the blocks of inserted HTML; a textarea keeps exactly what was inserted. An editor left
// untouched never counts as a requested change.
function editedAsExpected(editor: Editor, before: string, after: string, expected: string, html: boolean): boolean {
  if (isTextArea(editor) || (after === before && expected !== before)) {
    return after === expected;
  }

  if (html) {
    return collapseWhitespace(after) === collapseWhitespace(expected);
  }

  return after.replaceAll("\u00a0", " ") === expected.replaceAll("\u00a0", " ");
}

interface Insertion {
  command: "insertText" | "insertHTML" | "delete";
  value: string;
  /** The plain text the editor should show in place of the selection. */
  rendered: string;
  /** For HTML, the formatting the editor should show in place of the selection. */
  runs?: FormatRun[];
  /** For a rich editor, the same content as HTML wrapped in a block of its own. */
  block?: string;
  /** HTML with no list, which can take the spaces around it. */
  inline?: boolean;
}

/** Markdown becomes HTML built from the subset in a rich editor and stripped text in a textarea. */
function insertionFor(doc: Document, editor: Editor, text: string, markdown: boolean): Insertion {
  if (markdown && !isPlainEditor(editor)) {
    const container = markdownToElement(doc, text);
    const rendered = readPlainText(container).text;
    if (rendered.length > 0) {
      return {
        command: "insertHTML",
        value: container.innerHTML,
        rendered,
        runs: formatRuns(container, 0, rendered.length),
        block: container.outerHTML,
        inline: container.querySelector("ul, ol") === null,
      };
    }
  }

  const plain = markdown ? stripMarkdown(text) : text;
  const block = doc.createElement("div");
  // As HTML, a run of spaces would collapse to one, so each space that would be lost becomes a no-break space, as insertText does.
  plain.split("\n").forEach((line, index) => block.append(...(index > 0 ? [doc.createElement("br")] : []), line.replace(/^ | (?= )| $/g, "\u00a0")));
  return { command: plain.length > 0 ? "insertText" : "delete", value: plain, rendered: plain, block: isPlainEditor(editor) ? undefined : block.outerHTML };
}

function textNodeAt(plain: PlainText, offset: number): Text | undefined {
  for (const segment of plain.segments) {
    if (segment.kind === "text" && segment.start <= offset && offset < segment.start + segment.node.data.length) {
      return segment.node;
    }
  }

  return undefined;
}

/**
 * True when the character at `space` is editable text in the same inline formatting as the one at `edge`: the same
 * parent, or a sibling element just like it, as when an editor gives each run of a line a span of its own.
 */
function sameFormatting(plain: PlainText, space: number, edge: number): boolean {
  const parent = textNodeAt(plain, space)?.parentElement;
  const other = textNodeAt(plain, edge)?.parentElement;
  const host = parent?.closest("[contenteditable]");
  if (!parent || !other || !host || !isEditableHost(host)) {
    return false;
  }

  return parent === other || (parent.parentNode === other.parentNode && parent.cloneNode(false).isEqualNode(other.cloneNode(false)));
}

const isVisible = (char: string | undefined): boolean => char !== undefined && /[^\s\u200b]/.test(char);

/**
 * The range to select and the value to insert. Gecko turns a space next to where insertHTML splits a text node
 * into a no-break space, so there inline HTML also replaces a single space beside the range and inserts it again.
 * Chrome would drop such a space at the edge of a line, so it gets the range as captured.
 */
function editTarget(editor: Editor, before: string, offsets: Offsets, insertion: Insertion, gecko: boolean): Offsets & { value: string } {
  if (!gecko || !insertion.inline) {
    return { ...offsets, value: insertion.value };
  }

  const { start, end } = offsets;
  const plain = readPlainText(editor);
  const lead =
    before[start - 1] === " " && isVisible(before[start - 2]) && !/^\s/.test(insertion.rendered) &&
    sameFormatting(plain, start - 1, start) && embeddedContent(editor, start - 1, end).length === 0;
  const trail =
    before[end] === " " && isVisible(before[end + 1]) && !/\s$/.test(insertion.rendered) &&
    sameFormatting(plain, end, end - 1) && embeddedContent(editor, start, end + 1).length === 0;
  return { start: lead ? start - 1 : start, end: trail ? end + 1 : end, value: (lead ? " " : "") + insertion.value + (trail ? " " : "") };
}

/**
 * Chrome keeps a line it empties open with a `<br>`, which stays after the inserted text when a block follows the line.
 * It draws nothing there, but reads as an extra empty line, as in the mail's plain-text part.
 */
function hasNewBreakBeforeBlock(editor: Editor, known: ReadonlySet<Element>): boolean {
  return Array.from(editor.querySelectorAll("br")).some((br) => {
    if (known.has(br)) {
      return false;
    }

    let next = br.nextSibling;
    while (next?.nodeType === Node.TEXT_NODE && (next as Text).data === "") {
      next = next.nextSibling;
    }

    return next instanceof Element && isBlock(next);
  });
}

/** Returns a function that puts the selection back where it is now. */
function holdSelection(doc: Document, editor: Editor): () => void {
  if (isTextArea(editor)) {
    const { selectionStart, selectionEnd, selectionDirection } = editor;
    return () => editor.setSelectionRange(selectionStart, selectionEnd, selectionDirection ?? undefined);
  }

  const selection = doc.getSelection();
  const range = selection && selection.rangeCount > 0 ? selection.getRangeAt(0).cloneRange() : null;
  return () => {
    selection?.removeAllRanges();
    if (range) {
      selection?.addRange(range);
    }
  };
}

/**
 * Puts back the text the editor changed since `before`, through the editor's own command over the part that differs,
 * then selects the captured range again; when the editor still differs, the selection goes back where it was.
 * The range comes back as plain text by design: losing a link beats losing the sentence, so do not swap this for an Undo.
 */
function restoreText(doc: Document, editor: Editor, before: string, offsets: { start: number; end: number }, exec: ExecCommand): void {
  const select = (start: number, end: number): void => {
    if (isTextArea(editor)) {
      editor.setSelectionRange(start, end);
    } else {
      const selection = doc.getSelection();
      selection?.removeAllRanges();
      selection?.addRange(offsetsToRange(editor, readPlainText(editor), start, end));
    }
  };

  const now = editorText(editor);
  if (now !== before) {
    let prefix = 0;
    while (prefix < now.length && prefix < before.length && now[prefix] === before[prefix]) {
      prefix++;
    }

    let suffix = 0;
    while (suffix < now.length - prefix && suffix < before.length - prefix && now[now.length - 1 - suffix] === before[before.length - 1 - suffix]) {
      suffix++;
    }

    const restoreSelection = holdSelection(doc, editor);
    const missing = before.slice(prefix, before.length - suffix);
    // Trailing line breaks go in after the words, so the editor shapes them like the line they end, as Firefox's
    // padding <br> does; the block the editor left there may already draw them.
    const words = missing.replace(/\n+$/, "");
    let replaced = true;
    if (words.length > 0 || prefix < now.length - suffix) {
      select(prefix, now.length - suffix);
      replaced = exec(doc, words.length > 0 ? "insertText" : "delete", words);
    }

    if (replaced && words !== missing && editorText(editor) !== before) {
      select(prefix + words.length, prefix + words.length);
      exec(doc, "insertText", missing.slice(words.length));
    }

    if (editorText(editor) !== before) {
      restoreSelection();
      return;
    }
  }

  select(offsets.start, offsets.end);
}

/**
 * Replaces the captured selection with `text`, only when the editor still holds the draft that was captured.
 * Goes through the editor's own editing command so Undo keeps working; never writes innerHTML or textContent.
 */
export async function applyReplacement(
  doc: Document,
  registry: EditorRegistry,
  request: { editorId: string; expectedHash: string; start: number; end: number; text: string; format?: "markdown"; expectedFormatHash?: string },
  exec: ExecCommand = browserExecCommand,
  recordUndoPoint?: (editor: HTMLElement) => void,
  gecko = inGecko,
): Promise<ApplyResult> {
  const editor = registry.find(request.editorId);
  if (!editor) {
    return failure("editor-gone", "The editor is no longer on the page.");
  }

  const offsets = { start: request.start, end: request.end };
  const text = request.text.replace(/\r\n?/g, "\n");
  const before = editorText(editor);
  if (offsets.start < 0 || offsets.start > offsets.end || offsets.end > before.length || (await hashText(before)) !== request.expectedHash) {
    return failure("draft-changed", "The draft changed since it was captured.");
  }

  // The hash covers plain text only; this catches formatting or embedded content changed since the capture.
  const signature = request.format === "markdown" && !isPlainEditor(editor) ? formatSignature(editor, offsets.start, offsets.end) : undefined;
  if (signature !== undefined && (await hashText(signature)) !== request.expectedFormatHash) {
    return failure("draft-changed", "The draft changed since it was captured.");
  }

  // The edit would delete an image or table in the range along with the text.
  if (!isTextArea(editor) && embeddedContent(editor, offsets.start, offsets.end).length > 0) {
    return failure("unsupported-content", "The selection holds content other than text.");
  }

  if (!isEditable(editor)) {
    return failure("editor-gone", "The editor is no longer editable.");
  }

  const insertion = insertionFor(doc, editor, text, request.format === "markdown");
  let accepted: boolean;
  let usedFallback = false;
  editor.focus();
  if (doc.activeElement !== editor && !editor.contains(doc.activeElement)) {
    return failure("editor-gone", "The editor cannot take focus.");
  }

  if (editorText(editor) !== before || (signature !== undefined && formatSignature(editor, offsets.start, offsets.end) !== signature)) {
    return failure("draft-changed", "The draft changed since it was captured.");
  }

  // From here on Apply may have changed the editor, so every refusal first puts the draft back as it was.
  const refuse = (message: string): ApplyResult => {
    restoreText(doc, editor, before, offsets, exec);
    return failure("internal", message);
  };

  // Text alone cannot show whether an HTML edit kept its formatting, as in turning "hello" into "**hello**".
  const html = insertion.command === "insertHTML";
  const formattingBefore = html ? formattingSnapshot(editor) : "";
  const formattedAsExpected = html ? expectFormatting(editor, offsets.start, offsets.end, before.length, insertion.runs ?? []) : () => true;

  if (isTextArea(editor)) {
    editor.setSelectionRange(offsets.start, offsets.end);
    accepted = exec(doc, insertion.command, insertion.value);
    if (!accepted && typeof doc.execCommand !== "function") {
      // Browsers without execCommand; Undo may not cover this edit.
      editor.setRangeText(insertion.value, offsets.start, offsets.end, "end");
      editor.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertReplacementText", data: insertion.value }));
      accepted = true;
      usedFallback = true;
    }
  } else {
    const selection = doc.getSelection();
    if (!selection) {
      return failure("internal", "The page has no selection object.");
    }

    const target = editTarget(editor, before, offsets, insertion, gecko);
    const select = (): void => {
      selection.removeAllRanges();
      selection.addRange(offsetsToRange(editor, readPlainText(editor), target.start, target.end));
    };
    const breaks = new Set(editor.querySelectorAll("br"));
    select();
    if (recordUndoPoint) {
      recordUndoPoint(editor);
      const now = editorText(editor);
      const placeholder = before.slice(0, target.start) + "\u200b" + before.slice(target.end);
      if (now === before) {
        select();
      } else if (selection.toString() !== "\u200b" || now !== placeholder) {
        return refuse("The editor did not change as expected before the edit command.");
      }
    }

    accepted = exec(doc, insertion.command, target.value);

    // A whole line inserted as its own block needs no <br>; the retry is one edit, so one Undo still reverts it.
    const wholeLine =
      (offsets.start === 0 || before[offsets.start - 1] === "\n") &&
      (offsets.end === before.length || before[offsets.end] === "\n") &&
      !before.slice(offsets.start, offsets.end).includes("\n") &&
      !insertion.rendered.endsWith("\n");
    if (accepted && wholeLine && insertion.block !== undefined && insertion.command !== "delete" && hasNewBreakBeforeBlock(editor, breaks)) {
      const edited = editorText(editor);
      exec(doc, "undo");
      if (editorText(editor) === before && (!html || formattingSnapshot(editor) === formattingBefore)) {
        select();
        accepted = exec(doc, "insertHTML", insertion.block);
      } else if (editorText(editor) !== edited) {
        // The Undo did not exactly reverse the edit; put the edit back rather than leave the draft in between.
        exec(doc, "redo");
        if (editorText(editor) !== edited) {
          return refuse("The editor did not change as expected after the edit command.");
        }
      }
    }
  }

  if (!accepted) {
    return refuse("The editor rejected the edit command.");
  }

  const expected = before.slice(0, offsets.start) + insertion.rendered + before.slice(offsets.end);
  const after = editorText(editor);
  if (!editedAsExpected(editor, before, after, expected, html) || !formattedAsExpected(editor)) {
    if (after !== before || (html && formattingSnapshot(editor) !== formattingBefore)) {
      // An unconditional Undo could take the user's own last edit.
      if (usedFallback && isTextArea(editor)) {
        editor.value = before;
      } else {
        exec(doc, "undo");
      }
    }

    return refuse("The editor did not change as expected after the edit command.");
  }

  return { ok: true };
}
