import browser, { type Tabs } from "webextension-polyfill";
import { log, warn } from "../shared/log";
import {
  applyError,
  applyOk,
  contextError,
  shortcutPressed,
  testPageError,
  testPageOk,
  type ApplyRequestMessage,
  type ContextRequestMessage,
  type IncomingMessage,
  type TestPageRequestMessage,
} from "../shared/protocol";
import {
  isPopupRequest,
  type ApplyResult,
  type CaptureResult,
  type ContentRequest,
  type PingResult,
  type PopupRequest,
  type Status,
} from "../shared/runtime-messages";
import { Connection, type Reply } from "./connection";
import { EditorTabs } from "./editor-tabs";
import { ShortcutTabs } from "./shortcut-tabs";
import { buildTrimmedContextResponse } from "./trim";

const CONTENT_SCRIPT = "content.js";

const connection = new Connection(onAppRequest);

const editorTabs = new EditorTabs((tabId) => browser.tabs.get(tabId));

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

async function handleApplyRequest({ id, editorId, expectedHash, start, end, text }: ApplyRequestMessage, reply: Reply): Promise<void> {
  const tab = await editorTabs.find(editorId);
  const gone = applyError(id, { code: "editor-gone", message: "The editor is no longer open." });
  if (!tab) {
    reply(gone);
    return;
  }

  // No injection here: a freshly injected script would not know the editor id.
  const result = (await sendToTab(tab.id, { kind: "juvin.apply", editorId, expectedHash, start, end, text })) as ApplyResult | undefined;
  if (!result) {
    reply(gone);
    return;
  }

  log("apply.done", { id, editorId, textLength: text.length, code: result.ok ? "ok" : result.error.code });
  reply(result.ok ? applyOk(id) : applyError(id, result.error));
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

function handleAppRequest(message: IncomingMessage, reply: Reply): Promise<void> {
  switch (message.type) {
    case "context.request":
      return handleContextRequest(message, reply);
    case "apply.request":
      return handleApplyRequest(message, reply);
    case "testpage.request":
      return handleTestPageRequest(message, reply);
    default:
      return Promise.resolve();
  }
}

function onAppRequest(message: IncomingMessage, reply: Reply): void {
  log("request", { type: message.type, id: "id" in message ? message.id : undefined });
  handleAppRequest(message, reply).catch((e: unknown) => {
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

browser.runtime.onMessage.addListener((message: unknown) => (isPopupRequest(message) ? onPopupRequest(message) : undefined));

browser.tabs.onRemoved.addListener((tabId: number) => editorTabs.forgetTab(tabId));
