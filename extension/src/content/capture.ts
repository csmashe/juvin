import { warn } from "../shared/log";
import type { ContextErrorCode, ErrorPayload } from "../shared/protocol";
import type { CaptureResult } from "../shared/runtime-messages";
import { editorForSelection, type PageLocation, type SiteAdapter, type ThreadSnapshot } from "./adapters/adapter";
import { genericAdapter } from "./adapters/generic";
import { selectAdapter } from "./adapters/registry";
import { isPlainEditor, isTextArea, type Editor, type EditorRegistry, type Offsets } from "./editor-registry";
import { hashText } from "./hash";
import { embeddedContent, formatSignature } from "./formatting";
import { selectionMarkdown } from "./markdown";
import { pointToOffset, readPlainText } from "./plain-text";

/** The editor's full plain text: the value of a textarea, the walked text of a contenteditable. */
export function editorText(editor: Editor): string {
  return isTextArea(editor) ? editor.value : readPlainText(editor).text;
}

/** Selection offsets within the editor's plain text, or null when the selection is outside it. */
export function selectionOffsets(doc: Document, editor: Editor): Offsets | null {
  if (isTextArea(editor)) {
    return doc.activeElement === editor ? { start: editor.selectionStart, end: editor.selectionEnd } : null;
  }

  const selection = doc.getSelection();
  if (!selection || selection.rangeCount === 0) {
    return null;
  }

  const range = selection.getRangeAt(0);
  if (!editor.contains(range.startContainer) || !editor.contains(range.endContainer)) {
    return null;
  }

  const plain = readPlainText(editor);
  return {
    start: pointToOffset(plain, range.startContainer, range.startOffset),
    end: pointToOffset(plain, range.endContainer, range.endOffset),
  };
}

function failure(code: ContextErrorCode, message: string): CaptureResult {
  const error: ErrorPayload<ContextErrorCode> = { code, message };
  return { ok: false, error };
}

/** A site adapter that throws on a changed page still yields the page's visible text. */
async function readThread(adapter: SiteAdapter, doc: Document, editor: Editor): Promise<ThreadSnapshot> {
  try {
    return await adapter.captureThread(doc, editor);
  } catch (e) {
    if (adapter.fallback) {
      throw e;
    }

    warn("capture.thread-failed", { adapter: adapter.id, error: e instanceof Error ? e.name : "unknown" });
    return genericAdapter.captureThread(doc, editor);
  }
}

export async function captureContext(
  doc: Document,
  location: PageLocation & Pick<Location, "origin">,
  registry: EditorRegistry,
  adapters?: readonly SiteAdapter[],
  editorId?: string,
): Promise<CaptureResult> {
  const adapter = selectAdapter(location, adapters);
  if (!adapter) {
    return failure("no-adapter", "No adapter supports this page.");
  }

  // A site adapter that finds no draft may have missed a page change, so the selected editor is still captured.
  const found = adapter.findEditor(doc);
  const unrecognized = !found && !adapter.fallback;
  const threadAdapter = unrecognized ? genericAdapter : adapter;
  // A recapture reads only the editor it names.
  const editor = editorId !== undefined ? registry.find(editorId) : (found ?? (unrecognized ? editorForSelection(doc) : null));
  if (!editor && editorId !== undefined) {
    return failure("editor-gone", "The editor is no longer open.");
  }

  if (!editor) {
    return adapter.fallback
      ? failure("no-adapter", "No adapter supports this page and the selection is not in an editor.")
      : failure("no-editor", "No draft editor was found on the page.");
  }

  const offsets = selectionOffsets(doc, editor);
  if (!offsets || offsets.start === offsets.end) {
    return failure("no-selection", "Nothing is selected in the editor.");
  }

  // Taken together before any await, so an edit during the thread wait cannot split them.
  const text = editorText(editor);
  const rich = !isPlainEditor(editor);
  const markdown = rich ? selectionMarkdown(editor, offsets.start, offsets.end) : undefined;
  const signature = rich ? formatSignature(editor, offsets.start, offsets.end) : undefined;
  const embedded = !isTextArea(editor) && embeddedContent(editor, offsets.start, offsets.end).length > 0;
  const hash = await hashText(text);
  const formatHash = signature === undefined ? undefined : await hashText(signature);
  const thread = await readThread(threadAdapter, doc, editor);
  if (!editor.isConnected) {
    if (editorId !== undefined) {
      return failure("editor-gone", "The editor is no longer open.");
    }

    return failure("no-editor", "The draft editor left the page while the thread was being read.");
  }

  const id = registry.idFor(editor);

  return {
    ok: true,
    capture: {
      adapter: adapter.id,
      // Without the query and fragment, which on Gmail carry the search terms.
      pageUrl: location.origin + location.pathname,
      capturedAt: new Date().toISOString(),
      ...(thread.subject ? { subject: thread.subject } : {}),
      editor: { id, hash },
      selection: {
        text: text.slice(offsets.start, offsets.end),
        start: offsets.start,
        end: offsets.end,
        ...(markdown === undefined || formatHash === undefined ? {} : { markdown, formatHash }),
        ...(embedded ? { hasEmbeddedContent: true } : {}),
      },
      thread: { completeness: thread.completeness, collapsedCount: thread.collapsedCount, messages: thread.messages },
    },
  };
}
