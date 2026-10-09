// Fills the reply editor with a sample draft, one div per line like a webmail editor.

const DRAFT_LINES = [
  "hey robin",
  "",
  "april is mostly fine for me but not the first week, i have the audit then. also can we do somewhere closer this time, last year was a long drive.",
  "",
  "thx",
];

function resetDraft(editor: HTMLElement): void {
  editor.replaceChildren(
    ...DRAFT_LINES.map((line) => {
      const div = document.createElement("div");
      div.append(line ? document.createTextNode(line) : document.createElement("br"));
      return div;
    }),
  );
}

const editor = document.getElementById("juvin-test-editor");
if (editor) {
  resetDraft(editor);
  document.getElementById("reset")?.addEventListener("click", () => resetDraft(editor));
}
