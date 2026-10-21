// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { formatSignature } from "../src/content/formatting";
import { markdownToElement, selectionMarkdown, stripMarkdown } from "../src/content/markdown";
import { readPlainText } from "../src/content/plain-text";

function editor(html: string): HTMLElement {
  document.body.innerHTML = `<div id="ed" contenteditable="true">${html}</div>`;
  return document.getElementById("ed")!;
}

function whole(html: string): string {
  const root = editor(html);
  return selectionMarkdown(root, 0, readPlainText(root).text.length);
}

function html(markdown: string): string {
  return markdownToElement(document, markdown).innerHTML;
}

describe("selectionMarkdown", () => {
  it("marks bold and italic from tags and from computed style", () => {
    expect(whole("a <b>bold</b> and <i>it</i> <strong>s</strong> <em>e</em>")).toBe("a **bold** and *it* **s** *e*");
    expect(whole(`<span style="font-weight: 700">w</span> <span style="font-style: italic">x</span>`)).toBe("**w** *x*");
    expect(whole(`<b style="font-weight: normal">not bold</b>`)).toBe("not bold");
  });

  it("nests bold and italic and keeps markers off the surrounding spaces", () => {
    expect(whole("<b>all <i>both</i> of it</b>")).toBe("**all *both* of it**");
    expect(whole("<b><i>both</i></b>")).toBe("***both***");
    expect(whole("<b>hello </b>world")).toBe("**hello** world");
  });

  it("follows a Gmail div per line, with empty lines", () => {
    expect(whole("<div>Hi <b>Ana</b>,</div><div><br></div><div>Thanks</div>")).toBe("Hi **Ana**,\n\nThanks");
  });

  it("writes bulleted and numbered lists", () => {
    expect(whole("<div>Items:</div><ul><li>one</li><li><b>two</b></li></ul><ol><li>first</li><li>second</li></ol><div>end</div>")).toBe(
      "Items:\n- one\n- **two**\n1. first\n2. second\nend",
    );
  });

  it("indents nested list items two spaces per level", () => {
    expect(whole("<ul><li>one<ul><li>two<ol><li>deep</li></ol></li></ul></li><li>three</li></ul>")).toBe("- one\n  - two\n    1. deep\n- three");
  });

  it("keeps the cells of a table row apart with a tab", () => {
    expect(whole("<table><tr><td><b>Due</b></td><td>Oct 20</td></tr></table>")).toBe("**Due**\tOct 20");
  });

  it("gives no list marker to a line that starts inside a list item", () => {
    const root = editor("<ul><li>first</li><li>second</li></ul>");
    expect(selectionMarkdown(root, 2, 12)).toBe("rst\n- second");
  });

  it("keeps links with an allowed scheme and drops others to their text", () => {
    expect(whole(`see <a href="https://example.com/a(b)">the site</a>`)).toBe("see [the site](https://example.com/a%28b%29)");
    expect(whole(`<a href="mailto:a@example.com">mail</a>`)).toBe("[mail](mailto:a@example.com)");
    expect(whole(`<a href="javascript:alert(1)">click</a>`)).toBe("click");
  });

  it("escapes literal Markdown characters and list markers", () => {
    expect(whole("5 * 3 = snake_case [x]")).toBe("5 \\* 3 = snake\\_case \\[x\\]");
    expect(whole("<div>- not a list</div><div>1. nor this</div>")).toBe("\\- not a list\n1\\. nor this");
  });

  it("formats only the selected part of a bold run", () => {
    const root = editor("hey <b>robin</b> there");
    expect(selectionMarkdown(root, 6, 12)).toBe("**bin** th");
  });

  it("round-trips through the parser to the same text", () => {
    const markdown = whole("<div>5 * 3 <b>bold</b> snake_case \\*</div><ul><li>item</li></ul>");
    expect(stripMarkdown(markdown)).toBe("5 * 3 bold snake_case \\*\n- item");
  });
});

// Each input takes well under a second locally; a quadratic pass over it takes tens of
// seconds, so the budget leaves room for slow CI runners and still catches that.
const LINEAR_TIME_BUDGET_MS = 5000;

describe("apply performance", () => {
  it.each([
    ["unmatched brackets", "[".repeat(120_000) + "x](https://example.com)"],
    ["unmatched closing markers", "a* b_ ".repeat(40_000)],
    ["unmatched closing and opening markers", "a* *b _c b_ ".repeat(20_000)],
  ])("renders and strips a line of %s in linear time", (_, markdown) => {
    const started = performance.now();
    const container = markdownToElement(document, markdown);
    const stripped = stripMarkdown(markdown);
    expect(performance.now() - started).toBeLessThan(LINEAR_TIME_BUDGET_MS);
    expect(stripped.length).toBeGreaterThan(0);
    expect(container.textContent!.length).toBeGreaterThan(0);
  });
});

