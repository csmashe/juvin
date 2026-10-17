import browser, { type Runtime, type Tabs } from "webextension-polyfill";
import { log, warn } from "../shared/log";
import {
  applyError,
  applyOk,
  contextError,
  menuChoose,
  menuClose,
  menuError,
  menuOk,
  shortcutPressed,
  testPageError,
  testPageOk,
  type ApplyRequestMessage,
  type ContextRequestMessage,
  type IncomingMessage,
  type MenuCloseMessage,
  type MenuShowMessage,
  type MenuUpdateMessage,
  type TestPageRequestMessage,
} from "../shared/protocol";
import {
  isMenuNotice,
  isPopupRequest,
  type ApplyResult,
  type CaptureResult,
  type ContentRequest,
  type MenuDeliveryResult,
  type MenuNotice,
  type MenuShowResult,
  type PingResult,
  type PopupRequest,
  type Status,
} from "../shared/runtime-messages";
import { Connection, type IsLive, type Reply } from "./connection";
import { EditorTabs } from "./editor-tabs";
import { ShortcutTabs } from "./shortcut-tabs";
import { buildTrimmedContextResponse } from "./trim";

const CONTENT_SCRIPT = "content.js";

const connection = new Connection(onAppRequest, onDisconnected);

const editorTabs = new EditorTabs((tabId) => browser.tabs.get(tabId));

// The extension shows one menu at a time; this is the tab drawing it.
let openMenu: { menuId: string; tabId: number } | undefined;

// A menu.show still waiting for its tab; a close or a newer show meanwhile makes its result stale.
let pendingShow: { menuId: string; cancelled: boolean } | undefined;

async function activeTab(): Promise<Tabs.Tab | undefined> {
  const [tab] = await browser.tabs.query({ active: true, lastFocusedWindow: true });
  return tab;
}

const shortcutTabs = new ShortcutTabs({
  get: (tabId) => browser.tabs.get(tabId),
  active: activeTab,
  // Chrome can stay connected with no window at all; getLastFocused then rejects, which is "not focused".
  isFocused: () => browser.windows.getLastFocused().then((w) => w.focused === true, () => false),
});

function connectQuietly(trigger: string): void {
  connection.ensureConnected().catch(() => log("connect.failed", { trigger }));
}

function errorName(e: unknown): string {
  return e instanceof Error ? e.name : "unknown";
}

/** Undefined when no content script answered: every content request has a reply. */
async function sendToTab(tabId: number, request: ContentRequest): Promise<unknown> {
  try {
    const reply = await browser.tabs.sendMessage(tabId, request);
    if (reply !== undefined) {
      return reply;
    }
  } catch {
    // No content script in the tab; try the routes below.
  }

  // The bundled test page is an extension page and only receives runtime messages.
  try {
    return await browser.runtime.sendMessage({ ...request, tabId });
  } catch {
    return undefined;
  }
}

async function injectContentScript(tabId: number): Promise<boolean> {
  try {
    await browser.scripting.executeScript({ target: { tabId }, files: [CONTENT_SCRIPT] });
    return true;
  } catch (e) {
    log("inject.failed", { tabId, error: errorName(e) });
    return false;
  }
}

async function capture(tab: Tabs.Tab & { id: number }, editorId: string | undefined): Promise<CaptureResult | undefined> {
  const request: ContentRequest = editorId === undefined ? { kind: "juvin.capture" } : { kind: "juvin.capture", editorId };
  const reply = await sendToTab(tab.id, request);
  if (reply !== undefined) {
    return reply as CaptureResult;
  }

  // Tabs opened before install, or other sites under activeTab or the optional all-sites permission.
  if (await injectContentScript(tab.id)) {
    return (await sendToTab(tab.id, request)) as CaptureResult | undefined;
  }

  return undefined;
}

