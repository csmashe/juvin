import browser from "webextension-polyfill";
import { detectBrowser } from "../shared/browser-detect";
import type { PingResult, PopupRequest, Status } from "../shared/runtime-messages";
import { formatShortcut } from "../shared/shortcut-format";

const ALL_SITES = { origins: ["<all_urls>"] };

function element<T extends HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) {
    throw new Error(`Missing popup element #${id}`);
  }

  return found as T;
}

const statusText = element("status");
const detail = element("detail");
const connectButton = element<HTMLButtonElement>("connect");
const testButton = element<HTMLButtonElement>("test");
const appShortcut = element("app-shortcut");
const browserShortcut = element("browser-shortcut");
const shortcutsHint = element("shortcuts-hint");
const allSitesButton = element<HTMLButtonElement>("all-sites");

let isMac = false;

function request<T>(message: PopupRequest): Promise<T> {
  return browser.runtime.sendMessage(message) as Promise<T>;
}

function showDetail(text: string | undefined): void {
  detail.textContent = text ?? "";
  detail.hidden = !text;
}

function render(status: Status): void {
  statusText.textContent = status.connected ? "Connected to Juvin" : status.connecting ? "Connecting to Juvin..." : "Not connected";
  showDetail(status.connected ? undefined : status.lastError);
  connectButton.hidden = status.connected;
  appShortcut.textContent = status.shortcut === undefined ? "not connected" : formatShortcut(status.shortcut, isMac);
}

async function refreshStatus(): Promise<void> {
  render(await request<Status>({ kind: "juvin.status" }));
}

/** Opening the popup is a user action, so it reconnects after a worker restart; an earlier failure waits for Connect. */
async function openStatus(): Promise<void> {
  const status = await request<Status>({ kind: "juvin.status" });
  if (status.connected || status.connecting || status.lastError !== undefined) {
    render(status);
    return;
  }

  render({ ...status, connecting: true });
  render(await request<Status>({ kind: "juvin.connect" }));
}

async function refreshBrowserShortcut(): Promise<void> {
  const commands = await browser.commands.getAll();
  browserShortcut.textContent = commands.find((c) => c.name === "rewrite")?.shortcut || "not set";
}

async function refreshAllSites(): Promise<void> {
  const granted = await browser.permissions.contains(ALL_SITES);
  allSitesButton.disabled = granted;
  allSitesButton.textContent = granted ? "Allowed on all sites" : "Allow Juvin on all sites";
}

async function withBusy(button: HTMLButtonElement, work: () => Promise<void>): Promise<void> {
  button.disabled = true;
  try {
    await work();
  } finally {
    button.disabled = false;
  }
}

connectButton.addEventListener("click", () =>
  withBusy(connectButton, async () => {
    statusText.textContent = "Connecting to Juvin...";
    showDetail(undefined);
    render(await request<Status>({ kind: "juvin.connect" }));
  }),
);

testButton.addEventListener("click", () =>
  withBusy(testButton, async () => {
    statusText.textContent = "Testing...";
    const result = await request<PingResult>({ kind: "juvin.ping" });
    await refreshStatus();
    showDetail(result.ok ? `Juvin answered in ${result.latencyMs} ms.` : result.error);
  }),
);

element("shortcuts").addEventListener("click", async () => {
  const commands = browser.commands as typeof browser.commands & { openShortcutSettings?: () => Promise<void> };
  try {
    if (commands.openShortcutSettings) {
      await commands.openShortcutSettings();
      return;
    }

    const page = detectBrowser({ userAgent: navigator.userAgent }).name === "edge" ? "edge://extensions/shortcuts" : "chrome://extensions/shortcuts";
    await browser.tabs.create({ url: page });
  } catch {
    shortcutsHint.textContent = detectBrowser({ userAgent: navigator.userAgent }).name === "firefox"
      ? "Open about:addons, then choose Manage Extension Shortcuts from the gear menu."
      : "Open chrome://extensions/shortcuts in a new tab.";
    shortcutsHint.hidden = false;
  }
});

allSitesButton.addEventListener("click", async () => {
  // Must be called directly from the click so the browser treats it as a user gesture.
  await browser.permissions.request(ALL_SITES);
  await refreshAllSites();
});

element("test-page").addEventListener("click", async () => {
  await browser.tabs.create({ url: browser.runtime.getURL("test-page.html") });
  window.close();
});

void (async () => {
  isMac = (await browser.runtime.getPlatformInfo()).os === "mac";
  await openStatus();
})();
void refreshBrowserShortcut();
void refreshAllSites();
