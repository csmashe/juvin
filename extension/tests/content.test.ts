// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from "vitest";
import type { PageLocation, SiteAdapter } from "../src/content/adapters/adapter";
import { PAGE_TEXT_LIMIT } from "../src/content/adapters/generic";
import { createGmailAdapter } from "../src/content/adapters/gmail";
import { outlookAdapter } from "../src/content/adapters/outlook";
import { selectAdapter } from "../src/content/adapters/registry";
import { applyReplacement, type ExecCommand } from "../src/content/apply";
import { captureContext } from "../src/content/capture";
import { EditorRegistry } from "../src/content/editor-registry";
import { hashText } from "../src/content/hash";
import { formatSignature } from "../src/content/formatting";
import { offsetsToRange, readPlainText } from "../src/content/plain-text";
import type { CaptureResult } from "../src/shared/runtime-messages";

type TestLocation = PageLocation & Pick<Location, "origin">;

const TEST_PAGE: TestLocation = {
  protocol: "moz-extension:",
  hostname: "abc",
  pathname: "/test-page.html",
  origin: "moz-extension://abc",
};

const OTHER_SITE: TestLocation = {
  protocol: "https:",
  hostname: "www.example.com",
  pathname: "/compose",
  origin: "https://www.example.com",
};

const GMAIL: TestLocation = {
  protocol: "https:",
  hostname: "mail.google.com",
  pathname: "/mail/u/0/",
  origin: "https://mail.google.com",
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

/** Stands in for the browser's insertText and insertHTML: replaces the current selection in the DOM. */
const fakeExec: ExecCommand = (doc, command, value) => {
  const range = doc.getSelection()!.getRangeAt(0);
  range.deleteContents();
  if (command === "insertHTML") {
    const template = doc.createElement("template");
    template.innerHTML = value ?? "";
    range.insertNode(template.content);
  } else {
    range.insertNode(doc.createTextNode(value ?? ""));
  }

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

  it("picks the Gmail adapter on mail.google.com", () => {
    expect(selectAdapter(GMAIL)?.id).toBe("gmail");
  });
});

describe("EditorRegistry", () => {
  it("finds an editor again after it is detached and reattached", () => {
    document.body.innerHTML = `<div id="ed" contenteditable="true">draft</div>`;
    const editor = document.getElementById("ed")!;
    const registry = new EditorRegistry(() => "e-1");
    expect(registry.idFor(editor)).toBe("e-1");
    editor.remove();
    expect(registry.find("e-1")).toBeNull();
    document.body.append(editor);
    expect(registry.find("e-1")).toBe(editor);
    expect(registry.idFor(editor)).toBe("e-1");
  });
});