async function handleContextRequest({ id, editorId, requireFocus }: ContextRequestMessage, reply: Reply): Promise<void> {
  // A recapture names its editor and never falls back to the active tab.
  let tab: Tabs.Tab & { id: number };
  if (editorId === undefined) {
    const requested = await shortcutTabs.take(connection.generation, requireFocus === true);
    if (!requested.ok) {
      log("context.failed", { id, code: requested.error.code });
      reply(contextError(id, requested.error));
      return;
    }

    tab = requested.tab;
  } else {
    const owner = await editorTabs.find(editorId);
    if (!owner) {
      reply(contextError(id, { code: "editor-gone", message: "The editor is no longer open." }));
      return;
    }

    tab = owner;
  }

  const result = await capture(tab, editorId);
  if (!result) {
    reply(contextError(id, { code: "page-not-ready", message: "The page cannot be read yet. Reload it, or allow Juvin on all sites from its popup." }));
    return;
  }

  if (!result.ok) {
    log("context.failed", { id, code: result.error.code });
    reply(contextError(id, result.error));
    return;
  }

  editorTabs.remember(result.capture.editor.id, tab.id);
  const response = buildTrimmedContextResponse(id, result.capture);
  if (!response) {
    reply(contextError(id, { code: "internal", message: "The selection is too large to send." }));
    return;
  }

  if (response.ok) {
    log("context.sent", {
      id,
      adapter: response.capture.adapter,
      selectionLength: response.capture.selection.text.length,
      messages: response.capture.thread.messages.length,
      completeness: response.capture.thread.completeness,
    });
  }

  reply(response);
}

async function handleApplyRequest({ id, editorId, expectedHash, start, end, text, format, expectedFormatHash }: ApplyRequestMessage, reply: Reply, isLive: IsLive): Promise<void> {
  const tab = await editorTabs.find(editorId);
  // The app that asked is gone and can no longer see or undo this apply.
  if (!isLive()) {
    log("apply.stale", { id });
    return;
  }

  const gone = applyError(id, { code: "editor-gone", message: "The editor is no longer open." });
  if (!tab) {
    reply(gone);
    return;
  }

  // No injection here: a freshly injected script would not know the editor id.
  const result = (await sendToTab(tab.id, { kind: "juvin.apply", editorId, expectedHash, start, end, text, ...(format ? { format, expectedFormatHash } : {}) })) as ApplyResult | undefined;
  if (!result) {
    reply(gone);
    return;
  }

  log("apply.done", { id, editorId, textLength: text.length, format: format ?? "plain", code: result.ok ? "ok" : result.error.code });
  reply(result.ok ? applyOk(id) : applyError(id, result.error));
}

async function handleMenuShow({ id, menuId, editorId, slots, more, status }: MenuShowMessage, reply: Reply): Promise<void> {
  if (pendingShow) {
    pendingShow.cancelled = true;
  }

  const pending = { menuId, cancelled: false };
  pendingShow = pending;
  const stale = (): boolean => {
    if (pendingShow === pending) {
      pendingShow = undefined;
    }

    return pending.cancelled;
  };

  const tab = await editorTabs.find(editorId);
  if (!tab || pending.cancelled) {
    stale();
    reply(menuError(id, tab ? { code: "internal", message: "The menu was replaced before it was shown." } : { code: "editor-gone", message: "The editor is no longer open." }));
    return;
  }

  // No injection, as for apply: a freshly injected script would not know the editor id.
  const result = (await sendToTab(tab.id, { kind: "juvin.menu.show", menuId, editorId, slots, more, status })) as MenuShowResult | undefined;
  if (stale()) {
    log("menu.show.stale", { id, menuId });
    if (result?.ok) {
      void sendToTab(tab.id, { kind: "juvin.menu.close", menuId });
    }

    reply(menuError(id, { code: "internal", message: "The menu was replaced before it was shown." }));
    return;
  }

  if (!result) {
    reply(menuError(id, { code: "page-not-ready", message: "The page cannot show the menu yet." }));
    return;
  }

  log("menu.show", { id, menuId, more: more.length, code: result.ok ? "ok" : result.error.code });
  if (result.ok) {
    const previous = openMenu;
    openMenu = { menuId, tabId: tab.id };
    // A new menu replaces the old one in any tab without reporting it closed.
    if (previous && previous.tabId !== tab.id) {
      void sendToTab(previous.tabId, { kind: "juvin.menu.close", menuId: previous.menuId });
    }
  }

  reply(result.ok ? menuOk(id) : menuError(id, result.error));
}

