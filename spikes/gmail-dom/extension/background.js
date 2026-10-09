const api = typeof browser !== "undefined" ? browser : chrome;
const REPORT_URL = "http://127.0.0.1:18765/report";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = (data) => fetch(REPORT_URL, { method: "POST", body: JSON.stringify(data) }).catch(() => {});

const THREAD_ONLY = true;

async function send(tabId, type, attempts = 10) {
  for (let i = 0; i < attempts; i++) {
    try { return await api.tabs.sendMessage(tabId, { type, options: { threadOnly: THREAD_ONLY } }); } catch (e) { await sleep(1000); }
  }
  return { phase: "error", error: "content script unreachable for " + type };
}

async function runAuto() {
  await report({ phase: "auto-start", at: new Date().toISOString() });
  const tab = await api.tabs.create({ url: "https://mail.google.com/mail/u/0/#inbox", active: true });
  await sleep(10000);
  const pick = await send(tab.id, "pick-thread");
  await report({ phase: "pick-thread", ...pick });
  if (!pick || !pick.picked) { return; }
  await sleep(6000);
  const result = await send(tab.id, "run-spike");
  await report(result);
}

async function runCurrent() {
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });
  if (!tab || !/mail\.google\.com/.test(tab.url || "")) { await report({ phase: "error", error: "active tab is not Gmail" }); return; }
  await report(await send(tab.id, "run-spike"));
}

api.runtime.onInstalled.addListener(() => { runAuto(); });
api.commands.onCommand.addListener((name) => { if (name === "run-spike") { runCurrent(); } });
api.action.onClicked.addListener(() => runCurrent());
