import type { ThreadMessage } from "../../shared/protocol";
import { visibleText, type SiteAdapter } from "./adapter";

const EXTENSION_PROTOCOLS = new Set(["chrome-extension:", "moz-extension:", "extension:"]);

const SELECTORS = {
  editor: "#juvin-test-editor",
  subject: "#juvin-test-subject",
  message: "[data-juvin-message]",
  from: ".from",
  to: ".to",
  sent: "time[datetime]",
  body: ".body",
};

function textOf(parent: Element, selector: string): string {
  const element = parent.querySelector<HTMLElement>(selector);
  return element ? visibleText(element).trim() : "";
}

export const testPageAdapter: SiteAdapter = {
  id: "test-page",
  matches: (location) => EXTENSION_PROTOCOLS.has(location.protocol) && location.pathname.endsWith("/test-page.html"),
  findEditor: (doc) => doc.querySelector<HTMLElement>(SELECTORS.editor),
  captureThread(doc) {
    const messages: ThreadMessage[] = Array.from(doc.querySelectorAll(SELECTORS.message), (element) => {
      const to = textOf(element, SELECTORS.to)
        .split(",")
        .map((address) => address.trim())
        .filter((address) => address.length > 0);
      const sent = element.querySelector(SELECTORS.sent)?.getAttribute("datetime") ?? undefined;
      return { from: textOf(element, SELECTORS.from), to, sent, body: textOf(element, SELECTORS.body) };
    });

    const subject = textOf(doc.documentElement, SELECTORS.subject) || undefined;
    return { subject, messages, completeness: "entire-thread", collapsedCount: 0 };
  },
};
