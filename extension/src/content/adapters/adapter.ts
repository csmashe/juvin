import type { Completeness, ThreadMessage } from "../../shared/protocol";

export interface ThreadSnapshot {
  subject?: string;
  messages: ThreadMessage[];
  completeness: Completeness;
  collapsedCount: number;
}

export type PageLocation = Pick<Location, "protocol" | "hostname" | "pathname">;

/** The only code that reads a site's DOM. Selectors stay inside the adapter. */
export interface SiteAdapter {
  id: string;
  /** True for the catch-all adapter: when it finds no editor the page counts as having no adapter. */
  fallback?: boolean;
  matches(location: PageLocation): boolean;
  findEditor(doc: Document): HTMLElement | null;
  captureThread(doc: Document, editor: HTMLElement): Promise<ThreadSnapshot>;
}

const EDITABLE_VALUES = new Set(["", "true", "plaintext-only"]);

function isEditableHost(element: Element): boolean {
  const value = element.getAttribute("contenteditable");
  return value !== null && EDITABLE_VALUES.has(value.toLowerCase());
}

/** The textarea or contenteditable host that holds the current selection, or null. */
export function editorForSelection(doc: Document): HTMLElement | null {
  const active = doc.activeElement;
  if (active instanceof HTMLTextAreaElement) {
    return active;
  }

  const anchor = doc.getSelection()?.anchorNode;
  let element: Element | null = anchor instanceof Element ? anchor : (anchor?.parentElement ?? null);
  let host: HTMLElement | null = null;
  while (element) {
    const editable = element.closest("[contenteditable]");
    if (!editable || !isEditableHost(editable)) {
      break;
    }

    host = editable as HTMLElement;
    element = editable.parentElement;
  }

  return host;
}

/** innerText where the browser has it (it skips hidden text), textContent otherwise. */
export function visibleText(element: HTMLElement): string {
  return (typeof element.innerText === "string" ? element.innerText : element.textContent) ?? "";
}
