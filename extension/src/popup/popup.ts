import browser from "webextension-polyfill";
import type { PingResult, PopupRequest, Status } from "../shared/runtime-messages";

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
  appShortcut.textContent = status.shortcut ?? "not connected";
}

async function refreshStatus(): Promise<void> {
  render(await request<Status>({ kind: "juvin.status" }));
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

    const page = navigator.userAgent.includes("Edg/") ? "edge://extensions/shortcuts" : "chrome://extensions/shortcuts";
    await browser.tabs.create({ url: page });
  } catch {
    shortcutsHint.textContent = navigator.userAgent.includes("Firefox/")
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

void refreshStatus();
void refreshBrowserShortcut();
void refreshAllSites();