async function handleMenuUpdate(update: MenuUpdateMessage): Promise<void> {
  if (openMenu?.menuId !== update.menuId) {
    return;
  }

  log("menu.update", { menuId: update.menuId, state: update.state, textLength: "text" in update ? update.text.length : undefined });
  const menu = openMenu;
  if (update.state === "applied") {
    openMenu = undefined;
  }

  const result = (await sendToTab(menu.tabId, { kind: "juvin.menu.update", update })) as MenuDeliveryResult | undefined;
  // Menu ids repeat after an app restart, so only this menu object's failure may close it.
  if (result?.ok !== true && openMenu === menu) {
    menuLost("menu.lost");
  }
}

/** The page no longer draws the open menu; the app stops whatever it was running for it. */
function menuLost(event: string): void {
  if (!openMenu) {
    return;
  }

  log(event, { menuId: openMenu.menuId });
  connection.send(menuClose(openMenu.menuId));
  openMenu = undefined;
}

async function handleMenuClose({ menuId }: MenuCloseMessage): Promise<void> {
  if (pendingShow?.menuId === menuId) {
    pendingShow.cancelled = true;
    return;
  }

  if (openMenu?.menuId !== menuId) {
    return;
  }

  const { tabId } = openMenu;
  openMenu = undefined;
  await sendToTab(tabId, { kind: "juvin.menu.close", menuId });
}

/** Rejects a choice the app will not receive, so the menu stops waiting for its update. */
function onMenuNotice(notice: MenuNotice, sender: Runtime.MessageSender): Promise<void> | undefined {
  const notDelivered = (): Promise<void> | undefined => (notice.kind === "juvin.menu.choose" ? Promise.reject(new Error("The choice did not reach Juvin.")) : undefined);
  if (openMenu?.menuId !== notice.menuId || openMenu.tabId !== sender.tab?.id) {
    return notDelivered();
  }

  if (notice.kind === "juvin.menu.closed") {
    log("menu.closed", { menuId: notice.menuId });
    openMenu = undefined;
    connection.send(menuClose(notice.menuId));
    return undefined;
  }

  log("menu.choose", { menuId: notice.menuId, action: notice.action, instructionLength: notice.instruction?.length });
  // The menu closes itself on Review, and the app sends no menu.close for it.
  if (notice.action === "review") {
    openMenu = undefined;
  }

  return connection.send(menuChoose(notice.menuId, notice.action, notice.instruction)) ? undefined : notDelivered();
}

function onDisconnected(): void {
  if (pendingShow) {
    pendingShow.cancelled = true;
  }

  if (openMenu) {
    log("menu.disconnected", { menuId: openMenu.menuId });
    void sendToTab(openMenu.tabId, { kind: "juvin.menu.disconnected", menuId: openMenu.menuId });
    openMenu = undefined;
  }
}

async function handleTestPageRequest({ id }: TestPageRequestMessage, reply: Reply): Promise<void> {
  // Without windowId the tab lands in the "current" window, not the last-used one; popups are skipped by hand because Firefox ignores windowTypes.
  const window = await browser.windows.getLastFocused().catch(() => undefined);
  const windowId = window?.type === "normal" ? window.id : undefined;
  const tab = await browser.tabs.create({
    url: browser.runtime.getURL("test-page.html"),
    active: true,
    ...(typeof windowId === "number" ? { windowId } : {}),
  });
  if (typeof tab.windowId === "number") {
    await browser.windows.update(tab.windowId, { focused: true }).catch(() => undefined);
  }

  log("testpage.opened", { id });
  reply(testPageOk(id));
}