describe("deep nesting", () => {
  it("keeps emphasis nested past the limit as literal markers instead of overflowing the stack", () => {
    const markdown = "*".repeat(10_000) + "x" + "*".repeat(10_000);
    const container = markdownToElement(document, markdown);
    expect(container.querySelectorAll("b")).toHaveLength(8);
    expect(container.textContent).toBe("*".repeat(9_984) + "x" + "*".repeat(9_984));
    expect(stripMarkdown(markdown)).toBe("*".repeat(9_984) + "x" + "*".repeat(9_984));
  });

  it("renders and strips a long ordinary text", () => {
    const markdown = "Thanks for the **update**, see you *soon*. ".repeat(20_000);
    const container = markdownToElement(document, markdown);
    expect(container.querySelectorAll("b")).toHaveLength(20_000);
    expect(container.querySelectorAll("i")).toHaveLength(20_000);
    expect(stripMarkdown(markdown)).toBe("Thanks for the update, see you soon. ".repeat(20_000));
  });
});

describe("nested lists", () => {
  it("captures and rebuilds a nested list with its formatting", () => {
    const source = `<ul><li>one<ul><li>two</li><li>three<ol start="4"><li>four</li></ol></li></ul></li><li>five</li></ul>`;
    const root = editor(source);
    const length = readPlainText(root).text.length;
    const markdown = selectionMarkdown(root, 0, length);
    expect(markdown).toBe("- one\n  - two\n  - three\n    4. four\n- five");
    const container = markdownToElement(document, markdown);
    expect(container.innerHTML).toBe(source);
    expect(formatSignature(container, 0, length)).toBe(formatSignature(root, 0, length));
  });

  it("nests by indentation of any width and goes back to the level an item reaches", () => {
    expect(html("- a\n    - b\n        1. c\n    - d\n- e")).toBe("<ul><li>a<ul><li>b<ol><li>c</li></ol></li><li>d</li></ul></li><li>e</li></ul>");
    expect(html("1. a\n   - b\n   2. c\n2. d")).toBe(`<ol><li>a<ul><li>b</li></ul><ol start="2"><li>c</li></ol></li><li>d</li></ol>`);
  });

  it("starts a top-level list at whatever indent its first item has", () => {
    expect(html("  - a\n- b\n1. c")).toBe("<ul><li>a</li><li>b</li></ul><ol><li>c</li></ol>");
  });

  it("nests under an item that dedents below the list's first item", () => {
    expect(html("  - a\n- b\n  - c")).toBe("<ul><li>a</li><li>b<ul><li>c</li></ul></li></ul>");
    expect(stripMarkdown("  - a\n- b\n  - c")).toBe("- a\n- b\n  - c");
  });

  it("puts items deeper than the limit in the deepest list", () => {
    const markdown = Array.from({ length: 12 }, (_, i) => `${"  ".repeat(i)}- ${i}`).join("\n");
    const container = markdownToElement(document, markdown);
    expect(container.querySelectorAll("ul")).toHaveLength(8);
    expect(container.querySelectorAll("li")).toHaveLength(12);
  });

  it("keeps the nesting when stripped", () => {
    expect(stripMarkdown("- a\n    - **b**\n        1. c")).toBe("- a\n  - b\n    1. c");
  });
});

