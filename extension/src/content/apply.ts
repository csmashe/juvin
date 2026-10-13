import type { ApplyErrorCode } from "../shared/protocol";
import type { ApplyResult } from "../shared/runtime-messages";
import { editorText } from "./capture";
import { isEditable, isPlainEditor, isTextArea, type Editor, type EditorRegistry } from "./editor-registry";
import { hashText } from "./hash";
import { expectFormatting, formatRuns, formatSignature, formattingSnapshot, type FormatRun } from "./formatting";
import { markdownToElement, stripMarkdown } from "./markdown";
import { offsetsToRange, readPlainText } from "./plain-text";

/** Runs an editing command; injectable because jsdom has no execCommand. */
export type ExecCommand = (doc: Document, command: "insertText" | "insertHTML" | "delete" | "undo", value?: string) => boolean;

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
}

/** Markdown becomes HTML built from the subset in a rich editor and stripped text in a textarea. */
function insertionFor(doc: Document, editor: Editor, text: string, markdown: boolean): Insertion {
  if (markdown && !isPlainEditor(editor)) {
    const container = markdownToElement(doc, text);
    const rendered = readPlainText(container).text;
    if (rendered.length > 0) {
      return { command: "insertHTML", value: container.innerHTML, rendered, runs: formatRuns(container, 0, rendered.length) };
    }
  }

  const plain = markdown ? stripMarkdown(text) : text;
  return { command: plain.length > 0 ? "insertText" : "delete", value: plain, rendered: plain };
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

  // The hash covers plain text only; this catches formatting changed since the capture.
  const signature = request.format === "markdown" && !isPlainEditor(editor) ? formatSignature(editor, offsets.start, offsets.end) : undefined;
  if (signature !== undefined && (await hashText(signature)) !== request.expectedFormatHash) {
    return failure("draft-changed", "The draft changed since it was captured.");
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

    selection.removeAllRanges();
    selection.addRange(offsetsToRange(editor, readPlainText(editor), offsets.start, offsets.end));
    accepted = exec(doc, insertion.command, insertion.value);
  }

  if (!accepted) {
    return failure("internal", "The editor rejected the edit command.");
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

    return failure("internal", "The editor did not change as expected after the edit command.");
  }

  return { ok: true };
}
