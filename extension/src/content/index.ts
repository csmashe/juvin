import browser from "webextension-polyfill";
import { isContentRequest, type ApplyResult, type CaptureResult, type ContentRequest, type MenuDeliveryResult, type MenuNotice, type MenuShowResult } from "../shared/runtime-messages";
import { applyReplacement } from "./apply";
import { captureContext } from "./capture";
import { EditorRegistry } from "./editor-registry";
import { MenuRenderer } from "./menu";

declare global {
  interface Window {
    juvinContentLoaded?: boolean;
  }
}

async function handle(registry: EditorRegistry, menu: MenuRenderer, request: ContentRequest): Promise<CaptureResult | ApplyResult | MenuShowResult | MenuDeliveryResult> {
  try {
    switch (request.kind) {
      case "juvin.capture":
        return await captureContext(document, location, registry, undefined, request.editorId);
      case "juvin.apply":
        return await applyReplacement(document, registry, request);
      case "juvin.menu.show":
        return menu.show(request);
      case "juvin.menu.update":
        return { ok: menu.update(request.update) };
      case "juvin.menu.close":
        return { ok: menu.close(request.menuId) };
      case "juvin.menu.disconnected":
        return { ok: menu.disconnected(request.menuId) };
      case "juvin.menu.check":
        return { ok: menu.has(request.menuId) };
    }
  } catch (e) {
    return { ok: false, error: { code: "internal", message: `Content script failed: ${e instanceof Error ? e.name : "unknown"}.` } };
  }
}

function start(): void {
  const registry = new EditorRegistry();
  const menu = new MenuRenderer(registry, (notice: MenuNotice) => browser.runtime.sendMessage(notice));

  // On the bundled test page this runs as an extension page, which receives runtime messages addressed by tab id.
  let ownTabId: number | undefined;
  if (browser.tabs?.getCurrent) {
    void browser.tabs.getCurrent().then((tab) => (ownTabId = tab?.id));
  }

  browser.runtime.onMessage.addListener((message: unknown) => {
    if (!isContentRequest(message) || (message.tabId !== undefined && message.tabId !== ownTabId)) {
      return undefined;
    }

    return handle(registry, menu, message);
  });
}

// The background may inject this script into a tab that already has it.
if (!window.juvinContentLoaded) {
  window.juvinContentLoaded = true;
  start();
}