describe("list numbering", () => {
  it("captures and rebuilds explicit item numbers", () => {
    const root = editor(`<ol><li value="7">one</li><li>two</li><li value="20">three</li></ol>`);
    const length = readPlainText(root).text.length;
    const markdown = selectionMarkdown(root, 0, length);
    expect(markdown).toBe("7. one\n8. two\n20. three");
    const container = markdownToElement(document, markdown);
    expect(container.innerHTML).toBe(`<ol start="7"><li>one</li><li>two</li><li value="20">three</li></ol>`);
    expect(formatSignature(container, 0, length)).toBe(formatSignature(root, 0, length));
  });

  it("numbers a reversed list down from its item count, its start, or an item's value", () => {
    expect(whole(`<ol reversed><li>a</li><li>b</li><li>c</li></ol>`)).toBe("3. a\n2. b\n1. c");
    expect(whole(`<ol reversed start="10"><li>a</li><li>b</li></ol>`)).toBe("10. a\n9. b");
    expect(whole(`<ol reversed><li>a</li><li value="5">b</li><li>c</li></ol>`)).toBe("3. a\n5. b\n4. c");
    const container = markdownToElement(document, "3. a\n2. b\n1. c");
    const root = editor(`<ol reversed><li>a</li><li>b</li><li>c</li></ol>`);
    expect(formatSignature(container, 0, 5)).toBe(formatSignature(root, 0, 5));
  });

  it("round-trips a reversed list that counts below one", () => {
    const root = editor(`<ol reversed start="1"><li>a</li><li>b</li><li>c</li></ol>`);
    const markdown = selectionMarkdown(root, 0, 5);
    expect(markdown).toBe("1. a\n0. b\n-1. c");
    const container = markdownToElement(document, markdown);
    expect(container.innerHTML).toBe(`<ol><li>a</li><li value="0">b</li><li value="-1">c</li></ol>`);
    expect(formatSignature(container, 0, 5)).toBe(formatSignature(root, 0, 5));
    expect(stripMarkdown(markdown)).toBe(markdown);
  });

  it("escapes a plain line that starts like a negative number item", () => {
    const markdown = whole("<div>-1. not a list</div>");
    expect(markdown).toBe("-1\\. not a list");
    expect(markdownToElement(document, markdown).innerHTML).toBe("-1. not a list");
  });

  it("tells two bullets apart from one bullet with a line break", () => {
    const two = formatSignature(editor("<ul><li>one</li><li>two</li></ul>"), 0, 7);
    const one = formatSignature(editor("<ul><li>one<br>two</li></ul>"), 0, 7);
    expect(readPlainText(editor("<ul><li>one<br>two</li></ul>")).text).toBe("one\ntwo");
    expect(two).not.toBe(one);
  });
});

describe("capture performance", () => {
  it("serializes a long single-line selection in linear time", () => {
    const words = Array.from({ length: 25_000 }, (_, i) => (i % 10 === 0 ? `<b>word${i}</b>` : `word${i}`));
    const root = editor(words.join(" "));
    const length = readPlainText(root).text.length;
    expect(length).toBeGreaterThan(200_000);
    const started = performance.now();
    const markdown = selectionMarkdown(root, 0, length);
    expect(performance.now() - started).toBeLessThan(LINEAR_TIME_BUDGET_MS);
    expect(markdown.startsWith("**word0** word1")).toBe(true);
  });

  it("numbers a long ordered list in linear time", () => {
    const root = editor(`<ol>${Array.from({ length: 5_000 }, (_, i) => `<li>item ${i}</li>`).join("")}</ol>`);
    const length = readPlainText(root).text.length;
    const started = performance.now();
    const markdown = selectionMarkdown(root, 0, length);
    expect(performance.now() - started).toBeLessThan(LINEAR_TIME_BUDGET_MS);
    expect(markdown.split("\n").slice(-2)).toEqual(["4999. item 4998", "5000. item 4999"]);
  });
});

describe("capture and apply round trip", () => {
  it.each([
    ["bold containing italic", "<b>US<i>D</i>100</b>", "**US*D*100**", "<b>US<i>D</i>100</b>"],
    ["italic containing bold", "<i>US<b>D</b>100</i>", "*US**D**100*", "<i>US<b>D</b>100</i>"],
    ["bold then italic", "<b>a</b><i>b</i>", "**a***b*", "<b>a</b><i>b</i>"],
    ["italic then bold", "<i>a</i><b>b</b>", "*a***b**", "<i>a</i><b>b</b>"],
    ["bold and italic together", "<b><i>both</i></b>", "***both***", "<i><b>both</b></i>"],
    ["both, then bold", "<b><i>a</i>b</b>", "***a*b**", "<b><i>a</i>b</b>"],
    ["both, then italic", "<i><b>a</b>b</i>", "***a****b*", "<i><b>a</b></i><i>b</i>"],
    ["emphasis at the start and end of a line", "<b>Hi</b> there <i>friend</i>", "**Hi** there *friend*", "<b>Hi</b> there <i>friend</i>"],
    // Inside one word no marker form reads back unambiguously, so the word loses its formatting, not its text.
    ["bold, then both, then italic", "<b>US</b><i><b>D</b>100</i>", "USD100", "USD100"],
    ["italic, then both, then bold", "<i>US</i><b><i>D</i>100</b>", "USD100", "USD100"],
    ["italic, then both, then bold, as words", "<i>a </i><b><i>b</i> c</b>", "*a **b*** **c**", "<i>a <b>b</b></i> <b>c</b>"],
    ["bold, then both, then italic, as words", "<b>a </b><i><b>b</b> c</i>", "**a *b*** *c*", "<b>a <i>b</i></b> <i>c</i>"],
    ["arithmetic", "5 * 3 * 2 = 30", "5 \\* 3 \\* 2 = 30", "5 * 3 * 2 = 30"],
  ])("keeps the text and formatting of %s", (_, source, markdown, rendered) => {
    const root = editor(source);
    const text = readPlainText(root).text;
    const captured = selectionMarkdown(root, 0, text.length);
    expect(captured).toBe(markdown);
    const container = markdownToElement(document, captured);
    expect(readPlainText(container).text).toBe(text);
    expect(container.innerHTML).toBe(rendered);
  });

  it("splits a text node at its own newlines", () => {
    const root = editor(`<b style="white-space: pre-wrap">first\n- second</b>`);
    const captured = selectionMarkdown(root, 0, 14);
    expect(captured).toBe("**first**\n**- second**");
    const container = markdownToElement(document, captured);
    expect(readPlainText(container).text).toBe("first\n- second");
    expect(container.innerHTML).toBe("<b>first</b><br><b>- second</b>");
  });

  it("escapes a list marker on a later line of a text node", () => {
    const root = editor(`<span style="white-space: pre-wrap">first\n- second\n2. third</span>`);
    const captured = selectionMarkdown(root, 0, 23);
    expect(captured).toBe("first\n\\- second\n2\\. third");
    const container = markdownToElement(document, captured);
    expect(container.querySelector("ul, ol")).toBeNull();
    expect(readPlainText(container).text).toBe("first\n- second\n2. third");
  });
});

