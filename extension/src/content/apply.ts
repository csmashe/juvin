import type { ApplyErrorCode } from "../shared/protocol";
import type { ApplyResult } from "../shared/runtime-messages";
import { editorText } from "./capture";
import { isEditable, isTextArea, type Editor, type EditorRegistry } from "./editor-registry";
import { hashText } from "./hash";
import { offsetsToRange, readPlainText } from "./plain-text";

/** Runs an editing command; injectable because jsdom has no execCommand. */
export type ExecCommand = (doc: Document, command: "insertText" | "delete" | "undo", value?: string) => boolean;

export const browserExecCommand: ExecCommand = (doc, command, value) =>
  typeof doc.execCommand === "function" && doc.execCommand(command, false, value);

function failure(code: ApplyErrorCode, message: string): ApplyResult {
  return { ok: false, error: { code, message } };
}

// A rich editor keeps a run of inserted spaces visible by storing some of them as no-break spaces;
// a textarea keeps exactly what was inserted. An editor left untouched never counts as a requested change.
function editedAsExpected(editor: Editor, before: string, after: string, expected: string): boolean {
  if (isTextArea(editor) || (after === before && expected !== before)) {
    return after === expected;
  }

  return after.replaceAll("\u00a0", " ") === expected.replaceAll("\u00a0", " ");
}

/**
 * Replaces the captured selection with `text`, only when the editor still holds the draft that was captured.
 * Goes through the editor's own editing command so Undo keeps working; never writes innerHTML or textContent.
 */
export async function applyReplacement(
  doc: Document,
  registry: EditorRegistry,
  request: { editorId: string; expectedHash: string; start: number; end: number; text: string },
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

  if (!isEditable(editor)) {
    return failure("editor-gone", "The editor is no longer editable.");
  }

  const command = text.length > 0 ? "insertText" : "delete";
  let accepted: boolean;
  let usedFallback = false;
  editor.focus();
  if (doc.activeElement !== editor && !editor.contains(doc.activeElement)) {
    return failure("editor-gone", "The editor cannot take focus.");
  }

  if (editorText(editor) !== before) {
    return failure("draft-changed", "The draft changed since it was captured.");
  }

  if (isTextArea(editor)) {
    editor.setSelectionRange(offsets.start, offsets.end);
    accepted = exec(doc, command, text);
    if (!accepted && typeof doc.execCommand !== "function") {
      // Browsers without execCommand; Undo may not cover this edit.
      editor.setRangeText(text, offsets.start, offsets.end, "end");
      editor.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertReplacementText", data: text }));
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
    accepted = exec(doc, command, text);
  }

  if (!accepted) {
    return failure("internal", "The editor rejected the edit command.");
  }

  const expected = before.slice(0, offsets.start) + text + before.slice(offsets.end);
  const after = editorText(editor);
  if (!editedAsExpected(editor, before, after, expected)) {
    if (after !== before) {
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
