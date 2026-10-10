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
      this.elements.set(id, new WeakRef(editor));
    }

    return id;
  }

  /** The editor for an id, or null when it was collected or removed from the page. */
  find(id: string): Editor | null {
    const editor = this.elements.get(id)?.deref();
    if (!editor || !editor.isConnected) {
      this.elements.delete(id);
      return null;
    }

    return editor;
  }
}

export function randomEditorId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return "e-" + Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function isTextArea(editor: Editor): editor is HTMLTextAreaElement {
  return editor.tagName === "TEXTAREA";
}

const EDITABLE_VALUES = new Set(["", "true", "plaintext-only"]);

/** True when the editor still accepts input; isContentEditable is not available in every runtime. */
export function isEditable(editor: Editor): boolean {
  if (isTextArea(editor)) {
    return !editor.readOnly && !editor.disabled;
  }

  const value = editor.getAttribute("contenteditable");
  return value !== null && EDITABLE_VALUES.has(value.toLowerCase());
}
