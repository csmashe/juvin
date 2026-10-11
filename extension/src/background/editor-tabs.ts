import type { Tabs } from "webextension-polyfill";

/** Which tab each captured editor lives in, so apply and a recapture go back to that tab and no other. */
export class EditorTabs {
  private readonly tabs = new Map<string, number>();

  constructor(private readonly getTab: (tabId: number) => Promise<Tabs.Tab>) {}

  remember(editorId: string, tabId: number): void {
    this.tabs.set(editorId, tabId);
  }

  /** The tab that owns the editor, or undefined when the editor is unknown or its tab is closed. */
  async find(editorId: string): Promise<(Tabs.Tab & { id: number }) | undefined> {
    const tabId = this.tabs.get(editorId);
    if (tabId === undefined) {
      return undefined;
    }

    try {
      return { ...(await this.getTab(tabId)), id: tabId };
    } catch {
      this.tabs.delete(editorId);
      return undefined;
    }
  }

  forgetTab(tabId: number): void {
    for (const [editorId, editorTab] of this.tabs) {
      if (editorTab === tabId) {
        this.tabs.delete(editorId);
      }
    }
  }
}
