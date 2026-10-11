import type { Tabs } from "webextension-polyfill";
import { describe, expect, it } from "vitest";
import { EditorTabs } from "../src/background/editor-tabs";

function tabsWith(open: number[]): EditorTabs {
  const live = new Set(open);
  return new EditorTabs((tabId) => (live.has(tabId) ? Promise.resolve({ id: tabId } as Tabs.Tab) : Promise.reject(new Error("No tab"))));
}

describe("EditorTabs", () => {
  it("finds the tab that owns an editor", async () => {
    const tabs = tabsWith([3, 7]);
    tabs.remember("e-1", 7);

    expect((await tabs.find("e-1"))?.id).toBe(7);
  });

  it("returns nothing for an unknown editor rather than another tab", async () => {
    const tabs = tabsWith([3]);

    expect(await tabs.find("e-1")).toBeUndefined();
  });

  it("returns nothing once the owning tab is closed or forgotten", async () => {
    const tabs = tabsWith([3]);
    tabs.remember("e-1", 7);
    tabs.remember("e-2", 3);

    expect(await tabs.find("e-1")).toBeUndefined();
    tabs.forgetTab(3);
    expect(await tabs.find("e-2")).toBeUndefined();
  });
});
