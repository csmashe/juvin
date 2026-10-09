// Throwaway Gmail DOM spike. Records structure, counts, lengths, and hashes only; never message text.
const api = typeof browser !== "undefined" ? browser : chrome;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const visible = (el) => !!(el && el.offsetParent !== null);

async function sha256(text) {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("").slice(0, 16);
}

function findEditor() {
  const candidates = [...document.querySelectorAll('div[contenteditable="true"][role="textbox"]')].filter(visible);
  const byLabel = candidates.find((el) => /message body/i.test(el.getAttribute("aria-label") || ""));
  return byLabel || candidates.find((el) => el.getAttribute("g_editable") === "true") || candidates[0] || null;
}

function threadSnapshot() {
  const items = [...document.querySelectorAll('div[role="main"] div[role="listitem"]')].filter(visible);
  const messages = items.map((item) => {
    const body = item.querySelector("div.a3s");
    const expanded = !!(body && visible(body) && body.innerText.trim().length > 0);
    const sender = item.querySelector("span.gD, span[email]");
    const date = item.querySelector("span.g3");
    return {
      expanded,
      bodyChars: expanded ? body.innerText.trim().length : 0,
      hasSender: !!sender,
      senderIsEmailAttr: !!(sender && sender.getAttribute("email")),
      hasDate: !!date,
      hasQuotedSection: !!item.querySelector("div.adL, div.gmail_quote, blockquote"),
      hasTrimmedToggle: !!item.querySelector('[aria-label*="trimmed" i], div.ajR'),
    };
  });
  const bundle = [...document.querySelectorAll('div[role="main"] [role="button"], div[role="main"] span')]
    .filter(visible)
    .map((el) => (el.getAttribute("aria-label") || el.textContent || "").trim())
    .find((t) => /^\d+ (older|more|hidden)/i.test(t)) || null;
  return { total: messages.length, expanded: messages.filter((m) => m.expanded).length, collapsed: messages.filter((m) => !m.expanded).length, bundleLabel: bundle, messages };
}

async function expandCollapsed(maxRounds) {
  const rounds = [];
  const expandAll = [...document.querySelectorAll('div[role="main"] [aria-label="Expand all"], div[role="main"] [data-tooltip="Expand all"]')].filter(visible)[0];
  if (expandAll) {
    const before = threadSnapshot();
    expandAll.click();
    await sleep(1500);
    const after = threadSnapshot();
    rounds.push({ round: "expand-all", clicked: "expand-all", collapsedBefore: before.collapsed, collapsedAfter: after.collapsed, totalBefore: before.total, totalAfter: after.total, bundleAfter: after.bundleLabel });
  } else {
    rounds.push({ round: "expand-all", clicked: "not-found" });
  }
  for (let i = 0; i < maxRounds; i++) {
    const before = threadSnapshot();
    const bundle = [...document.querySelectorAll('div[role="main"] [role="button"], div[role="main"] span[role="link"]')]
      .filter(visible).find((el) => /^\d+ (older|more|hidden)/i.test((el.getAttribute("aria-label") || el.textContent || "").trim()));
    let clicked = "none";
    if (bundle) { bundle.click(); clicked = "bundle"; }
    else {
      const items = [...document.querySelectorAll('div[role="main"] div[role="listitem"]')].filter(visible);
      const collapsedHeader = items.map((it) => it.querySelector("div.kv, div.kQ, div.adn > div[role='button'], td.gF, div.gE"))
        .find((h) => h && visible(h) && !h.closest("div[role='listitem']").querySelector("div.a3s:not([style*='display: none'])"));
      if (collapsedHeader) { collapsedHeader.click(); clicked = "header"; }
    }
    if (clicked === "none") { rounds.push({ round: i, clicked, before: before.collapsed }); break; }
    await sleep(900);
    const after = threadSnapshot();
    rounds.push({ round: i, clicked, collapsedBefore: before.collapsed, collapsedAfter: after.collapsed, totalBefore: before.total, totalAfter: after.total });
    if (after.collapsed === 0 && !after.bundleLabel) { break; }
  }
  return rounds;
}

function selectAllIn(editor) {
  editor.focus();
  const range = document.createRange();
  range.selectNodeContents(editor);
  const sel = window.getSelection();
  sel.removeAllRanges();
  sel.addRange(range);
  return sel;
}

