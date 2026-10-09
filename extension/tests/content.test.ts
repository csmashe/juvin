// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from "vitest";
import type { PageLocation } from "../src/content/adapters/adapter";
import { PAGE_TEXT_LIMIT } from "../src/content/adapters/generic";
import { selectAdapter } from "../src/content/adapters/registry";
import { applyReplacement, type ExecCommand } from "../src/content/apply";
import { captureContext } from "../src/content/capture";
import { EditorRegistry } from "../src/content/editor-registry";
import { hashText } from "../src/content/hash";
import { offsetsToRange, readPlainText } from "../src/content/plain-text";

const TEST_PAGE: PageLocation & { href: string } = {
  protocol: "moz-extension:",
  hostname: "abc",
  pathname: "/test-page.html",
  href: "moz-extension://abc/test-page.html",
};

const OTHER_SITE: PageLocation & { href: string } = {
  protocol: "https:",
  hostname: "www.example.com",
  pathname: "/compose",
  href: "https://www.example.com/compose",
};

function testPage(): HTMLElement {
  document.body.innerHTML = `
    <h1 id="juvin-test-subject">Offsite</h1>
    <article data-juvin-message>
      <span class="from">Robin &lt;robin@example.com&gt;</span><span class="to">team@example.com, me@example.com</span>
      <time datetime="2026-10-06T09:15:00Z">Oct 6</time><div class="body">First message</div>
    </article>
    <article data-juvin-message>
      <span class="from">Sam &lt;sam@example.com&gt;</span><span class="to"></span><div class="body">Second message</div>
    </article>
    <div id="juvin-test-editor" contenteditable="true"><div>hey robin</div><div>april works</div></div>`;
  return document.getElementById("juvin-test-editor")!;
}

function select(root: HTMLElement, start: number, end: number): void {
  const selection = document.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(offsetsToRange(root, readPlainText(root), start, end));
}

/** Stands in for the browser's insertText: replaces the current selection in the DOM. */
const fakeExec: ExecCommand = (doc, _command, value) => {
  const range = doc.getSelection()!.getRangeAt(0);
  range.deleteContents();
  range.insertNode(doc.createTextNode(value ?? ""));
  return true;
};

describe("selectAdapter", () => {
  it("picks the test-page adapter on the bundled page", () => {
    expect(selectAdapter(TEST_PAGE)?.id).toBe("test-page");
    expect(selectAdapter({ ...TEST_PAGE, protocol: "chrome-extension:" })?.id).toBe("test-page");
  });

  it("falls back to generic on other sites", () => {
    expect(selectAdapter(OTHER_SITE)?.id).toBe("generic");
  });

  it("has no adapter for Gmail until its adapter lands", () => {
    expect(selectAdapter({ protocol: "https:", hostname: "mail.google.com", pathname: "/mail/u/0/" })).toBeUndefined();
  });
});

