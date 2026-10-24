import { describe, expect, it } from "vitest";
import {
  applyError,
  applyOk,
  contextError,
  contextOk,
  errorMessage,
  hello,
  menuChoose,
  menuClose,
  menuError,
  menuOk,
  parseIncoming,
  ping,
  pong,
  shortcutPressed,
  testPageError,
  testPageOk,
  type Capture,
} from "../src/shared/protocol";

// The examples below are copied from the connector protocol spec (docs/connector-protocol.md in juvin-internal).
const spec = {
  hello: `{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"firefox","version":"157.0"}}`,
  config: `{"v":1,"type":"config",
 "actions":[{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"}],
 "adapters":["gmail","generic","test-page"],
 "shortcut":"Ctrl+Shift+F",
 "health":{"mode":"local","modelReachable":null}}`,
  ping: `{"v":1,"type":"ping","id":"p1"}`,
  pong: `{"v":1,"type":"pong","id":"p1"}`,
  shortcutPressed: `{"v":1,"type":"shortcut.pressed"}`,
  contextRequest: `{"v":1,"type":"context.request","id":"c1"}`,
  recaptureRequest: `{"v":1,"type":"context.request","id":"c2","editorId":"e-7f3a"}`,
  hotkeyRequest: `{"v":1,"type":"context.request","id":"c3","requireFocus":true}`,
  contextOk: `{"v":1,"type":"context.response","id":"c1","ok":true,
 "capture":{
   "adapter":"gmail",
   "pageUrl":"https://mail.google.com/mail/u/0/",
   "capturedAt":"2026-10-09T14:03:21.512Z",
   "subject":"Invoice for September",
   "editor":{"id":"e-7f3a","hash":"sha256:9f86d0..."},
   "selection":{"text":"Hi team, ...","start":0,"end":118,"markdown":"Hi **team**, ...","formatHash":"sha256:5d41a1..."},
   "thread":{
     "completeness":"entire-thread",
     "collapsedCount":0,
     "messages":[{"from":"Ana <ana@example.com>","to":["me@example.com"],"sent":"2026-10-08T09:12:00Z","body":"..."}]
   }}}`,
  contextError: `{"v":1,"type":"context.response","id":"c1","ok":false,"error":{"code":"no-selection","message":"Nothing is selected in the editor."}}`,
  applyRequest: `{"v":1,"type":"apply.request","id":"a1","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi team, thank you for the update..."}`,
  markdownApplyRequest: `{"v":1,"type":"apply.request","id":"a2","editorId":"e-7f3a","expectedHash":"sha256:9f86d0...","start":0,"end":118,"text":"Hi **team**, thank you for the update...","format":"markdown","expectedFormatHash":"sha256:5d41a1..."}`,
  applyOk: `{"v":1,"type":"apply.response","id":"a1","ok":true}`,
  applyError: `{"v":1,"type":"apply.response","id":"a1","ok":false,"error":{"code":"draft-changed","message":"The draft changed since it was captured."}}`,
  testPageRequest: `{"v":1,"type":"testpage.request","id":"t1"}`,
  testPageOk: `{"v":1,"type":"testpage.response","id":"t1","ok":true}`,
  testPageError: `{"v":1,"type":"testpage.response","id":"t1","ok":false,"error":{"code":"internal","message":"The sample page could not be opened."}}`,
  menuShow: `{"v":1,"type":"menu.show","id":"m1","menuId":"menu-4","editorId":"e-7f3a",
 "slots":[{"id":"missing","title":"What am I missing?"},{"id":"soften","title":"Soften"},{"id":"clearer","title":"Make clearer"},{"id":"shorter","title":"Make shorter"},{"id":"grammar","title":"Fix grammar"}],
 "more":[{"id":"custom-3f2a9b","title":"Pirate voice"}],
 "status":["6 messages","Entire thread","Private server: qwen3.5:27b"]}`,
  menuOk: `{"v":1,"type":"menu.response","id":"m1","ok":true}`,
  menuError: `{"v":1,"type":"menu.response","id":"m1","ok":false,"error":{"code":"unsupported","message":"This page cannot show the menu."}}`,
  menuChoose: `{"v":1,"type":"menu.choose","menuId":"menu-4","action":"soften"}`,
  menuChooseCustom: `{"v":1,"type":"menu.choose","menuId":"menu-4","action":"custom","instruction":"Make it sound less formal."}`,
  menuRunning: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"running","action":"soften"}`,
  menuApplied: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"applied"}`,
  menuAnswer: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"answer","text":"- The draft does not answer the question about the deadline."}`,
  menuDraftChanged: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"draft-changed","text":"Hi team, thank you for the update..."}`,
  menuFailed: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"error","text":"The model did not answer in time.","canRetry":true}`,
  menuUnapplied: `{"v":1,"type":"menu.update","menuId":"menu-4","state":"error","text":"Copy the rewrite instead.","canRetry":false,"result":"Hi team, thank you for the update..."}`,
  menuClose: `{"v":1,"type":"menu.close","menuId":"menu-4"}`,
  error: `{"v":1,"type":"error","id":"c1","error":{"code":"unknown-type","message":"Unknown message type."}}`,
};

const json = (text: string): unknown => JSON.parse(text);

describe("messages the extension sends", () => {
  it("builds hello", () => {
    expect(hello({ id: "extension@juvin.app", version: "0.1.0" }, { name: "firefox", version: "157.0" })).toEqual(json(spec.hello));
  });

  it("builds ping, pong, and shortcut.pressed", () => {
    expect(ping("p1")).toEqual(json(spec.ping));
    expect(pong("p1")).toEqual(json(spec.pong));
    expect(shortcutPressed()).toEqual(json(spec.shortcutPressed));
  });

  it("builds both context.response shapes", () => {
    const example = json(spec.contextOk) as { capture: Capture };
    expect(contextOk("c1", example.capture)).toEqual(example);
    expect(contextError("c1", { code: "no-selection", message: "Nothing is selected in the editor." })).toEqual(json(spec.contextError));
  });

  it("builds both apply.response shapes", () => {
    expect(applyOk("a1")).toEqual(json(spec.applyOk));
    expect(applyError("a1", { code: "draft-changed", message: "The draft changed since it was captured." })).toEqual(json(spec.applyError));
  });

  it("builds both testpage.response shapes", () => {
    expect(testPageOk("t1")).toEqual(json(spec.testPageOk));
    expect(testPageError("t1", { code: "internal", message: "The sample page could not be opened." })).toEqual(json(spec.testPageError));
  });

  it("builds both menu.response shapes", () => {
    expect(menuOk("m1")).toEqual(json(spec.menuOk));
    expect(menuError("m1", { code: "unsupported", message: "This page cannot show the menu." })).toEqual(json(spec.menuError));
  });

  it("builds menu.choose with an instruction only when given one", () => {
    expect(menuChoose("menu-4", "soften")).toEqual(json(spec.menuChoose));
    expect(menuChoose("menu-4", "custom", "Make it sound less formal.")).toEqual(json(spec.menuChooseCustom));
  });

  it("builds menu.close", () => {
    expect(menuClose("menu-4")).toEqual(json(spec.menuClose));
  });

  it("builds error with and without an id", () => {
    expect(errorMessage("c1", "unknown-type", "Unknown message type.")).toEqual(json(spec.error));
    expect(errorMessage(undefined, "bad-message", "x")).not.toHaveProperty("id");
  });
});

describe("parseIncoming", () => {
  it.each([
    ["config", spec.config],
    ["ping", spec.ping],
    ["pong", spec.pong],
    ["context.request", spec.contextRequest],
    ["context.request with an editor", spec.recaptureRequest],
    ["context.request from the desktop hotkey", spec.hotkeyRequest],
    ["apply.request", spec.applyRequest],
    ["apply.request with Markdown", spec.markdownApplyRequest],
    ["testpage.request", spec.testPageRequest],
    ["menu.show", spec.menuShow],
    ["menu.update running", spec.menuRunning],
    ["menu.update applied", spec.menuApplied],
    ["menu.update answer", spec.menuAnswer],
    ["menu.update draft-changed", spec.menuDraftChanged],
    ["menu.update error", spec.menuFailed],
    ["menu.update error with the unapplied result", spec.menuUnapplied],
    ["menu.close", spec.menuClose],
    ["error", spec.error],
  ])("accepts the spec example for %s", (_, text) => {
    const result = parseIncoming(json(text));
    expect(result).toEqual({ ok: true, message: json(text) });
  });

  it("reads a null format as plain text", () => {
    const result = parseIncoming({ v: 1, type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 1, text: "t", format: null });
    expect(result.ok && result.message).not.toHaveProperty("format");
  });

  it("ignores unknown properties", () => {
    const result = parseIncoming({ v: 1, type: "context.request", id: "c2", extra: { nested: true } });
    expect(result).toEqual({ ok: true, message: { v: 1, type: "context.request", id: "c2" } });
  });

  it("rejects a non-object as bad-message", () => {
    for (const raw of [null, 42, "hello", [1, 2]]) {
      const result = parseIncoming(raw);
      expect(result.ok).toBe(false);
      expect(!result.ok && result.reply.error.code).toBe("bad-message");
    }
  });

  it("rejects another version as unsupported-version and echoes the id", () => {
    const result = parseIncoming({ v: 2, type: "ping", id: "p9" });
    expect(result).toEqual({
      ok: false,
      reply: { v: 1, type: "error", id: "p9", error: { code: "unsupported-version", message: "Unsupported protocol version." } },
      replyDue: true,
    });
  });

  it("rejects a version that is not the integer 1 as unsupported-version", () => {
    for (const v of [1.5, 0, 2]) {
      expect(parseIncoming({ v, type: "ping", id: "p1" })).toMatchObject({ ok: false, reply: { error: { code: "unsupported-version" } }, replyDue: true });
    }
  });

  it.each([
    ["no version", { type: "error", error: { code: "x", message: "y" } }],
    ["another version", { v: 2, type: "error", error: { code: "x", message: "y" } }],
    ["no error payload", { v: 1, type: "error" }],
    ["a malformed error payload", { v: 1, type: "error", error: { code: 7 } }],
  ])("rejects an error with %s without a reply due", (_, raw) => {
    expect(parseIncoming(raw)).toMatchObject({ ok: false, replyDue: false });
  });

  it("owes a reply for every other rejected message", () => {
    for (const raw of [null, { type: "ping", id: "p1" }, { v: 1, type: "ping" }, { v: 1, type: "something.new" }]) {
      expect(parseIncoming(raw)).toMatchObject({ ok: false, replyDue: true });
    }
  });

  it("rejects a missing or non-numeric version as bad-message", () => {
    for (const raw of [{ type: "ping", id: "p1" }, { v: "1", type: "ping", id: "p1" }]) {
      const result = parseIncoming(raw);
      expect(!result.ok && result.reply).toMatchObject({ id: "p1", error: { code: "bad-message" } });
    }
  });

  it("rejects a missing type as bad-message", () => {
    const result = parseIncoming({ v: 1, id: "x1" });
    expect(!result.ok && result.reply).toMatchObject({ id: "x1", error: { code: "bad-message" } });
  });

  it("rejects an unknown type as unknown-type", () => {
    const result = parseIncoming({ v: 1, type: "something.new", id: "s1" });
    expect(!result.ok && result.reply).toEqual(json(`{"v":1,"type":"error","id":"s1","error":{"code":"unknown-type","message":"Unknown message type."}}`));
  });

  it("rejects requests with missing fields", () => {
    expect(parseIncoming({ v: 1, type: "context.request" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "testpage.request" })).toMatchObject({ ok: false, reply: { error: { code: "bad-message" } } });
    expect(parseIncoming({ v: 1, type: "context.request", id: "c1", editorId: 7 }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "context.request", id: "c1", requireFocus: "yes" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", text: "t" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 3, end: 1, text: "t" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 1, text: "t", format: "html" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 1, text: "t", format: "markdown" }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "config", actions: [], adapters: [], shortcut: "x", health: { mode: "cloud", modelReachable: null } }).ok).toBe(false);
    expect(parseIncoming({ v: 1, type: "config", actions: [], adapters: [], shortcut: "x", shortcutKey: 7, health: { mode: "local", modelReachable: null } }).ok).toBe(false);
  });

  it("keeps a config's shortcutKey when the layout moved the key", () => {
    const parsed = parseIncoming({ v: 1, type: "config", actions: [], adapters: [], shortcut: "Super+Shift+Z", shortcutKey: "Y", health: { mode: "local", modelReachable: null } });

    expect(parsed.ok && parsed.message).toMatchObject({ type: "config", shortcut: "Super+Shift+Z", shortcutKey: "Y" });
  });
});

describe("parseIncoming for the action menu", () => {
  const menu = (): Record<string, unknown> => json(spec.menuShow) as Record<string, unknown>;
  const slots = (): unknown[] => menu().slots as unknown[];

  it("drops unknown properties from menu.show and its actions", () => {
    const raw = { ...menu(), extra: 1, slots: slots().map((slot) => ({ ...(slot as object), icon: "x" })) };
    expect(parseIncoming(raw)).toEqual({ ok: true, message: json(spec.menuShow) });
  });

  it.each([
    ["no id", { id: undefined }],
    ["no menuId", { menuId: undefined }],
    ["a numeric menuId", { menuId: 4 }],
    ["no editorId", { editorId: undefined }],
    ["four slots", { slots: slots().slice(0, 4) }],
    ["six slots", { slots: [...slots(), { id: "extra", title: "Extra" }] }],
    ["a slot without a title", { slots: [...slots().slice(0, 4), { id: "grammar" }] }],
    ["a slot that is not an action", { slots: [...slots().slice(0, 4), "grammar"] }],
    ["no more list", { more: undefined }],
    ["a more entry that is not an action", { more: [{ id: 7, title: "Pirate voice" }] }],
    ["no status", { status: undefined }],
    ["a status that is not text", { status: ["6 messages", 6] }],
  ])("rejects menu.show with %s", (_, change) => {
    expect(parseIncoming({ ...menu(), ...change })).toMatchObject({ ok: false, reply: { error: { code: "bad-message" } } });
  });

  it.each([
    ["no menuId", { v: 1, type: "menu.update", state: "applied" }],
    ["an unknown state", { v: 1, type: "menu.update", menuId: "menu-4", state: "done" }],
    ["running without an action", { v: 1, type: "menu.update", menuId: "menu-4", state: "running" }],
    ["an answer without text", { v: 1, type: "menu.update", menuId: "menu-4", state: "answer" }],
    ["draft-changed without text", { v: 1, type: "menu.update", menuId: "menu-4", state: "draft-changed" }],
    ["an error without canRetry", { v: 1, type: "menu.update", menuId: "menu-4", state: "error", text: "Failed." }],
    ["an error without text", { v: 1, type: "menu.update", menuId: "menu-4", state: "error", canRetry: false }],
    ["an error with a non-text result", { v: 1, type: "menu.update", menuId: "menu-4", state: "error", text: "Failed.", canRetry: false, result: 7 }],
    ["applied with a non-text result", { v: 1, type: "menu.update", menuId: "menu-4", state: "applied", result: ["x"] }],
    ["menu.close without a menuId", { v: 1, type: "menu.close" }],
  ])("rejects %s", (_, raw) => {
    expect(parseIncoming(raw)).toMatchObject({ ok: false, reply: { error: { code: "bad-message" } } });
  });

  it("keeps only the fields each menu.update state carries", () => {
    const result = parseIncoming({ v: 1, type: "menu.update", menuId: "menu-4", state: "applied", text: "stray", canRetry: true });
    expect(result).toEqual({ ok: true, message: json(spec.menuApplied) });
  });
});
