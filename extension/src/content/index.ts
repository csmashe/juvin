import browser from "webextension-polyfill";
import { isContentRequest, type ApplyResult, type CaptureResult, type ContentRequest, type MenuDeliveryResult, type MenuNotice, type MenuShowResult, type PopupRequest, type Status } from "../shared/runtime-messages";
import { isMacShortcut } from "../shared/shortcut-format";
import { applyReplacement } from "./apply";
import { captureContext } from "./capture";
import { EditorRegistry } from "./editor-registry";
import { MenuRenderer } from "./menu";

declare global {
  interface Window {
    juvinContentLoaded?: boolean;
  }
}

async function handle(
  registry: EditorRegistry,
  menu: MenuRenderer,
  guard: ShortcutGuard | undefined,
  request: ContentRequest,
): Promise<CaptureResult | ApplyResult | MenuShowResult | MenuDeliveryResult> {
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
      case "juvin.shortcut":
        guard?.set(request.shortcut, request.shortcutKey);
        return { ok: true };
    }
  } catch (e) {
    return { ok: false, error: { code: "internal", message: `Content script failed: ${e instanceof Error ? e.name : "unknown"}.` } };
  }
}

/**
 * On macOS a Control chord is also a text-editing key (Control+Shift+F extends the selection by a character), and
 * Firefox hands it to the page, so the selection would grow before the app captures it.
 */
class ShortcutGuard {
  private shortcut?: string;
  private shortcutKey?: string;
  private updated = false;

  constructor() {
    window.addEventListener(
      "keydown",
      (event) => {
        if (this.shortcut !== undefined && isMacShortcut(this.shortcut, event, this.shortcutKey)) {
          event.preventDefault();
        }
      },
      { capture: true },
    );
    // The background sends later changes; this covers a page that loads while the app is already connected.
    browser.runtime.sendMessage({ kind: "juvin.status" } satisfies PopupRequest).then(
      (status: unknown) => {
        if (!this.updated) {
          this.apply((status as Status | undefined)?.shortcut, (status as Status | undefined)?.shortcutKey);
        }
      },
      () => undefined,
    );
  }

  /** A change from the background, newer than the status asked for at load. */
  set(shortcut: string | undefined, shortcutKey: string | undefined): void {
    this.updated = true;
    this.apply(shortcut, shortcutKey);
  }

  private apply(shortcut: string | undefined, shortcutKey: string | undefined): void {
    this.shortcut = shortcut;
    this.shortcutKey = shortcutKey;
  }
}

function start(): void {
  const registry = new EditorRegistry();
  const menu = new MenuRenderer(registry, (notice: MenuNotice) => browser.runtime.sendMessage(notice));
  const guard = navigator.userAgent.includes("Macintosh") ? new ShortcutGuard() : undefined;

  // On the bundled test page this runs as an extension page, which receives runtime messages addressed by tab id.
  let ownTabId: number | undefined;
  if (browser.tabs?.getCurrent) {
    void browser.tabs.getCurrent().then((tab) => (ownTabId = tab?.id));
  }

  browser.runtime.onMessage.addListener((message: unknown) => {
    if (!isContentRequest(message) || (message.tabId !== undefined && message.tabId !== ownTabId)) {
      return undefined;
    }

    return handle(registry, menu, guard, message);
  });
}

// The background may inject this script into a tab that already has it.
if (!window.juvinContentLoaded) {
  window.juvinContentLoaded = true;
  start();
}