async function runSpike(options) {
  const withEditor = !(options && options.threadOnly);
  const report = { phase: "start", threadOnly: !withEditor, url: location.pathname, startedAt: new Date().toISOString(), steps: [] };
  const t0 = performance.now();
  try {
    report.threadBefore = threadSnapshot();
    report.expandRounds = await expandCollapsed(8);
    report.threadAfter = threadSnapshot();
    report.steps.push({ step: "thread", ms: Math.round(performance.now() - t0) });
    if (!withEditor) { report.phase = "done"; report.totalMs = Math.round(performance.now() - t0); return report; }

    let editor = findEditor();
    if (!editor) {
      const replyBtn = [...document.querySelectorAll('div[role="main"] span[role="link"], div[role="main"] div[role="button"], div[role="main"] span.ams')]
        .filter(visible).find((el) => /^reply$/i.test((el.getAttribute("aria-label") || el.textContent || "").trim()) || /^reply$/i.test(el.getAttribute("data-tooltip") || ""));
      report.replyButtonFound = !!replyBtn;
      if (replyBtn) { replyBtn.click(); for (let i = 0; i < 20 && !(editor = findEditor()); i++) { await sleep(300); } }
      report.openedReply = !!editor;
    }
    if (!editor) { report.phase = "no-editor"; return report; }
    report.editor = { ariaLabel: editor.getAttribute("aria-label"), gEditable: editor.getAttribute("g_editable"), tag: editor.tagName, initialChars: editor.innerText.length };

    editor.focus();
    const draftLines = ["Hi team, thanks for the update on the shipment.", "I think we should wait until the invoice arrives before paying.", "Let me know if Friday works for the call."];
    const typedOk = document.execCommand("insertText", false, draftLines.join("\n"));
    await sleep(300);
    const draftText = editor.innerText;
    report.draft = { insertTextOk: typedOk, chars: draftText.length, hash: await sha256(draftText) };

    const sel = selectAllIn(editor);
    const selectedText = sel.toString();
    report.selection = { chars: selectedText.length, rangeCount: sel.rangeCount, anchorInsideEditor: editor.contains(sel.anchorNode), coversDraft: selectedText.trim() === draftText.trim(), hash: await sha256(selectedText) };

    const replacement = "Hi team, thank you for the shipment update. Let's hold payment until the invoice arrives. Does Friday work for a call?";
    const replaceOk = document.execCommand("insertText", false, replacement);
    await sleep(300);
    const afterReplace = editor.innerText;
    report.replace = { execCommandOk: replaceOk, charsAfter: afterReplace.length, matchesReplacement: afterReplace.trim() === replacement, hashAfter: await sha256(afterReplace) };

    const undoOk = document.execCommand("undo", false, null);
    await sleep(300);
    const afterUndo = editor.innerText;
    report.undo = { execCommandOk: undoOk, charsAfter: afterUndo.length, restoredDraft: afterUndo.trim() === draftText.trim(), hashAfter: await sha256(afterUndo) };

    const redoOk = document.execCommand("redo", false, null);
    await sleep(300);
    report.redo = { execCommandOk: redoOk, matchesReplacement: editor.innerText.trim() === replacement };

    const discard = [...document.querySelectorAll('[aria-label^="Discard draft" i], [data-tooltip^="Discard draft" i]')].filter(visible)[0];
    report.discardButtonFound = !!discard;
    if (discard) { discard.click(); await sleep(1200); report.editorGoneAfterDiscard = !findEditor(); }
    report.phase = "done";
  } catch (e) {
    report.phase = "error"; report.error = String(e && e.stack || e);
  }
  report.totalMs = Math.round(performance.now() - t0);
  return report;
}

function pickThread() {
  const rows = [...document.querySelectorAll("tr.zA")].filter(visible);
  const scored = rows.map((row) => {
    const senderCell = row.querySelector("td.yX");
    const text = senderCell ? senderCell.innerText.trim() : "";
    const explicit = row.querySelector("span.bx0");
    const m = text.match(/(\d+)\s*$/);
    const count = explicit ? parseInt(explicit.textContent, 10) : (m ? parseInt(m[1], 10) : 1);
    return { row, count: isNaN(count) ? 1 : count, senderChars: text.length };
  }).filter((r) => r.count >= 4 && r.count < 500).sort((a, b) => b.count - a.count);
  const out = { rowsVisible: rows.length, candidates: scored.length, picked: false, topCounts: scored.slice(0, 5).map((r) => r.count) };
  if (scored.length) {
    const best = scored[0];
    out.pickedCount = best.count;
    const subject = best.row.querySelector("span.bog, span.bqe") || best.row.querySelector("td.xY");
    (subject || best.row).click();
    out.picked = true;
  }
  return out;
}

api.runtime.onMessage.addListener((msg) => {
  if (!msg) { return; }
  if (msg.type === "run-spike") { return runSpike(msg.options); }
  if (msg.type === "pick-thread") { return Promise.resolve(pickThread()); }
});