describe("captureContext", () => {
  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("sends the page URL without its query or fragment", async () => {
    const original = location.href;
    window.history.replaceState(null, "", "/mail/u/0/?q=secret#search/invoice+overdue");
    document.body.innerHTML = `<div id="ed" contenteditable="true">my draft</div>`;
    select(document.getElementById("ed")!, 0, 2);
    const result = await captureContext(document, location, new EditorRegistry());
    window.history.replaceState(null, "", original);
    expect(result.ok && result.capture.pageUrl).toBe(`${location.origin}/mail/u/0/`);
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
      pageUrl: "moz-extension://abc/test-page.html",
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

  // The generic adapter follows the selection, so only the editor id keeps a recapture on its editor.
  describe("recapture by editor id", () => {
    function secondEditor(): HTMLElement {
      const other = document.createElement("div");
      other.setAttribute("contenteditable", "true");
      other.textContent = "another draft";
      document.body.append(other);
      return other;
    }

    async function firstCapture(): Promise<{ editor: HTMLElement; registry: EditorRegistry }> {
      const editor = testPage();
      select(editor, 4, 9);
      let next = 0;
      const registry = new EditorRegistry(() => `e-${++next}`);
      expect((await captureContext(document, OTHER_SITE, registry)).ok).toBe(true);
      return { editor, registry };
    }

    it("captures the named editor when the selection is inside it", async () => {
      const { editor, registry } = await firstCapture();
      select(editor, 0, 3);
      const result = await captureContext(document, OTHER_SITE, registry, undefined, "e-1");
      expect(result.ok && { id: result.capture.editor.id, text: result.capture.selection.text }).toEqual({ id: "e-1", text: "hey" });
    });

    it("reports editor-gone when the named editor was removed, even with another editor selected", async () => {
      const { editor, registry } = await firstCapture();
      editor.remove();
      select(secondEditor(), 0, 7);
      const result = await captureContext(document, OTHER_SITE, registry, undefined, "e-1");
      expect(!result.ok && result.error.code).toBe("editor-gone");
    });

    it("reports no-selection when the selection is in a different editor", async () => {
      const { registry } = await firstCapture();
      select(secondEditor(), 0, 7);
      const result = await captureContext(document, OTHER_SITE, registry, undefined, "e-1");
      expect(!result.ok && result.error.code).toBe("no-selection");
    });
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

  it("adds the selection as Markdown for a rich editor", async () => {
    document.body.innerHTML = `<div id="ed" contenteditable="true"><div>Hi <b>Ana</b>,</div><ul><li>one</li></ul></div>`;
    const editor = document.getElementById("ed")!;
    select(editor, 0, 11);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.selection).toEqual({ text: "Hi Ana,\none", start: 0, end: 11, markdown: "Hi **Ana**,\n- one", formatHash: await hashText(formatSignature(editor, 0, 11)) });
  });

  it("takes the Markdown from the same draft as the plain text when the editor changes during the thread wait", async () => {
    document.body.innerHTML = `<div id="ed" contenteditable="true">hey <b>robin</b></div>`;
    const editor = document.getElementById("ed")!;
    select(editor, 0, 9);
    const formatHash = await hashText(formatSignature(editor, 0, 9));
    const editing: SiteAdapter = {
      id: "editing",
      matches: () => true,
      findEditor: () => editor,
      captureThread: async () => {
        editor.innerHTML = "hey <i>someone else</i>";
        return { messages: [], completeness: "draft-only", collapsedCount: 0 };
      },
    };
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry(), [editing]);
    expect(result.ok && result.capture.selection).toEqual({ text: "hey robin", start: 0, end: 9, markdown: "hey **robin**", formatHash });
  });

  it("sends no Markdown for a plaintext-only editor", async () => {
    document.body.innerHTML = `<div id="ed" contenteditable="plaintext-only">hey robin</div>`;
    select(document.getElementById("ed")!, 4, 9);
    const result = await captureContext(document, OTHER_SITE, new EditorRegistry());
    expect(result.ok && result.capture.selection).toEqual({ text: "robin", start: 4, end: 9 });
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
  async function captured(): Promise<{ editor: HTMLElement; registry: EditorRegistry; hash: string; formatHash: string }> {
    const editor = testPage();
    select(editor, 4, 9);
    const registry = new EditorRegistry(() => "e-1");
    const result = await captureContext(document, TEST_PAGE, registry);
    if (!result.ok) {
      throw new Error("capture failed");
    }

    return { editor, registry, hash: result.capture.editor.hash, formatHash: result.capture.selection.formatHash! };
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

  it("accepts an edit the editor stored with no-break spaces in a run of spaces", async () => {
    const { editor, registry, hash } = await captured();
    const exec: ExecCommand = (doc, command, value) => fakeExec(doc, command, value?.replaceAll("   ", "\u00a0 \u00a0"));
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "*   Robin" }, exec);
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hey *\u00a0 \u00a0Robin\napril works");
  });

  it("still reports internal when a rich editor's text differs by more than no-break spaces", async () => {
    const { registry, hash } = await captured();
    const exec: ExecCommand = (doc, command, value) => fakeExec(doc, command, value?.replace("Robin", "Rob\u00a0in"));
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "*   Robin" }, exec);
    expect(!result.ok && result.error.code).toBe("internal");
  });

  it("reports internal when a rich editor is left untouched by a change that differs only in spaces", async () => {
    document.body.innerHTML = `<div id="ed" contenteditable="true">10\u00a0kg</div>`;
    const editor = document.getElementById("ed")!;
    select(editor, 0, 5);
    const registry = new EditorRegistry(() => "e-1");
    const capture = await captureContext(document, OTHER_SITE, registry);
    const hash = capture.ok ? capture.capture.editor.hash : "";
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 0, end: 5, text: "10 kg" }, () => true);
    expect(!result.ok && result.error.code).toBe("internal");
  });

  it("keeps a textarea check exact, so a command that changed nothing is not a success", async () => {
    document.body.innerHTML = `<textarea id="ta">10 kg</textarea>`;
    const textarea = document.getElementById("ta") as HTMLTextAreaElement;
    textarea.focus();
    textarea.setSelectionRange(0, 5);
    const registry = new EditorRegistry(() => "e-1");
    const capture = await captureContext(document, OTHER_SITE, registry);
    const hash = capture.ok ? capture.capture.editor.hash : "";
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 0, end: 5, text: "10\u00a0kg" }, () => true);
    expect(!result.ok && result.error.code).toBe("internal");
    expect(textarea.value).toBe("10 kg");
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

  describe("a whole line that a block follows", () => {
    // Gmail's draft on Chrome: the first line is bare text and each later line a div.
    const GMAIL_DRAFT = `<div dir="ltr">line one<div>line two</div></div>`;

    async function capturedLine(start: number, end: number, text: string, markdown: boolean): Promise<{ editor: HTMLElement; registry: EditorRegistry; request: Parameters<typeof applyReplacement>[2] }> {
      document.body.innerHTML = `<div id="ed" contenteditable="true">${GMAIL_DRAFT}</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, start, end);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      if (!capture.ok) {
        throw new Error("capture failed");
      }

      const formatHash = capture.capture.selection.formatHash!;
      return { editor, registry, request: { editorId: "e-1", expectedHash: capture.capture.editor.hash, start, end, text, ...(markdown ? { format: "markdown" as const, expectedFormatHash: formatHash } : {}) } };
    }

    /** Chrome emptying the line leaves a <br> after the inserted text, unless the insertion is a block of its own. */
    function chromeExec(editor: HTMLElement, commands: string[]): ExecCommand {
      const history: string[] = [];
      return (doc, command, value) => {
        commands.push(command);
        if (command === "undo") {
          editor.innerHTML = history.pop() ?? editor.innerHTML;
          return true;
        }

        history.push(editor.innerHTML);
        const range = doc.getSelection()!.getRangeAt(0);
        const line = range.startContainer.parentNode!;
        const emptied = range.startOffset === 0 && range.endOffset === (range.endContainer as Text).data.length;
        fakeExec(doc, command, value);
        if (emptied && !value?.startsWith("<div>")) {
          line.insertBefore(doc.createElement("br"), line.querySelector("div"));
        }

        return true;
      };
    }

    for (const markdown of [false, true]) {
      it(`replaces the line without an extra empty line after it${markdown ? " from Markdown" : ""}`, async () => {
        const { editor, registry, request } = await capturedLine(0, 8, "the rewritten first line", markdown);
        const commands: string[] = [];
        const exec = chromeExec(editor, commands);
        const result = await applyReplacement(document, registry, request, exec);
        expect(result).toEqual({ ok: true });
        expect(commands).toEqual([markdown ? "insertHTML" : "insertText", "undo", "insertHTML"]);
        expect(editor.querySelector("br")).toBeNull();
        expect(editor.innerHTML).toBe(`<div dir="ltr"><div>the rewritten first line</div><div>line two</div></div>`);

        exec(document, "undo");
        expect(editor.innerHTML).toBe(GMAIL_DRAFT);
      });
    }

    it("keeps line breaks and runs of spaces in a multi-line plain replacement", async () => {
      const { editor, registry, request } = await capturedLine(0, 8, "two  spaced\nlines", false);
      const result = await applyReplacement(document, registry, request, chromeExec(editor, []));
      expect(result).toEqual({ ok: true });
      expect(editor.innerHTML).toBe(`<div dir="ltr"><div>two&nbsp; spaced<br>lines</div><div>line two</div></div>`);
    });

    it("edits once when no <br> is left behind", async () => {
      const { editor, registry, request } = await capturedLine(0, 4, "LINE", false);
      const commands: string[] = [];
      const result = await applyReplacement(document, registry, request, chromeExec(editor, commands));
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertText"]);
      expect(editor.innerHTML).toBe(`<div dir="ltr">LINE one<div>line two</div></div>`);
    });

    it("leaves the edit alone when Undo did not restore the draft", async () => {
      const { editor, registry, request } = await capturedLine(0, 8, "the rewritten first line", false);
      const commands: string[] = [];
      const chrome = chromeExec(editor, commands);
      const exec: ExecCommand = (doc, command, value) => (command === "undo" ? (commands.push(command), true) : chrome(doc, command, value));
      const result = await applyReplacement(document, registry, request, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertText", "undo"]);
      expect(readPlainText(editor).text).toBe("the rewritten first line\nline two");
    });

    it("never retries an edit of part of a line, even when the replacement brings its own breaks", async () => {
      const { editor, registry, request } = await capturedLine(5, 8, "ONE\n\n\n- item", true);
      const commands: string[] = [];
      const result = await applyReplacement(document, registry, request, chromeExec(editor, commands));
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertHTML"]);
      expect(readPlainText(editor).text.startsWith("line ONE")).toBe(true);
    });

    it("redoes the edit when Undo left the draft in between", async () => {
      const { editor, registry, request } = await capturedLine(0, 8, "the rewritten first line", false);
      const commands: string[] = [];
      const chrome = chromeExec(editor, commands);
      let edited = "";
      const exec: ExecCommand = (doc, command, value) => {
        if (command === "undo") {
          commands.push(command);
          edited = editor.innerHTML;
          editor.innerHTML = `<div dir="ltr">neither<div>line two</div></div>`;
          return true;
        }

        if (command === "redo") {
          commands.push(command);
          editor.innerHTML = edited;
          return true;
        }

        return chrome(doc, command, value);
      };
      const result = await applyReplacement(document, registry, request, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertText", "undo", "redo"]);
      expect(readPlainText(editor).text).toBe("the rewritten first line\nline two");
    });
  });

  describe("around content other than text", () => {
    async function capturedWith(html: string, start: number, end: number): Promise<{ editor: HTMLElement; registry: EditorRegistry; request: Parameters<typeof applyReplacement>[2] }> {
      document.body.innerHTML = `<div id="ed" contenteditable="true">${html}</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, start, end);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      if (!capture.ok) {
        throw new Error("capture failed");
      }

      const { editor: { hash }, selection } = capture.capture;
      return { editor, registry, request: { editorId: "e-1", expectedHash: hash, start, end, text: "Hi team", format: "markdown", expectedFormatHash: selection.formatHash! } };
    }

    it("refuses with draft-changed when an image was inserted between the captured words", async () => {
      const { editor, registry, request } = await capturedWith("hello world", 0, 11);
      const image = document.createElement("img");
      image.setAttribute("src", "https://example.com/logo.png");
      (editor.firstChild as Text).splitText(6);
      editor.insertBefore(image, editor.childNodes[1]!);
      let called = false;
      const exec: ExecCommand = () => (called = true);
      const result = await applyReplacement(document, registry, request, exec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
      expect(called).toBe(false);
      expect(editor.querySelector("img")).not.toBeNull();
    });

    it("refuses with unsupported-content when the selection held an image at capture", async () => {
      const { editor, registry, request } = await capturedWith(`hello <img src="https://example.com/logo.png">world`, 0, 11);
      let called = false;
      const exec: ExecCommand = () => (called = true);
      const markdown = await applyReplacement(document, registry, request, exec);
      expect(markdown).toEqual({ ok: false, error: { code: "unsupported-content", message: "The selection holds content other than text." } });
      const { format: _format, expectedFormatHash: _hash, ...plain } = request;
      const result = await applyReplacement(document, registry, plain, exec);
      expect(!result.ok && result.error.code).toBe("unsupported-content");
      expect(called).toBe(false);
      expect(editor.querySelector("img")).not.toBeNull();
    });

    it("refuses with unsupported-content for a selection inside a table", async () => {
      const { registry, request } = await capturedWith("<table><tr><td>Due date</td><td>Oct 20</td></tr></table>", 0, 15);
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(!result.ok && result.error.code).toBe("unsupported-content");
    });

    it("flags a selection holding an image at capture, and leaves the flag out otherwise", async () => {
      document.body.innerHTML = `<div id="ed" contenteditable="true">hello <img src="https://example.com/logo.png">world</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, 0, 11);
      const flagged = await captureContext(document, OTHER_SITE, new EditorRegistry());
      expect(flagged.ok && flagged.capture.selection.hasEmbeddedContent).toBe(true);
      select(editor, 0, 5);
      const plain = await captureContext(document, OTHER_SITE, new EditorRegistry());
      expect(plain.ok && "hasEmbeddedContent" in plain.capture.selection).toBe(false);
    });

    it("keeps an image with a whitespace or punctuation alt as embedded content", async () => {
      const html = `hello <img alt=" " src="https://example.com/spacer.png">there <img alt="..." src="https://example.com/dots.png">world`;
      const { editor, registry, request } = await capturedWith(html, 0, 16);
      expect(readPlainText(editor).text).toBe("hello there world");
      const capture = await captureContext(document, OTHER_SITE, registry);
      expect(capture.ok && capture.capture.selection.hasEmbeddedContent).toBe(true);
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(!result.ok && result.error.code).toBe("unsupported-content");
      expect(editor.querySelectorAll("img")).toHaveLength(2);
    });

    it("captures an emoji image as its emoji and applies over it normally", async () => {
      const html = `hello <img data-emoji="👍" alt="👍" class="an1" src="https://fonts.gstatic.com/s/e/notoemoji/1f44d/32.png"> world <img alt="🎉" src="https://example.com/party.png">`;
      const { editor, registry, request } = await capturedWith(html, 0, 17);
      expect(readPlainText(editor).text).toBe("hello 👍 world 🎉");
      const capture = await captureContext(document, OTHER_SITE, registry);
      expect(capture.ok && capture.capture.selection).toMatchObject({ text: "hello 👍 world 🎉", markdown: "hello 👍 world 🎉" });
      expect(capture.ok && "hasEmbeddedContent" in capture.capture.selection).toBe(false);
      const result = await applyReplacement(document, registry, { ...request, text: "Hi 👍 team" }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(readPlainText(editor).text).toBe("Hi 👍 team");
    });

    it.each([
      ["after", 3, 6, "abcHi team"],
      ["before", 0, 3, "Hi teamdef"],
    ])("leaves an image right %s the selection out of the range, the flag, and the edit", async (_, start, end, expected) => {
      const { editor, registry, request } = await capturedWith(`abc<img src="https://example.com/x.png">def`, start, end);
      const capture = await captureContext(document, OTHER_SITE, registry);
      expect(capture.ok && "hasEmbeddedContent" in capture.capture.selection).toBe(false);
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(readPlainText(editor).text).toBe(expected);
      expect(editor.querySelector("img")).not.toBeNull();
    });

    it("ends the range right after an emoji, leaving the image that follows it alone", async () => {
      const { editor, registry, request } = await capturedWith(`hi <img alt="👍"><img src="https://example.com/logo.png">there`, 0, 5);
      const capture = await captureContext(document, OTHER_SITE, registry);
      expect(capture.ok && capture.capture.selection.text).toBe("hi 👍");
      expect(capture.ok && "hasEmbeddedContent" in capture.capture.selection).toBe(false);
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(readPlainText(editor).text).toBe("Hi teamthere");
      expect(editor.querySelector("img")?.getAttribute("src")).toBe("https://example.com/logo.png");
    });

    it("applies next to an image outside the selection and keeps it", async () => {
      const { editor, registry, request } = await capturedWith(`hello world <img src="https://example.com/logo.png">`, 0, 5);
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelector("img")).not.toBeNull();
      expect(readPlainText(editor).text).toBe("Hi team world ");
    });
  });

  it("reports internal when the editor rejects the command", async () => {
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, () => false);
    expect(!result.ok && result.error.code).toBe("internal");
  });

  describe("with Markdown", () => {
    it("inserts HTML built from the Markdown into a rich editor", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      const commands: string[] = [];
      const exec: ExecCommand = (doc, command, value) => (commands.push(command), fakeExec(doc, command, value));
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**Robin**,\n- one\n- [two](https://example.com)", format: "markdown" as const, expectedFormatHash: formatHash };
      const result = await applyReplacement(document, registry, request, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertHTML"]);
      expect(editor.querySelector("b")?.textContent).toBe("Robin");
      expect(editor.querySelector("a")?.getAttribute("href")).toBe("https://example.com");
      expect(readPlainText(editor).text).toBe("hey Robin,\none\ntwo\napril works");
    });

    it("never turns HTML in the Markdown into elements", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      const text = `<img src=x onerror="alert(1)"><script>x</script>`;
      const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text, format: "markdown", expectedFormatHash: formatHash }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelector("img, script")).toBeNull();
      expect(readPlainText(editor).text).toBe(`hey ${text}\napril works`);
    });

    it("inserts the Markdown stripped to plain text into a textarea", async () => {
      document.body.innerHTML = `<textarea id="ta">hello there</textarea>`;
      const textarea = document.getElementById("ta") as HTMLTextAreaElement;
      textarea.focus();
      textarea.setSelectionRange(6, 11);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      const hash = capture.ok ? capture.capture.editor.hash : "";
      const commands: { command: string; value?: string }[] = [];
      const exec: ExecCommand = (_doc, command, value) => {
        commands.push({ command, value });
        textarea.setRangeText(value ?? "", textarea.selectionStart, textarea.selectionEnd, "end");
        return true;
      };
      const text = "**you**\n* [one](https://example.com)";
      const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 6, end: 11, text, format: "markdown", expectedFormatHash: "sha256:unused" }, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual([{ command: "insertText", value: "you\n- one" }]);
      expect(textarea.value).toBe("hello you\n- one");
    });

    it("rolls back and reports internal when the editor ends up with other text", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      const original = editor.innerHTML;
      const commands: string[] = [];
      const exec: ExecCommand = (_doc, command) => {
        commands.push(command);
        editor.firstElementChild!.textContent = command === "undo" ? "hey robin" : "something else";
        return true;
      };
      const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**Robin**", format: "markdown", expectedFormatHash: formatHash }, exec);
      expect(!result.ok && result.error.code).toBe("internal");
      expect(commands).toEqual(["insertHTML", "undo"]);
      expect(editor.innerHTML).toBe(original);
    });

    it("refuses with draft-changed when only the formatting changed since the capture", async () => {
      document.body.innerHTML = `<div id="ed" contenteditable="true">see <a href="https://old.example.com">the site</a> today</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, 0, 18);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      if (!capture.ok) {
        throw new Error("capture failed");
      }

      const request = {
        editorId: "e-1",
        expectedHash: capture.capture.editor.hash,
        start: 0,
        end: 18,
        text: "see [the site](https://old.example.com) **today**",
        format: "markdown" as const,
        expectedFormatHash: capture.capture.selection.formatHash!,
      };
      let called = false;
      const exec: ExecCommand = (doc, command, value) => ((called = true), fakeExec(doc, command, value));

      editor.querySelector("a")!.setAttribute("href", "https://new.example.com");
      const relinked = await applyReplacement(document, registry, request, exec);
      expect(!relinked.ok && relinked.error.code).toBe("draft-changed");

      editor.querySelector("a")!.setAttribute("href", "https://old.example.com");
      const today = editor.lastChild as Text;
      const bold = document.createElement("b");
      bold.textContent = "today";
      today.data = " ";
      editor.append(bold);
      const bolded = await applyReplacement(document, registry, request, exec);
      expect(!bolded.ok && bolded.error.code).toBe("draft-changed");
      expect(called).toBe(false);

      bold.replaceWith("today");
      const unchanged = await applyReplacement(document, registry, request, exec);
      expect(unchanged).toEqual({ ok: true });
      expect(editor.querySelector("b")?.textContent).toBe("today");
    });

    it("refuses with draft-changed when overlapping bold and italic were removed since the capture", async () => {
      document.body.innerHTML = `<div id="ed" contenteditable="true">pay <b>US</b><i><b>D</b>100</i></div>`;
      const editor = document.getElementById("ed")!;
      select(editor, 0, 10);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      if (!capture.ok) {
        throw new Error("capture failed");
      }

      expect(capture.capture.selection.markdown).toBe("pay USD100");
      editor.innerHTML = "pay USD100";
      const request = {
        editorId: "e-1",
        expectedHash: capture.capture.editor.hash,
        start: 0,
        end: 10,
        text: "pay USD100",
        format: "markdown" as const,
        expectedFormatHash: capture.capture.selection.formatHash!,
      };
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
    });

    it("reports internal when a formatting-only change leaves the editor untouched", async () => {
      const { registry, hash, formatHash } = await captured();
      const commands: string[] = [];
      const exec: ExecCommand = (_doc, command) => (commands.push(command), true);
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**robin**", format: "markdown" as const, expectedFormatHash: formatHash };
      const result = await applyReplacement(document, registry, request, exec);
      expect(!result.ok && result.error.code).toBe("internal");
      expect(commands).toEqual(["insertHTML"]);
    });

    it("rolls back and reports internal when the editor inserts the text but drops the formatting", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      const original = editor.innerHTML;
      const commands: string[] = [];
      const exec: ExecCommand = (doc, command) => {
        commands.push(command);
        if (command === "undo") {
          editor.innerHTML = original;
          return true;
        }

        return fakeExec(doc, "insertText", "Robin");
      };
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**Robin**", format: "markdown" as const, expectedFormatHash: formatHash };
      const result = await applyReplacement(document, registry, request, exec);
      expect(!result.ok && result.error.code).toBe("internal");
      expect(commands).toEqual(["insertHTML", "undo"]);
      expect(editor.innerHTML).toBe(original);
    });

    it("applies a formatting-only change the editor carries out", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**robin**", format: "markdown" as const, expectedFormatHash: formatHash };
      const result = await applyReplacement(document, registry, request, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelector("b")?.textContent).toBe("robin");
    });

    async function richCapture(html: string, start: number, end: number): Promise<{ editor: HTMLElement; registry: EditorRegistry; request: { editorId: string; expectedHash: string; start: number; end: number; format: "markdown"; expectedFormatHash: string } }> {
      document.body.innerHTML = `<div id="ed" contenteditable="true">${html}</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, start, end);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      if (!capture.ok) {
        throw new Error("capture failed");
      }

      const request = { editorId: "e-1", expectedHash: capture.capture.editor.hash, start, end, format: "markdown" as const, expectedFormatHash: capture.capture.selection.formatHash! };
      return { editor, registry, request };
    }

    it("replaces text inside a list item with the same text", async () => {
      const { editor, registry, request } = await richCapture("<ul><li>first item</li></ul>", 2, 5);
      const result = await applyReplacement(document, registry, { ...request, text: "rst" }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(readPlainText(editor).text).toBe("first item");
    });

    it("inserts a bold word inside a list item and keeps the item", async () => {
      const { editor, registry, request } = await richCapture("<ul><li>first item</li></ul>", 6, 10);
      const result = await applyReplacement(document, registry, { ...request, text: "**entry**" }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelector("li b")?.textContent).toBe("entry");
      expect(readPlainText(editor).text).toBe("first entry");
    });

    it("accepts plain text that the editor keeps bold inside an existing bold run", async () => {
      const { editor, registry, request } = await richCapture("hey <b>robin hood</b>", 5, 9);
      const result = await applyReplacement(document, registry, { ...request, text: "OBIN" }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelector("b")?.textContent).toBe("rOBIN hood");
    });

    it("refuses with draft-changed when a numbered list was renumbered since the capture", async () => {
      const { editor, registry, request } = await richCapture("<ol><li>one</li><li>two</li></ol>", 0, 7);
      editor.querySelector("ol")!.setAttribute("start", "5");
      const result = await applyReplacement(document, registry, { ...request, text: "1. one\n2. two" }, fakeExec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
    });

    it("refuses with draft-changed when two bullets were merged since the capture", async () => {
      const { editor, registry, request } = await richCapture("<ul><li>one</li><li>two</li></ul>", 0, 7);
      editor.innerHTML = "<ul><li>one<br>two</li></ul>";
      const result = await applyReplacement(document, registry, { ...request, text: "- one\n- two" }, fakeExec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
    });

    it("refuses with draft-changed when a numbered list was reversed since the capture", async () => {
      const { editor, registry, request } = await richCapture("<ol><li>one</li><li>two</li></ol>", 0, 7);
      editor.querySelector("ol")!.setAttribute("reversed", "");
      const result = await applyReplacement(document, registry, { ...request, text: "1. one\n2. two" }, fakeExec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
    });

    it("rolls back and reports internal when the editor merges the inserted items", async () => {
      const { editor, registry, request } = await richCapture("<div>one</div><div>two</div>", 0, 7);
      const original = editor.innerHTML;
      const commands: string[] = [];
      const exec: ExecCommand = (_doc, command) => {
        commands.push(command);
        editor.innerHTML = command === "undo" ? original : "<ul><li>one<br>two</li></ul>";
        return true;
      };
      const result = await applyReplacement(document, registry, { ...request, text: "- one\n- two" }, exec);
      expect(!result.ok && result.error.code).toBe("internal");
      expect(commands).toEqual(["insertHTML", "undo"]);
      expect(editor.innerHTML).toBe(original);
    });

    it("applies a list the editor inserts item by item", async () => {
      const { editor, registry, request } = await richCapture("<div>one</div><div>two</div>", 0, 7);
      const result = await applyReplacement(document, registry, { ...request, text: "- one\n- two" }, fakeExec);
      expect(result).toEqual({ ok: true });
      expect(editor.querySelectorAll("li")).toHaveLength(2);
    });

    it("applies a rewrite of a later numbered item that the editor keeps in the list", async () => {
      const { editor, registry, request } = await richCapture("<ol><li>one</li><li>two</li><li>three</li></ol>", 4, 7);
      const exec: ExecCommand = (_doc, command) => {
        if (command === "insertHTML") {
          editor.querySelectorAll("li")[1]!.textContent = "TWO";
        }

        return true;
      };
      const result = await applyReplacement(document, registry, { ...request, text: "2. TWO" }, exec);
      expect(result).toEqual({ ok: true });
    });

    it("applies a rewrite that turns two items into three and renumbers the ones after", async () => {
      const items = (texts: string[]): string => `<ol>${texts.map((t) => `<li>${t}</li>`).join("")}</ol>`;
      const { editor, registry, request } = await richCapture(items(["one", "two", "three", "four", "five"]), 4, 13);
      const commands: string[] = [];
      const exec: ExecCommand = (_doc, command) => {
        commands.push(command);
        editor.innerHTML = items(["one", "a", "b", "c", "four", "five"]);
        return true;
      };
      const result = await applyReplacement(document, registry, { ...request, text: "2. a\n3. b\n4. c" }, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertHTML"]);
    });

    it("inserts plain text into a plaintext-only editor", async () => {
      document.body.innerHTML = `<div id="ed" contenteditable="plaintext-only">hey robin</div>`;
      const editor = document.getElementById("ed")!;
      select(editor, 4, 9);
      const registry = new EditorRegistry(() => "e-1");
      const capture = await captureContext(document, OTHER_SITE, registry);
      const hash = capture.ok ? capture.capture.editor.hash : "";
      const commands: string[] = [];
      const exec: ExecCommand = (doc, command, value) => (commands.push(command), fakeExec(doc, command, value));
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**Robin**", format: "markdown" as const, expectedFormatHash: "sha256:unused" };
      const result = await applyReplacement(document, registry, request, exec);
      expect(result).toEqual({ ok: true });
      expect(commands).toEqual(["insertText"]);
      expect(editor.querySelector("b")).toBeNull();
      expect(readPlainText(editor).text).toBe("hey Robin");
    });

    it("checks the hash and the draft before editing", async () => {
      const { editor, registry, hash, formatHash } = await captured();
      let called = false;
      const exec: ExecCommand = () => (called = true);
      const request = { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "**Robin**", format: "markdown" as const, expectedFormatHash: formatHash };
      const pending = applyReplacement(document, registry, request, exec);
      editor.firstElementChild!.append(" and team");
      const edited = await pending;
      expect(!edited.ok && edited.error.code).toBe("draft-changed");
      const result = await applyReplacement(document, registry, request, exec);
      expect(!result.ok && result.error.code).toBe("draft-changed");
      expect(called).toBe(false);
    });
  });

  it("reports internal when the command claims success but nothing changed", async () => {
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 4, end: 9, text: "Robin" }, () => true);
    expect(!result.ok && result.error.code).toBe("internal");
  });
});

describe("Gmail adapter", () => {
  const GMAIL_EDITOR_ATTRIBUTES = `contenteditable="true" role="textbox" aria-label="Message Body" g_editable="true"`;

  function expandedRow(name: string, email: string, title: string, body: string): string {
    return `
      <div role="listitem"><div class="adn">
        <div class="gE"><span class="gD" email="${email}" name="${name}">${name}</span><span class="g3" title="${title}">Oct 7</span></div>
        <div class="a3s"><div>${body}</div><div class="gmail_quote" style="display:none">On Oct 5 someone wrote: quoted history</div></div>
      </div></div>`;
  }

  function collapsedRow(name: string, email: string, body: string): string {
    return `
      <div role="listitem" class="collapsed"><div class="adn">
        <div class="gE"><span class="zF" email="${email}" name="${name}">${name}</span><span class="g3" title="Tue, Oct 6, 2026, 4:30 PM">Oct 6</span></div>
        <div class="a3s" style="display:none"><div>${body}</div></div>
      </div></div>`;
  }

  /** A conversation in `div[role=main]` with a reply draft as its last row; returns the draft editor. */
  function gmailThread(rows: string, toolbar = ""): HTMLElement {
    document.body.innerHTML = `
      <div role="main">
        <div role="toolbar">${toolbar}</div>
        <h2 class="hP" data-thread-perm-id="thread-f:1">Venue for the offsite</h2>
        <div role="list">${rows}
          <div role="listitem"><div class="M9"><div id="reply" ${GMAIL_EDITOR_ATTRIBUTES}><div>hi ana</div><div>sounds good</div></div></div></div>
        </div>
      </div>`;
    return document.getElementById("reply")!;
  }

  const EXPAND_ALL = `<div role="button" aria-label="Expand all"></div>`;

  /** A Gmail adapter whose waits return at once, each advancing its clock one poll; `onTick` runs on each poll so a test can change the page. */
  function instantGmail(onTick: (tick: number) => void = () => {}): { adapters: readonly SiteAdapter[]; ticks: () => number } {
    let ticks = 0;
    const adapter = createGmailAdapter({
      pollMs: 100,
      deadlineMs: 3000,
      now: () => ticks * 100,
      sleep: async () => {
        ticks++;
        onTick(ticks);
      },
    });
    return { adapters: [adapter], ticks: () => ticks };
  }

  /** Gmail opens an inline reply inside the row of the message it answers; the editor sits beside that message's body. */
  function inlineReplyRow(name: string, email: string, title: string, body: string): string {
    return `
      <div role="listitem"><div class="adn">
        <div class="gE"><span class="gD" email="${email}" name="${name}">${name}</span><span class="g3" title="${title}">Oct 7</span></div>
        <div class="a3s"><div>${body}</div></div>
        <div class="M9"><div id="inline-reply" ${GMAIL_EDITOR_ATTRIBUTES}><div>hi sam</div><div>will do</div></div></div>
      </div></div>`;
  }

  function expandCollapsedRows(): void {
    for (const body of document.querySelectorAll<HTMLElement>(".collapsed .a3s")) {
      body.style.display = "";
    }
  }

  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("captures an expanded thread oldest first without the draft or quoted history", async () => {
    const editor = gmailThread(
      expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message")
        + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Second message"),
    );
    select(editor, 3, 6);
    const registry = new EditorRegistry(() => "e-1");
    const { adapters } = instantGmail();
    const result = await captureContext(document, GMAIL, registry, adapters);

    expect(result.ok).toBe(true);
    if (!result.ok) {
      return;
    }

    expect(result.capture).toMatchObject({
      adapter: "gmail",
      subject: "Venue for the offsite",
      editor: { id: "e-1", hash: await hashText("hi ana\nsounds good") },
      selection: { text: "ana", start: 3, end: 6 },
      thread: { completeness: "entire-thread", collapsedCount: 0 },
    });
    expect(result.capture.thread.messages).toEqual([
      { from: "Ana <ana@example.com>", sent: new Date(2026, 9, 7, 9, 12).toISOString(), body: "First message" },
      { from: "Sam <sam@example.com>", sent: new Date(2026, 9, 7, 10, 5).toISOString(), body: "Second message" },
    ]);
  });

  it("keeps the message whose row holds the inline reply", async () => {
    document.body.innerHTML = `
      <div role="main"><div role="list">
        ${expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message")}
        ${inlineReplyRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Second message")}
      </div></div>`;
    const editor = document.getElementById("inline-reply")!;
    select(editor, 3, 6);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);

    expect(result.ok && result.capture.selection.text).toBe("sam");
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "entire-thread", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages.map((m) => [m.from, m.body])).toEqual([
      ["Ana <ana@example.com>", "First message"],
      ["Sam <sam@example.com>", "Second message"],
    ]);
  });

  it("counts a collapsed message that holds the inline reply instead of dropping it", async () => {
    document.body.innerHTML = `
      <div role="main"><div role="list">
        ${expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message")}
        ${inlineReplyRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Second message")}
      </div></div>`;
    const editor = document.getElementById("inline-reply")!;
    editor.closest('[role="listitem"]')!.querySelector<HTMLElement>(".a3s")!.style.display = "none";
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);

    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 1 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message"]);
  });

  it("leaves sent out when the date does not parse and uses the address when there is no name", async () => {
    const editor = gmailThread(expandedRow("", "ana@example.com", "sometime", "Hello"));
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread.messages).toEqual([{ from: "ana@example.com", sent: undefined, body: "Hello" }]);
  });

  it("leaves sent out when the date title has no year or no time", async () => {
    const editor = gmailThread(expandedRow("Ana", "ana@example.com", "Oct 7", "Hello") + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026", "Again"));
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread.messages.map((m) => m.sent)).toEqual([undefined, undefined]);
  });

  it("caps each message body at the page text limit and reports the thread as truncated", async () => {
    const editor = gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "x".repeat(PAGE_TEXT_LIMIT + 5)));
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "truncated", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages[0]?.body.length).toBe(PAGE_TEXT_LIMIT);
  });

  it("clicks Expand all and waits for the collapsed messages to open", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message") + collapsedRow("Sam", "sam@example.com", "Second message"), EXPAND_ALL);
    let clicked = false;
    document.querySelector('[aria-label="Expand all"]')!.addEventListener("click", () => (clicked = true));
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (clicked && tick === 4) {
        expandCollapsedRows();
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(clicked).toBe(true);
    expect(timed.ticks()).toBe(4);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "entire-thread", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message", "Second message"]);
  });

  it("puts the draft back where it was after Expand all scrolls the conversation to its top", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message") + collapsedRow("Sam", "sam@example.com", "Second message"), EXPAND_ALL);
    const scroller = document.createElement("div");
    scroller.style.overflowY = "auto";
    Object.defineProperty(scroller, "scrollHeight", { value: 4000 });
    Object.defineProperty(scroller, "clientHeight", { value: 700 });
    document.body.append(scroller);
    scroller.append(document.querySelector('[role="main"]')!);
    scroller.scrollTop = 1500;
    // The draft's place in the conversation, which the opened messages push down by 600.
    let draftOffset = 2000;
    editor.getBoundingClientRect = () => ({ top: draftOffset - scroller.scrollTop }) as DOMRect;
    const heading = document.querySelector<HTMLElement>("h2")!;
    heading.tabIndex = -1;
    document.querySelector('[aria-label="Expand all"]')!.addEventListener("click", () => {
      scroller.scrollTop = 0;
      heading.focus();
      document.getSelection()!.removeAllRanges();
    });
    editor.focus();
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        draftOffset = 2600;
        expandCollapsedRows();
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(result.ok && result.capture.thread.messages.length).toBe(2);
    expect(scroller.scrollTop).toBe(2100);
    expect(document.activeElement).toBe(editor);
    expect(document.getSelection()!.toString()).toBe("hi");
  });

  it("stops moving the draft once the user edits it without a key press", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message") + collapsedRow("Sam", "sam@example.com", "Second message"), EXPAND_ALL);
    const scroller = document.createElement("div");
    scroller.style.overflowY = "auto";
    Object.defineProperty(scroller, "scrollHeight", { value: 4000 });
    Object.defineProperty(scroller, "clientHeight", { value: 700 });
    document.body.append(scroller);
    scroller.append(document.querySelector('[role="main"]')!);
    scroller.scrollTop = 1500;
    editor.getBoundingClientRect = () => ({ top: 2000 - scroller.scrollTop }) as DOMRect;
    document.querySelector('[aria-label="Expand all"]')!.addEventListener("click", () => {
      scroller.scrollTop = 0;
    });
    editor.focus();
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        editor.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertFromPaste" }));
        scroller.scrollTop = 800;
      }

      if (tick === 4) {
        expandCollapsedRows();
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(result.ok && result.capture.thread.messages.length).toBe(2);
    expect(scroller.scrollTop).toBe(800);
  });

  it("leaves focus where the user moved it while the messages are still opening", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message") + collapsedRow("Sam", "sam@example.com", "Second message"), EXPAND_ALL);
    const search = document.createElement("input");
    document.body.append(search);
    const heading = document.querySelector<HTMLElement>("h2")!;
    heading.tabIndex = -1;
    document.querySelector('[aria-label="Expand all"]')!.addEventListener("click", () => heading.focus());
    editor.focus();
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        search.focus();
        document.getSelection()!.removeAllRanges();
      }

      if (tick === 4) {
        expandCollapsedRows();
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(result.ok && result.capture.thread.messages.length).toBe(2);
    expect(document.activeElement).toBe(search);
    expect(document.getSelection()!.rangeCount).toBe(0);
  });

  it("keeps waiting when the older-messages bundle vanishes a poll before its rows arrive", async () => {
    const editor = gmailThread(
      `<div id="bundle"><span role="button" aria-label="2 older messages">2</span></div>` + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Third message"),
      EXPAND_ALL,
    );
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        document.getElementById("bundle")!.remove();
      }

      if (tick === 3) {
        document.querySelector('[role="list"]')!.insertAdjacentHTML(
          "afterbegin",
          expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message") + expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:30 AM", "Second message"),
        );
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(timed.ticks()).toBe(3);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "entire-thread", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message", "Second message", "Third message"]);
  });

  it("counts the bundle's messages as hidden when they never arrive", async () => {
    const editor = gmailThread(
      `<div id="bundle"><span role="button" aria-label="2 older messages">2</span></div>` + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Third message"),
      EXPAND_ALL,
    );
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        document.getElementById("bundle")!.remove();
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(timed.ticks()).toBe(30);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 2 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["Third message"]);
  });

  it("counts a bundle that stays on the page once", async () => {
    const editor = gmailThread(
      `<span role="button" aria-label="2 older messages">2</span>` + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 10:05 AM", "Third message"),
      EXPAND_ALL,
    );
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 2 });
  });

  it("reports truncated over loaded-messages-only and keeps the hidden count", async () => {
    const editor = gmailThread(
      expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "x".repeat(PAGE_TEXT_LIMIT + 1)) + collapsedRow("Sam", "sam@example.com", "Second message"),
    );
    select(editor, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "truncated", collapsedCount: 1 });
  });

  it("refuses the capture when the draft editor leaves the page during the wait", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message"), EXPAND_ALL);
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        document.querySelector('[role="main"]')!.innerHTML = `<h2 class="hP">Another conversation</h2><div role="listitem"><div class="a3s">Other message</div></div>`;
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);
    expect(result).toEqual({ ok: false, error: { code: "no-editor", message: "The draft editor left the page while the thread was being read." } });
  });

  it("never reports the whole thread on a page in a language it has no labels for", async () => {
    const editor = gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message"));
    document.documentElement.lang = "de";
    select(editor, 0, 2);
    try {
      const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
      expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 0 });
      expect(result.ok && result.capture.thread.messages).toHaveLength(1);
    } finally {
      document.documentElement.lang = "";
    }
  });

  it("stops waiting once the thread stops changing after Expand all", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message") + collapsedRow("Sam", "sam@example.com", "Second message"), EXPAND_ALL);
    select(editor, 0, 2);
    const timed = instantGmail((tick) => {
      if (tick === 2) {
        document.querySelector<HTMLElement>(".collapsed .a3s")!.style.display = "";
      }
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(timed.ticks()).toBe(5);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 1 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message"]);
  });

  it("gives up at the deadline when Expand all opens nothing", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message"), EXPAND_ALL);
    select(editor, 0, 2);
    const timed = instantGmail();
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(timed.ticks()).toBe(30);
    expect(result.ok && result.capture.thread).toEqual({ completeness: "loaded-messages-only", collapsedCount: 1, messages: [] });
  });

  it("measures the deadline on the clock, so a throttled tab stops after a few stretched waits", async () => {
    const editor = gmailThread(collapsedRow("Ana", "ana@example.com", "First message"), EXPAND_ALL);
    select(editor, 0, 2);
    let ticks = 0;
    const throttled = createGmailAdapter({
      pollMs: 150,
      deadlineMs: 3000,
      now: () => ticks * 1000,
      sleep: async () => {
        ticks++;
      },
    });
    const result = await captureContext(document, GMAIL, new EditorRegistry(), [throttled]);

    expect(ticks).toBe(3);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 1 });
  });

  it("does not click Expand all when nothing is collapsed", async () => {
    const editor = gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message"), EXPAND_ALL);
    let clicked = false;
    document.querySelector('[aria-label="Expand all"]')!.addEventListener("click", () => (clicked = true));
    select(editor, 0, 2);
    const timed = instantGmail();
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(clicked).toBe(false);
    expect(timed.ticks()).toBe(0);
    expect(result.ok && result.capture.thread.completeness).toBe("entire-thread");
  });

  it("counts collapsed rows and the older-messages bundle when Expand all is absent", async () => {
    const editor = gmailThread(
      `<div><span role="button" aria-label="2 older messages">2</span></div>`
        + collapsedRow("Ana", "ana@example.com", "First message")
        + expandedRow("Sam", "sam@example.com", "Wed, Oct 7, 2026, 9:12 AM", "Latest message"),
    );
    select(editor, 0, 2);
    const timed = instantGmail();
    const result = await captureContext(document, GMAIL, new EditorRegistry(), timed.adapters);

    expect(timed.ticks()).toBe(0);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 3 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["Latest message"]);
  });

  it("captures the draft that holds the selection when two are open", async () => {
    const reply = gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message"));
    document.body.insertAdjacentHTML("beforeend", `<div role="dialog"><div id="compose" ${GMAIL_EDITOR_ATTRIBUTES}>new note</div></div>`);
    const compose = document.getElementById("compose")!;
    const registry = new EditorRegistry();
    const { adapters } = instantGmail();

    select(compose, 4, 8);
    const fromCompose = await captureContext(document, GMAIL, registry, adapters);
    expect(fromCompose.ok && fromCompose.capture.selection.text).toBe("note");
    expect(fromCompose.ok && registry.find(fromCompose.capture.editor.id)).toBe(compose);

    select(reply, 7, 13);
    const fromReply = await captureContext(document, GMAIL, registry, adapters);
    expect(fromReply.ok && fromReply.capture.selection.text).toBe("sounds");
    expect(fromReply.ok && registry.find(fromReply.capture.editor.id)).toBe(reply);
  });

  it("reports no-selection when a draft is open but nothing in it is selected", async () => {
    gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message"));
    document.getSelection()!.selectAllChildren(document.querySelector("h2")!);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(!result.ok && result.error.code).toBe("no-selection");
  });

  it("reports no-editor when no draft is open", async () => {
    document.body.innerHTML = `<div role="main"><p>Inbox</p></div>`;
    document.getSelection()!.selectAllChildren(document.querySelector("p")!);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(!result.ok && result.error.code).toBe("no-editor");
  });

  it("reports draft-only for a new message outside the conversation", async () => {
    document.body.innerHTML = `
      <div role="main"><div role="list">${expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "Unrelated")}</div></div>
      <div role="dialog"><div id="compose" ${GMAIL_EDITOR_ATTRIBUTES}>new note</div></div>`;
    select(document.getElementById("compose")!, 0, 3);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread).toEqual({ completeness: "draft-only", collapsedCount: 0, messages: [] });
  });

  it("falls back to visible text when the conversation no longer has the expected structure", async () => {
    document.title = "Venue - Gmail";
    document.body.innerHTML = `
      <div role="main">
        <div role="listitem"><div class="renamed"><span>Ana</span><div class="renamed-body">First message</div></div></div>
        <div role="listitem"><div id="reply" ${GMAIL_EDITOR_ATTRIBUTES}>hi ana</div></div>
      </div>`;
    select(document.getElementById("reply")!, 0, 2);
    try {
      const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
      expect(result.ok && result.capture.adapter).toBe("gmail");
      expect(result.ok && result.capture.subject).toBe("Venue - Gmail");
      expect(result.ok && result.capture.thread).toEqual({
        completeness: "visible-text-only",
        collapsedCount: 0,
        messages: [{ from: document.location.hostname, body: "Ana\nFirst message" }],
      });
    } finally {
      document.title = "";
    }
  });

  it("falls back to visible text when rows keep their senders but the body class is gone", async () => {
    document.body.innerHTML = `
      <div role="main">
        <div role="listitem"><div class="gE"><span class="gD" email="ana@example.com">Ana</span><span class="g3" title="Wed, Oct 7, 2026, 9:12 AM">Oct 7</span></div><div class="renamed-body">First message</div></div>
        <div role="listitem"><div id="reply" ${GMAIL_EDITOR_ATTRIBUTES}>hi ana</div></div>
      </div>`;
    select(document.getElementById("reply")!, 0, 2);
    const result = await captureContext(document, GMAIL, new EditorRegistry(), instantGmail().adapters);
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "visible-text-only", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["AnaOct 7\nFirst message"]);
  });

  it("applies a rewrite into the reply draft, leaving its hidden quoted text alone", async () => {
    const editor = gmailThread(expandedRow("Ana", "ana@example.com", "Wed, Oct 7, 2026, 9:12 AM", "First message"));
    editor.insertAdjacentHTML("beforeend", `<div class="gmail_signature">Me</div><div class="gmail_quote" style="display:none">quoted</div>`);
    select(editor, 3, 6);
    const registry = new EditorRegistry(() => "e-1");
    const captured = await captureContext(document, GMAIL, registry, instantGmail().adapters);
    const hash = captured.ok ? captured.capture.editor.hash : "";
    expect(hash).toBe(await hashText("hi ana\nsounds good\nMe"));

    document.getSelection()!.removeAllRanges();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 3, end: 6, text: "Ana" }, fakeExec);
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hi Ana\nsounds good\nMe");
    expect(editor.querySelector(".gmail_quote")!.textContent).toBe("quoted");
  });
});

describe("Outlook adapter", () => {
  const OUTLOOK: TestLocation = {
    protocol: "https:",
    hostname: "outlook.office.com",
    pathname: "/mail/inbox/id/AAQk",
    origin: "https://outlook.office.com",
  };

  const EDITOR = `<div id="editorParent_1"><div id="reply" contenteditable="true" role="textbox" aria-multiline="true" aria-label="Message body"><div>hi ana</div><div>sounds good</div></div></div>`;

  interface Message {
    id: string;
    from: string;
    to?: string;
    date: string;
    body: string;
  }

  /** A received or sent message as Outlook draws it expanded, with its reply toolbar and recipient line; `inside` lands after the body. */
  function expanded(message: Message, inside = ""): string {
    return `
      <div data-itempart-id="${message.id}"><div aria-expanded="true">
        <span id="MSG_${message.id}_SUBJ" role="heading" aria-labelledby="CONV_1_SUBJECT"></span>
        <div aria-label="Email message">
          <span id="MSG_${message.id}_FROM" role="heading">${message.from}</span>
          <div role="toolbar"><div role="menuitem" aria-label="Reply"></div></div>
          <div data-testid="RecipientWell"><div id="MSG_${message.id}_TO">${message.to ? `<span class="_Entity _EType_RECIPIENT_ENTITY"><span aria-label="${message.to}">Me</span></span>` : ""}</div></div>
          <div data-testid="SentReceivedSavedTime">${message.date}</div>
          <div data-test-id="mailMessageBodyContainer"><div id="UniqueMessageBody_${message.id}" role="document" aria-label="Message body"><div>${message.body}</div></div></div>
        </div>
        ${inside}
      </div></div>`;
  }

  /** A collapsed message shows only its sender, a preview, and its date. */
  function collapsed(message: Message): string {
    return `
      <div data-itempart-id="${message.id}"><div aria-expanded="false"><div>
        <span id="MSG_${message.id}_FROM" role="heading">${message.from}</span>
        <div>${message.body.slice(0, 10)}</div>
        <div data-testid="SentReceivedSavedTime">${message.date}</div>
      </div></div></div>`;
  }

  /** A saved draft is drawn expanded, with no reply toolbar and no recipient line. */
  function savedDraft(id: string): string {
    return `
      <div data-itempart-id="${id}"><div aria-expanded="true">
        <span id="MSG_${id}_FROM" role="heading">[Draft]</span>
        <div data-testid="SentReceivedSavedTime">Saved: Wed 10/28/2026 3:13 PM</div>
        <div id="UniqueMessageBody_${id}" role="document"><div>an older unsent reply</div></div>
      </div></div>`;
  }

  /** The reading pane of a conversation, items in the order given, with the subject the items point at. */
  function conversation(label: string, items: string): void {
    document.body.innerHTML = `
      <div role="main" aria-label="Reading Pane">
        <span id="CONV_1_SUBJECT">Venue for the offsite</span>
        <div data-app-section="ConversationContainer" aria-label="${label}"><div>
          <div data-itempart-id="placeholder"></div>
          ${items}
        </div></div>
      </div>`;
  }

  const ANA: Message = { id: "a", from: "Ana Lopez", to: "Me <me@example.com>", date: "Wed 10/28/2026 9:12 AM", body: "First message" };
  const SAM: Message = { id: "b", from: "Sam Lee", date: "Wed 10/28/2026 10:05 AM", body: "Second message" };
  const KIM: Message = { id: "c", from: "Kim Park", date: "Wed 10/28/2026 3:03 PM", body: "Third message" };

  async function capture(registry = new EditorRegistry(() => "e-1")): Promise<CaptureResult> {
    const editor = document.getElementById("reply")!;
    select(editor, 3, 6);
    return captureContext(document, OUTLOOK, registry, [outlookAdapter]);
  }

  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("is picked on every Outlook on the web host and nowhere else", () => {
    for (const hostname of ["outlook.live.com", "outlook.office.com", "outlook.office365.com", "outlook.cloud.microsoft"]) {
      expect(selectAdapter({ ...OUTLOOK, hostname })?.id).toBe("outlook");
    }

    expect(selectAdapter({ ...OUTLOOK, protocol: "http:" })?.id).toBe("generic");
    expect(selectAdapter({ ...OUTLOOK, hostname: "outlook.example.com" })?.id).toBe("generic");
  });

  it("reads a newest-first conversation oldest first, skipping a saved draft and counting a collapsed message", async () => {
    conversation("5 messages", savedDraft("d") + expanded(KIM, EDITOR) + collapsed(SAM) + expanded(ANA));
    const result = await capture();

    expect(result.ok && result.capture).toMatchObject({
      adapter: "outlook",
      subject: "Venue for the offsite",
      selection: { text: "ana", start: 3, end: 6 },
      thread: { completeness: "loaded-messages-only", collapsedCount: 1 },
    });
    expect(result.ok && result.capture.thread.messages).toEqual([
      { from: "Ana Lopez", to: ["Me <me@example.com>"], sent: new Date(2026, 9, 28, 9, 12).toISOString(), body: "First message" },
      { from: "Kim Park", sent: new Date(2026, 9, 28, 15, 3).toISOString(), body: "Third message" },
    ]);
  });

  it("reports the entire thread when every message is open, even with the reply counted in the label", async () => {
    conversation("4 messages", expanded(ANA) + expanded(SAM) + expanded(KIM, EDITOR));
    const result = await capture();

    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "entire-thread", collapsedCount: 0 });
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message", "Second message", "Third message"]);
  });

  it("keeps an oldest-first conversation in order when its first two messages share a minute", async () => {
    conversation("3 messages", expanded({ ...ANA, date: SAM.date }) + expanded(SAM) + expanded(KIM, EDITOR));
    const result = await capture();
    expect(result.ok && result.capture.thread.messages.map((m) => m.body)).toEqual(["First message", "Second message", "Third message"]);
  });

  it("counts messages the label promises but the page has not drawn", async () => {
    conversation("6 messages", expanded(KIM, EDITOR) + expanded(SAM));
    const result = await capture();
    expect(result.ok && result.capture.thread).toMatchObject({ completeness: "loaded-messages-only", collapsedCount: 3 });
  });

  it("reads day-first dates when a day past 12 proves the order", async () => {
    conversation(
      "2 messages",
      expanded({ ...KIM, date: "Wed 28/10/2026 15:03" }, EDITOR) + expanded({ ...ANA, date: "Mon 05/10/2026 09:12" }),
    );
    const result = await capture();
    expect(result.ok && result.capture.thread.messages.map((m) => m.sent)).toEqual([
      new Date(2026, 9, 5, 9, 12).toISOString(),
      new Date(2026, 9, 28, 15, 3).toISOString(),
    ]);
  });

  it("leaves sent out when a date has no year or no time", async () => {
    conversation("2 messages", expanded({ ...KIM, date: "Wed 3:03 PM" }, EDITOR) + expanded({ ...ANA, date: "Wed 10/28/2026" }));
    const result = await capture();
    expect(result.ok && result.capture.thread.messages.map((m) => m.sent)).toEqual([undefined, undefined]);
  });

  it("falls back to visible text when items remain but the body hook is gone", async () => {
    conversation("1 message", expanded(KIM, EDITOR).replace(/UniqueMessageBody/g, "RenamedBody"));
    const result = await capture();
    expect(result.ok && result.capture.thread.completeness).toBe("visible-text-only");
  });

  it("reads the quoted original under a reply composed outside the conversation", async () => {
    document.body.innerHTML = `
      <div role="main">
        <div>${EDITOR}</div>
        <div class="quoted">
          <a name="_MailOriginal"></a>
          <div id="divRplyFwdMsg"><div><b>From:</b> Ana Lopez &lt;ana@example.com&gt;<br><b>Sent:</b> Wednesday, October 28, 2026 3:00 PM<br><b>To:</b> Me &lt;me@example.com&gt;<br><b>Subject:</b> Venue for the offsite</div></div>
          <div>Shall we book the hall?</div>
          <blockquote><div>On Tue, Sam wrote:</div><div>Any venue ideas?</div></blockquote>
        </div>
      </div>`;
    const result = await capture();

    expect(result.ok && result.capture).toMatchObject({
      subject: "Venue for the offsite",
      editor: { hash: await hashText("hi ana\nsounds good") },
      thread: { completeness: "loaded-messages-only", collapsedCount: 0 },
    });
    expect(result.ok && result.capture.thread.messages).toEqual([
      {
        from: "Ana Lopez <ana@example.com>",
        sent: new Date(2026, 9, 28, 15, 0).toISOString(),
        body: "Shall we book the hall?\n\nOn Tue, Sam wrote:\nAny venue ideas?",
      },
    ]);
  });

  it("captures the draft alone in a new message", async () => {
    document.body.innerHTML = `<div role="main"><div id="To" contenteditable="true" aria-label="To">ana@example.com</div>${EDITOR}</div>`;
    const result = await capture();
    expect(result.ok && result.capture.thread).toEqual({ completeness: "draft-only", collapsedCount: 0, messages: [] });
  });

  /** Does what Outlook's editor does with a key press over a selection: deletes it and selects a zero-width space in its place. */
  const outlookKeyPress = (): void => {
    const range = document.getSelection()!.getRangeAt(0);
    range.deleteContents();
    const placeholder = document.createTextNode("\u200b");
    range.insertNode(placeholder);
    const selection = document.getSelection()!;
    const held = document.createRange();
    held.selectNodeContents(placeholder);
    selection.removeAllRanges();
    selection.addRange(held);
  };

  async function captured(): Promise<{ registry: EditorRegistry; hash: string }> {
    const registry = new EditorRegistry(() => "e-1");
    const result = await capture(registry);
    return { registry, hash: result.ok ? result.capture.editor.hash : "" };
  }

  it("applies over the placeholder the editor leaves after recording an undo point", async () => {
    document.body.innerHTML = EDITOR;
    const editor = document.getElementById("reply")!;
    const { registry, hash } = await captured();
    const recorded: HTMLElement[] = [];
    const record = (target: HTMLElement): void => {
      recorded.push(target);
      outlookKeyPress();
    };

    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 3, end: 6, text: "Ana" }, fakeExec, record);

    expect(result).toEqual({ ok: true });
    expect(recorded).toEqual([editor]);
    expect(readPlainText(editor).text).toBe("hi Ana\nsounds good");
  });

  it("puts the selected text back over the placeholder when the editor rejects the edit", async () => {
    document.body.innerHTML = EDITOR;
    const editor = document.getElementById("reply")!;
    const { registry, hash } = await captured();
    const rejectRewrite: ExecCommand = (doc, command, value) => value !== "Ana" && fakeExec(doc, command, value);

    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 3, end: 6, text: "Ana" }, rejectRewrite, outlookKeyPress);
    expect(result).toMatchObject({ ok: false, error: { code: "internal" } });
    expect(readPlainText(editor).text).toBe("hi ana\nsounds good");
  });

  it("applies as usual when the editor ignores the key press", async () => {
    document.body.innerHTML = EDITOR;
    const editor = document.getElementById("reply")!;
    const { registry, hash } = await captured();
    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 3, end: 6, text: "Ana" }, fakeExec, () => {});
    expect(result).toEqual({ ok: true });
    expect(readPlainText(editor).text).toBe("hi Ana\nsounds good");
  });

  it("stops before the edit when the key press changes the draft some other way", async () => {
    document.body.innerHTML = EDITOR;
    const editor = document.getElementById("reply")!;
    const { registry, hash } = await captured();
    const typed = (target: HTMLElement): void => {
      target.firstElementChild!.append("a");
    };

    const result = await applyReplacement(document, registry, { editorId: "e-1", expectedHash: hash, start: 3, end: 6, text: "Ana" }, fakeExec, typed);
    expect(result).toMatchObject({ ok: false, error: { code: "internal" } });
    expect(readPlainText(editor).text).toBe("hi anaa\nsounds good");
  });

  it("makes the editor record an undo point with a key press over the selection", () => {
    document.body.innerHTML = EDITOR;
    const editor = document.getElementById("reply")!;
    const keys: string[] = [];
    editor.addEventListener("keydown", (event) => keys.push(`down ${event.key}`));
    editor.addEventListener("keyup", (event) => keys.push(`up ${event.key}`));
    outlookAdapter.recordUndoPoint!(editor);
    expect(keys).toEqual(["down a", "up a"]);
  });

  it("finds the message editor rather than the recipient field", () => {
    document.body.innerHTML = `<div id="To" contenteditable="true" aria-label="To">ana@example.com</div>${EDITOR}`;
    select(document.getElementById("To")!, 0, 3);
    expect(outlookAdapter.findEditor(document)?.id).toBe("reply");
  });
});