describe("markdownToElement", () => {
  it("renders every form of the subset", () => {
    expect(html("**b** *i* _u_ ***both***")).toBe("<b>b</b> <i>i</i> <i>u</i> <i><b>both</b></i>");
    expect(html("[site](https://example.com) [m](mailto:a@example.com)")).toBe(
      `<a href="https://example.com">site</a> <a href="mailto:a@example.com">m</a>`,
    );
    expect(html("intro\n- a\n* b\n\n3. c\n4. d\nend")).toBe(`intro<ul><li>a</li><li>b</li></ul><ol start="3"><li>c</li><li>d</li></ol>end`);
    expect(html("one\n\ntwo")).toBe("one<br><br>two");
  });

  it("drops an empty line right after a list, which the editor already spaces", () => {
    const container = markdownToElement(document, "1. one\n2. two\n\nhttps://example.com");
    expect(container.innerHTML).toBe("<ol><li>one</li><li>two</li></ol>https://example.com");
    expect(readPlainText(container).text).toBe("one\ntwo\nhttps://example.com");
  });

  it("drops an empty line right before a list but keeps a second one", () => {
    expect(markdownToElement(document, "Items:\n\n- a").innerHTML).toBe("Items:<ul><li>a</li></ul>");
    expect(markdownToElement(document, "Items:\n\n\n- a").innerHTML).toBe("Items:<br><br><ul><li>a</li></ul>");
  });

  it("leaves arithmetic, lone markers, and intraword underscores literal", () => {
    expect(html("5 * 3 * 2 and a * b")).toBe("5 * 3 * 2 and a * b");
    expect(html("snake_case_name and **")).toBe("snake_case_name and **");
    expect(html("\\*not\\* \\[x\\]")).toBe("*not* [x]");
  });

  it("renders a link with a disallowed scheme as text", () => {
    const container = markdownToElement(document, "[click](javascript:alert) [x](data:text/html,hi)");
    expect(container.querySelector("a")).toBeNull();
    expect(container.textContent).toBe("[click](javascript:alert) [x](data:text/html,hi)");
  });

  it("keeps image syntax literal", () => {
    const container = markdownToElement(document, "see ![scan](https://example.com/scan.png) and ![x]() **ok**");
    expect(container.innerHTML).toBe("see ![scan](https://example.com/scan.png) and ![x]() <b>ok</b>");
    expect(stripMarkdown("see ![scan](https://example.com/scan.png)\n- ![a](b)")).toBe("see ![scan](https://example.com/scan.png)\n- ![a](b)");
  });

  it("keeps HTML in the Markdown as text, never as elements", () => {
    const container = markdownToElement(document, `<script>alert(1)</script> <img src=x onerror="alert(1)"> **<b>x</b>**`);
    expect(container.querySelector("script, img")).toBeNull();
    expect(Array.from(container.querySelectorAll("*"), (e) => e.tagName)).toEqual(["B"]);
    expect(container.textContent).toBe(`<script>alert(1)</script> <img src=x onerror="alert(1)"> <b>x</b>`);
  });

  it("keeps a run of spaces visible", () => {
    expect(markdownToElement(document, "a   b").textContent).toBe("a\u00a0\u00a0 b");
  });
});

describe("stripMarkdown", () => {
  it("keeps list markers and drops emphasis and link targets", () => {
    expect(stripMarkdown("**Hi** *there*,\n* one\n- [two](https://example.com)\n1. three\n7. four")).toBe(
      "Hi there,\n- one\n- two\n1. three\n7. four",
    );
  });
});