function handleAppRequest(message: IncomingMessage, reply: Reply, isLive: IsLive): Promise<void> {
  switch (message.type) {
    case "context.request":
      return handleContextRequest(message, reply);
    case "apply.request":
      return handleApplyRequest(message, reply, isLive);
    case "testpage.request":
      return handleTestPageRequest(message, reply);
    case "menu.show":
      return handleMenuShow(message, reply);
    case "menu.update":
      return handleMenuUpdate(message);
    case "menu.close":
      return handleMenuClose(message);
    default:
      return Promise.resolve();
  }
}

function onAppRequest(message: IncomingMessage, reply: Reply, isLive: IsLive): void {
  log("request", { type: message.type, id: "id" in message ? message.id : undefined });
  handleAppRequest(message, reply, isLive).catch((e: unknown) => {
    warn("request.failed", { type: message.type, error: errorName(e) });
    switch (message.type) {
      case "context.request":
        reply(contextError(message.id, { code: "internal", message: "The capture failed." }));
        break;
      case "apply.request":
        reply(applyError(message.id, { code: "internal", message: "The apply failed." }));
        break;
      case "testpage.request":
        reply(testPageError(message.id, { code: "internal", message: "The sample page could not be opened." }));
        break;
      case "menu.show":
        reply(menuError(message.id, { code: "internal", message: "The menu could not be shown." }));
        break;
    }
  });
}

async function onPopupRequest(request: PopupRequest): Promise<Status | PingResult> {
  switch (request.kind) {
    case "juvin.status":
      return connection.status();
    case "juvin.connect":
      await connection.ensureConnected().catch(() => undefined);
      return connection.status();
    case "juvin.ping":
      try {
        return { ok: true, latencyMs: await connection.ping() };
      } catch (e) {
        return { ok: false, error: e instanceof Error ? e.message : "The test failed." };
      }
  }
}

browser.runtime.onStartup.addListener(() => connectQuietly("startup"));
browser.runtime.onInstalled.addListener(() => connectQuietly("installed"));

browser.commands.onCommand.addListener((command: string, commandTab: Tabs.Tab | undefined) => {
  if (command !== "rewrite") {
    return;
  }

  // Use the tab the command event supplies; otherwise ask before focus can change.
  (commandTab ? Promise.resolve(commandTab) : activeTab())
    .then(async (tab) => {
      await connection.ensureConnected();
      shortcutTabs.push(connection.generation, tab?.id);
      connection.send(shortcutPressed());
    })
    .catch(() => log("shortcut.not-connected"));
});

browser.runtime.onMessage.addListener((message: unknown, sender: Runtime.MessageSender) => {
  if (isPopupRequest(message)) {
    return onPopupRequest(message);
  }

  if (isMenuNotice(message)) {
    return onMenuNotice(message, sender);
  }

  return undefined;
});

browser.tabs.onRemoved.addListener((tabId: number) => {
  editorTabs.forgetTab(tabId);
  if (openMenu?.tabId === tabId) {
    menuLost("menu.tab-closed");
  }
});

// A same-document navigation (Gmail changes the URL as a draft saves) keeps the menu, so ask the page rather than assume a reload.
browser.tabs.onUpdated.addListener((tabId: number, changeInfo: Tabs.OnUpdatedChangeInfoType) => {
  const menu = openMenu;
  if (changeInfo.status !== "complete" || menu?.tabId !== tabId) {
    return;
  }

  void sendToTab(tabId, { kind: "juvin.menu.check", menuId: menu.menuId }).then((result) => {
    if ((result as MenuDeliveryResult | undefined)?.ok !== true && openMenu === menu) {
      // The editor ids died with the document.
      editorTabs.forgetTab(tabId);
      menuLost("menu.tab-reloaded");
    }
  });
});
