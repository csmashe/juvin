import { describe, expect, it } from "vitest";
import { buildTrimmedContextResponse, byteLength } from "../src/background/trim";
import { FRAME_CAP_BYTES, type Capture, type ThreadMessage } from "../src/shared/protocol";

function captureWith(messages: ThreadMessage[], selection = "Hi team"): Capture {
  return {
    adapter: "test-page",
    pageUrl: "moz-extension://abc/test-page.html",
    capturedAt: "2026-10-09T14:03:21.512Z",
    editor: { id: "e-1", hash: "sha256:00" },
    selection: { text: selection, start: 0, end: selection.length },
    thread: { completeness: "entire-thread", collapsedCount: 0, messages },
  };
}

const message = (n: number, size: number): ThreadMessage => ({ from: `m${n}@example.com`, body: "x".repeat(size) });

describe("buildTrimmedContextResponse", () => {
  it("leaves a small capture untouched", () => {
    const capture = captureWith([message(1, 100)]);
    const response = buildTrimmedContextResponse("c1", capture);
    expect(response).toEqual({ v: 1, type: "context.response", id: "c1", ok: true, capture });
  });

  it("drops the oldest messages first and marks the thread truncated", () => {
    const messages = Array.from({ length: 10 }, (_, i) => message(i, 1000));
    const response = buildTrimmedContextResponse("c1", captureWith(messages), 5000);
    expect(response?.ok).toBe(true);
    if (!response?.ok) {
      return;
    }

    const kept = response.capture.thread.messages;
    expect(kept.length).toBeGreaterThan(0);
    expect(kept.length).toBeLessThan(10);
    expect(kept).toEqual(messages.slice(10 - kept.length));
    expect(response.capture.thread.completeness).toBe("truncated");
    expect(byteLength(response)).toBeLessThanOrEqual(5000);
    expect(byteLength({ ...response, capture: { ...response.capture, thread: { ...response.capture.thread, messages: messages.slice(9 - kept.length) } } })).toBeGreaterThan(5000);
  });

  it("keeps a partial completeness when it drops messages", () => {
    const messages = Array.from({ length: 10 }, (_, i) => message(i, 1000));
    for (const completeness of ["loaded-messages-only", "visible-text-only"] as const) {
      const capture = captureWith(messages);
      const response = buildTrimmedContextResponse("c1", { ...capture, thread: { ...capture.thread, completeness } }, 5000);
      expect(response?.ok && response.capture.thread.messages.length).toBeLessThan(10);
      expect(response?.ok && response.capture.thread.completeness).toBe(completeness);
    }
  });

  it("does not drop a message the truncated response can still hold", () => {
    const capture = captureWith([message(1, 500), message(2, 500)]);
    const fits = buildTrimmedContextResponse("c1", { ...capture, thread: { ...capture.thread, completeness: "truncated", messages: [capture.thread.messages[1]!] } }, Number.MAX_SAFE_INTEGER)!;
    // Two bytes of room with "truncated"; the longer original completeness would push the estimate over.
    const response = buildTrimmedContextResponse("c1", capture, byteLength(fits) + 2);
    expect(response?.ok && response.capture.thread.messages.length).toBe(1);
  });

  it("measures bytes, not characters", () => {
    const wide = Array.from({ length: 4 }, (_, i) => ({ from: `m${i}`, body: "é".repeat(1000) }));
    const response = buildTrimmedContextResponse("c1", captureWith(wide), 5000);
    expect(response && byteLength(response)).toBeLessThanOrEqual(5000);
    expect(response?.ok && response.capture.thread.messages.length).toBe(2);
  });

  it("stays under the real frame cap for a huge thread", () => {
    const messages = Array.from({ length: 40 }, (_, i) => message(i, 50_000));
    const response = buildTrimmedContextResponse("c1", captureWith(messages));
    expect(response && byteLength(response)).toBeLessThan(FRAME_CAP_BYTES);
    expect(response?.ok && response.capture.thread.completeness).toBe("truncated");
  });

  it("returns null when the selection alone is over the cap", () => {
    expect(buildTrimmedContextResponse("c1", captureWith([message(1, 10)], "y".repeat(6000)), 5000)).toBeNull();
  });
});