describe("captureContext", () => {
  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("captures the test page thread, selection, and hash", async () => {
    const editor = testPage();
    select(editor, 4, 9);
    const registry = new EditorRegistry(() => "e-1");
    const result = await captureContext(document, TEST_PAGE, registry);

    expect(result.ok).toBe(true);
    if (!result.ok) {
      return;
    }

    expect(result.capture).toMatchObject({
      adapter: "test-page",
      pageUrl: TEST_PAGE.href,
      subject: "Offsite",
      editor: { id: "e-1", hash: await hashText("hey robin\napril works") },
      selection: { text: "robin", start: 4, end: 9 },
      thread: { completeness: "entire-thread", collapsedCount: 0 },
    });
    expect(result.capture.thread.messages).toEqual([
      { from: "Robin <robin@example.com>", to: ["team@example.com", "me@example.com"], sent: "2026-10-06T09:15:00Z", body: "First message" },
      { from: "Sam <sam@example.com>", to: [], sent: undefined, body: "Second message" },
    ]);
    expect(registry.find("e-1")).toBe(editor);
  });

  it("reports no-selection when the selection is outside the editor", async () => {
    testPage();
    const selection = document.getSelection()!;
    selection.selectAllChildren(document.getElementById("juvin-test-subject")!);
    const result = await captureContext(document, TEST_PAGE, new EditorRegistry());
    expect(!result.ok && result.error.code).toBe("no-selection");
  });

  it("reports no-adapter when the generic adapter finds no editor", async () => {
    document.body.innerHTML = "<main><p>Just an article.</p></main>";
    document.getSelection()!.selectAllChildren(document.querySelector("p")!);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(!result.ok && result.error.code).toBe("no-adapter");
  });

  it("captures visible page text with the generic adapter, without the draft", async () => {
    document.body.innerHTML = `<main><p>Earlier message on the page.</p><div id="ed" contenteditable="true">my draft</div></main>`;
    const editor = document.getElementById("ed")!;
    select(editor, 0, 2);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.thread.completeness).toBe("visible-text-only");
    expect(result.ok && result.capture.thread.messages[0]?.body).toBe("Earlier message on the page.");
    expect(result.ok && result.capture.selection.text).toBe("my");
  });

  it("keeps earlier text that matches the draft when the generic adapter captures", async () => {
    document.body.innerHTML = `<main><p>Thanks for approving the invoice.</p><div id="ed" contenteditable="true">Thanks</div></main>`;
    const editor = document.getElementById("ed")!;
    select(editor, 0, 6);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.thread.messages[0]?.body).toBe("Thanks for approving the invoice.");
  });

  it("does not cut a surrogate pair when the page text hits the limit", async () => {
    const filler = "a".repeat(PAGE_TEXT_LIMIT - 1);
    document.body.innerHTML = `<main><p>${filler}\u{1D400}tail</p><div id="ed" contenteditable="true">my draft</div></main>`;
    select(document.getElementById("ed")!, 0, 2);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    const body = result.ok ? result.capture.thread.messages[0]!.body : "";
    expect(body.length).toBe(PAGE_TEXT_LIMIT - 1);
    expect(body.endsWith("a")).toBe(true);
  });

  it("reports draft-only when the editor is the page's main element", async () => {
    document.body.innerHTML = `<main id="ed" contenteditable="true">my draft</main>`;
    select(document.getElementById("ed")!, 0, 2);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.thread.completeness).toBe("draft-only");
  });

  it("falls back to the body when the main element is not rendered", async () => {
    document.body.innerHTML = `<div style="display:none"><main>hidden</main></div><p>visible</p><div id="ed" contenteditable="true">draft</div>`;
    select(document.getElementById("ed")!, 0, 2);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.thread.messages[0]?.body).toBe("visible");
  });

  it("reports draft-only when the body itself is the editor", async () => {
    document.body.innerHTML = "my draft";
    document.body.setAttribute("contenteditable", "true");
    try {
      select(document.body, 0, 2);
      const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
      expect(result.ok && result.capture.thread.completeness).toBe("draft-only");
    } finally {
      document.body.removeAttribute("contenteditable");
    }
  });

  it("reports draft-only when the page has nothing besides the editor", async () => {
    document.body.innerHTML = `<div id="ed" contenteditable="true">my draft</div>`;
    select(document.getElementById("ed")!, 0, 2);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.thread).toEqual({ completeness: "draft-only", collapsedCount: 0, messages: [] });
  });

  it("captures a textarea selection", async () => {
    document.body.innerHTML = `<textarea id="ta">hello there</textarea>`;
    const textarea = document.getElementById("ta") as HTMLTextAreaElement;
    textarea.focus();
    textarea.setSelectionRange(6, 11);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.selection).toEqual({ text: "there", start: 6, end: 11 });
  });
});

