import type { Tabs } from "webextension-polyfill";
import type { ContextErrorCode, ErrorPayload } from "../shared/protocol";

export interface TabSource {
  get(tabId: number): Promise<Tabs.Tab>;
  /** The active tab of the last focused window. */
  active(): Promise<Tabs.Tab | undefined>;
  /** Whether a browser window has focus, rather than another app. */
  isFocused(): Promise<boolean>;
}

export type RequestedTab = { ok: true; tab: Tabs.Tab & { id: number } } | { ok: false; error: ErrorPayload<ContextErrorCode> };

const NO_ACTIVE_TAB: RequestedTab = { ok: false, error: { code: "no-active-tab", message: "No browser tab is active." } };

const NOT_FOCUSED: RequestedTab = {
  ok: false,
  error: { code: "browser-not-focused", message: "Focus the browser with your draft and press the shortcut again." },
};

/**
 * How long a press waits for its context.request. The app drops a press that repeats the desktop
 * hotkey's (one key press seen by both), and its entry must not linger for a later request.
 */
export const PRESS_LIFETIME_MS = 2000;

/**
 * The tab active at the latest shortcut press. Only the latest is kept, so a press the app never asked
 * about (coalesced with the desktop hotkey) cannot hand its tab to the next press.
 */
export class ShortcutTabs {
  private pressed: { generation: number; tabId: number | undefined; at: number } | undefined;

  constructor(
    private readonly source: TabSource,
    private readonly now: () => number = Date.now,
  ) {}

  push(generation: number, tabId: number | undefined): void {
    this.pressed = { generation, tabId, at: this.now() };
  }

  /**
   * The tab a capture without an editor id reads: the pending shortcut's tab, else the active tab.
   * With requireFocus (the desktop hotkey) an unfocused browser answers browser-not-focused, never a stale tab.
   */
  async take(generation: number, requireFocus: boolean): Promise<RequestedTab> {
    const pressed = this.pressed;
    this.pressed = undefined;
    const pending = !requireFocus && pressed?.generation === generation && pressed.at >= this.now() - PRESS_LIFETIME_MS;

    let tab: Tabs.Tab | undefined;
    if (pending) {
      tab = await this.tabOf(pressed.tabId);
    } else if (requireFocus && !(await this.source.isFocused())) {
      return NOT_FOCUSED;
    } else {
      tab = await this.source.active();
    }

    return tab?.id === undefined ? NO_ACTIVE_TAB : { ok: true, tab: { ...tab, id: tab.id } };
  }

  private async tabOf(tabId: number | undefined): Promise<Tabs.Tab | undefined> {
    if (tabId === undefined) {
      return undefined;
    }

    try {
      return await this.source.get(tabId);
    } catch {
      return undefined;
    }
  }
}
