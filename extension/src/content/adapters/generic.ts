import { isRendered, readPlainText } from "../plain-text";
import { editorForSelection, type SiteAdapter } from "./adapter";

export const PAGE_TEXT_LIMIT = 20_000;

export function normalize(text: string): string {
  return text.replace(/[ \t]+\n/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
}

/** Cuts at `limit` code units without splitting a surrogate pair, which JSON could not carry. */
export function truncate(text: string, limit: number): string {
  if (text.length <= limit) {
    return text;
  }

  const last = text.charCodeAt(limit - 1);
  return text.slice(0, last >= 0xd800 && last <= 0xdbff ? limit - 1 : limit);
}

export const genericAdapter: SiteAdapter = {
  id: "generic",
  fallback: true,
  matches: () => true,
  findEditor: (doc) => editorForSelection(doc),
  async captureThread(doc, editor) {
    const main = doc.querySelector<HTMLElement>("main, [role='main'], article");
    const root = main && !editor.contains(main) && isRendered(main) ? main : doc.body;
    const text = root && !editor.contains(root) ? truncate(normalize(readPlainText(root, editor).text), PAGE_TEXT_LIMIT) : "";
    const subject = doc.title.trim() || undefined;
    if (!text) {
      return { subject, messages: [], completeness: "draft-only", collapsedCount: 0 };
    }

    return {
      subject,
      messages: [{ from: doc.location?.hostname ?? "", body: text }],
      completeness: "visible-text-only",
      collapsedCount: 0,
    };
  },
};
