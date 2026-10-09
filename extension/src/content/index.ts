import browser from "webextension-polyfill";
import { isContentRequest, type ApplyResult, type CaptureResult, type ContentRequest } from "../shared/runtime-messages";
import { applyReplacement } from "./apply";
import { captureContext } from "./capture";
import { EditorRegistry } from "./editor-registry";

declare global {
  interface Window {
    juvinContentLoaded?: boolean;
  }
}

async function handle(registry: EditorRegistry, request: ContentRequest): Promise<CaptureResult | ApplyResult> {
  try {
    if (request.kind === "juvin.capture") {
      return await captureContext(document, location, registry);
    }

    return await applyReplacement(document, registry, request);
  } catch (e) {
    return { ok: false, error: { code: "internal", message: `Content script failed: ${e instanceof Error ? e.name : "unknown"}.` } };
  }
}

function start(): void {
  const registry = new EditorRegistry();

  // On the bundled test page this runs as an extension page, which receives runtime messages addressed by tab id.
  let ownTabId: number | undefined;
  if (browser.tabs?.getCurrent) {
    void browser.tabs.getCurrent().then((tab) => (ownTabId = tab?.id));
  }

  browser.runtime.onMessage.addListener((message: unknown) => {
    if (!isContentRequest(message) || (message.tabId !== undefined && message.tabId !== ownTabId)) {
      return undefined;
    }

    return handle(registry, message);
  });
}

// The background may inject this script into a tab that already has it.
if (!window.juvinContentLoaded) {
  window.juvinContentLoaded = true;
  start();
}
