// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";

const sendMessage = vi.fn((_message: unknown) => Promise.resolve());

vi.mock("webextension-polyfill", () => ({
  default: { runtime: { sendMessage, onMessage: { addListener: () => undefined } } },
}));

await import("../src/content/index");

function focusIn(html: string): unknown[] {
  document.body.innerHTML = html;
  sendMessage.mockClear();
  document.body.firstElementChild!.dispatchEvent(new FocusEvent("focusin", { bubbles: true }));
  return sendMessage.mock.calls.map(([message]) => message);
}

describe("content script activity notice", () => {
  it("tells the background when a textarea or an editable element takes focus", () => {
    expect(focusIn("<textarea></textarea>")).toEqual([{ kind: "juvin.activity" }]);
    expect(focusIn('<div contenteditable="true"></div>')).toEqual([{ kind: "juvin.activity" }]);
  });

  it("stays quiet for other elements", () => {
    expect(focusIn("<input>")).toEqual([]);
    expect(focusIn("<button></button>")).toEqual([]);
    expect(focusIn('<div contenteditable="false" tabindex="0"></div>')).toEqual([]);
  });
});
