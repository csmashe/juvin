// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from "vitest";
import { offsetsToRange, pointToOffset, readPlainText } from "../src/content/plain-text";

function editor(html: string): HTMLElement {
  document.body.innerHTML = `<div id="ed" contenteditable="true">${html}</div>`;
  return document.getElementById("ed")!;
}

// Gmail-style draft: one div per line, an empty line is <div><br></div>.
const DRAFT = "<div>Hi Ana,</div><div><br></div><div>Thanks for the <b>quick</b> reply.</div><div>Best,<br>Sam</div>";
const DRAFT_TEXT = "Hi Ana,\n\nThanks for the quick reply.\nBest,\nSam";

describe("readPlainText", () => {
  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("turns blocks and line breaks into newlines like innerText", () => {
    expect(readPlainText(editor(DRAFT)).text).toBe(DRAFT_TEXT);
  });

  it("ends a block with a newline when text follows it", () => {
    expect(readPlainText(editor("a<div>b</div>c")).text).toBe("a\nb\nc");
    expect(readPlainText(editor("<div>a</div><div>b</div>")).text).toBe("a\nb");
    expect(readPlainText(editor("<p>a</p>b<br>c")).text).toBe("a\nb\nc");
  });

  it("leaves out an excluded subtree", () => {
    const root = editor("<p>before</p><div id=\"x\">draft</div><p>after</p>");
    expect(readPlainText(root, document.getElementById("x")!).text).toBe("before\nafter");
  });

  it("reads plain text without blocks as is", () => {
    expect(readPlainText(editor("one<br>two")).text).toBe("one\ntwo");
  });

  it("skips hidden elements and scripts", () => {
    expect(readPlainText(editor(`a<span hidden>secret</span><span style="display:none">x</span><script>1</script>b`)).text).toBe("ab");
  });

  it("treats a block holding only a <br> as one empty line", () => {
    expect(readPlainText(editor("Hi<div><br></div>")).text).toBe("Hi\n");
    expect(readPlainText(editor("<div>a</div><div><br></div><div>b</div>")).text).toBe("a\n\nb");
    expect(readPlainText(editor("<div><br></div>")).text).toBe("");
    expect(readPlainText(editor("<div><br></div><div>a</div>")).text).toBe("\na");
  });

  it("uses the computed display to find line boundaries", () => {
    expect(readPlainText(editor(`a<span style="display:block">b</span>c`)).text).toBe("a\nb\nc");
    expect(readPlainText(editor(`<div style="display:inline">a</div><div style="display:inline">b</div>`)).text).toBe("ab");
  });

  it("reads nothing from a root that is not rendered", () => {
    const root = editor("hidden");
    root.style.display = "none";
    expect(readPlainText(root).text).toBe("");
  });

  it("skips text that visibility hides, like innerText", () => {
    expect(readPlainText(editor(`a<span style="visibility:hidden">x<i style="visibility:visible">y</i></span>b`)).text).toBe("ayb");
  });
});

describe("offsets and ranges", () => {
  it("round-trips every offset pair through a Range", () => {
    const root = editor(DRAFT);
    const plain = readPlainText(root);
    for (let start = 0; start <= plain.text.length; start++) {
      for (let end = start; end <= plain.text.length; end++) {
        const range = offsetsToRange(root, plain, start, end);
        expect(pointToOffset(plain, range.startContainer, range.startOffset)).toBe(start);
        expect(pointToOffset(plain, range.endContainer, range.endOffset)).toBe(end);
      }
    }
  });

  it("selects the expected text", () => {
    const root = editor(DRAFT);
    const plain = readPlainText(root);
    const start = DRAFT_TEXT.indexOf("quick");
    const range = offsetsToRange(root, plain, start, start + "quick reply".length);
    expect(range.toString()).toBe("quick reply");
  });

  it("maps element-level points, as a select-all produces", () => {
    const root = editor(DRAFT);
    const plain = readPlainText(root);
    expect(pointToOffset(plain, root, 0)).toBe(0);
    expect(pointToOffset(plain, root, root.childNodes.length)).toBe(DRAFT_TEXT.length);
    const secondLine = root.childNodes[2]!;
    expect(pointToOffset(plain, secondLine, 0)).toBe(DRAFT_TEXT.indexOf("Thanks"));
  });
});