describe("applyReplacement", () => {
  async function captured(): Promise<{ editor: HTMLElement; registry: EditorRegistry; hash: string }> {
    const editor = testPage();
    select(editor, 4, 9);
    const registry = new EditorRegistry(() => "e-1");
    const result = await captureContext(document, TEST_PAGE, registry);
    if (!result.ok) {
      throw new Error("capture failed");
    }

    return { editor, registry, hash: result.capture.editor.hash };
  }

  it("replaces the captured range when the draft is unchanged", async () => {
    const { editor, registry, hash } = await captured();
    document.getSelection()!.removeAllRanges();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, fakeExec);
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hey Robin\napril works");
  });

  it("refuses with draft-changed when the hash does not match", async () => {
    const { editor, registry, hash } = await captured();
    editor.firstElementChild!.append(" and team");
    let called = false;
    const exec: ExecCommand = () => (called = true);
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, exec);
    expect(result).toEqual({ ok: false, error: { code: "draft-changed", message: "The draft changed since it was captured." } });
    expect(called).toBe(false);
  });

  it("refuses with draft-changed when the draft is edited while the hash is computed", async () => {
    const { editor, registry, hash } = await captured();
    let called = false;
    const exec: ExecCommand = () => (called = true);
    const pending = applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, exec);
    editor.firstElementChild!.append(" and team");
    const result = await pending;
    expect(!result.ok && result.error.code).toBe("draft-changed");
    expect(called).toBe(false);
  });

  it("reports internal when the editor ends up with other text than the replacement", async () => {
    const { editor, registry, hash } = await captured();
    const exec: ExecCommand = () => {
      editor.firstElementChild!.textContent = "something else";
      return true;
    };
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, exec);
    expect(!result.ok && result.error.code).toBe("internal");
  });

  it("applies the offsets of the request, not of the latest capture", async () => {
    const { editor, registry, hash } = await captured();
    select(editor, 0, 3);
    expect((await captureContext(document, TEST_PAGE, registry)).ok).toBe(true);
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, fakeExec);
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hey Robin\napril works");
  });

  it("refuses with draft-changed when the offsets fall outside the editor text", async () => {
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 900, text: "Robin" }, fakeExec);
    expect(!result.ok && result.error.code).toBe("draft-changed");
  });

  it("reports editor-gone when the editor cannot take focus", async () => {
    const { editor, registry, hash } = await captured();
    editor.removeAttribute("contenteditable");
    let called = false;
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, () => (called = true));
    expect(!result.ok && result.error.code).toBe("editor-gone");
    expect(called).toBe(false);
  });

  it("normalizes CRLF in the replacement before editing and checking", async () => {
    const { editor, registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin,\r\nhi" }, fakeExec);
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hey Robin,\nhi\napril works");
  });

  it("reports editor-gone for a textarea that became read-only", async () => {
    document.body.innerHTML = `<textarea id="ta">hello there</textarea>`;
    const textarea = document.getElementById("ta") as HTMLTextAreaElement;
    textarea.focus();
    textarea.setSelectionRange(6, 11);
    const registry = new EditorRegistry(() => "e-1");
    const captured = await captureContext(document, OTHER_SITE, registry);
    const hash = captured.ok ? captured.capture.editor.hash : "";
    textarea.readOnly = true;
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 6, end: 11, text: "you" }, () => false);
    expect(!result.ok && result.error.code).toBe("editor-gone");
    expect(textarea.value).toBe("hello there");
  });

  it("does not undo when the command reported success but changed nothing", async () => {
    const { registry, hash } = await captured();
    const commands: string[] = [];
    const exec: ExecCommand = (_doc, command) => {
      commands.push(command);
      return true;
    };
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, exec);
    expect(!result.ok && result.error.code).toBe("internal");
    expect(commands).toEqual(["insertText"]);
  });

  it("rolls back and reports internal when the editor ends up with other text", async () => {
    const { editor, registry, hash } = await captured();
    const original = editor.innerHTML;
    const exec: ExecCommand = (_doc, command) => {
      editor.firstElementChild!.textContent = command === "undo" ? "hey robin" : "something else";
      return true;
    };
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, exec);
    expect(!result.ok && result.error.code).toBe("internal");
    expect(editor.innerHTML).toBe(original);
  });

  it("reports editor-gone for an unknown or removed editor", async () => {
    const { editor, registry, hash } = await captured();
    expect((await applyReplacement(document, registry, { editorId: "e-x", expectedHash: hash, start: 4, end: 9, text: "a" }, fakeExec)).ok).toBe(false);
    editor.remove();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "a" }, fakeExec);
    expect(!result.ok && result.error.code).toBe("editor-gone");
  });

  it("reports internal when the editor rejects the command", async () => {
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, () => false);
    expect(!result.ok && result.error.code).toBe("internal");
  });

  it("reports internal when the command claims success but nothing changed", async () => {
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, () => true);
    expect(!result.ok && result.error.code).toBe("internal");
  });
});
