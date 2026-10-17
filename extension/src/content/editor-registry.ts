export type Editor = HTMLElement;

export interface Offsets {
  start: number;
  end: number;
}

/** Stable ids for editor elements for the life of the page, without keeping removed elements alive. */
export class EditorRegistry {
  private readonly ids = new WeakMap<Editor, string>();
  private readonly elements = new Map<string, WeakRef<Editor>>();

  constructor(private readonly newId: () => string = randomEditorId) {}

  idFor(editor: Editor): string {
    let id = this.ids.get(editor);
    if (id === undefined) {
      id = this.newId();
      this.ids.set(editor, id);
    }

    if (!this.elements.has(id)) {
      this.elements.set(id, new WeakRef(editor));
    }

    return id;
  }

  /** The editor for an id, or null when it was collected or is off the page; an editor detached for a moment is found again once back. */
  find(id: string): Editor | null {
    const editor = this.elements.get(id)?.deref();
    if (!editor) {
      this.elements.delete(id);
      return null;
    }

    return editor.isConnected ? editor : null;
  }
}

export function randomEditorId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return "e-" + Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function isTextArea(editor: Editor): editor is HTMLTextAreaElement {
  return editor.tagName === "TEXTAREA";
}

/** A textarea or a plaintext-only host: it keeps no formatting, so it gets neither Markdown nor HTML. */
export function isPlainEditor(editor: Editor): boolean {
  return isTextArea(editor) || editor.getAttribute("contenteditable")?.toLowerCase() === "plaintext-only";
}

const EDITABLE_VALUES = new Set(["", "true", "plaintext-only"]);

/** True for an element whose contenteditable attribute makes it editable; isContentEditable is not available in every runtime. */
export function isEditableHost(element: Element): boolean {
  const value = element.getAttribute("contenteditable");
  return value !== null && EDITABLE_VALUES.has(value.toLowerCase());
}

/** True when the editor still accepts input. */
export function isEditable(editor: Editor): boolean {
  return isTextArea(editor) ? !editor.readOnly && !editor.disabled : isEditableHost(editor);
}
