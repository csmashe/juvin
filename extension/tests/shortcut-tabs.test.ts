import type { Tabs } from "webextension-polyfill";
import { describe, expect, it } from "vitest";
import { PRESS_LIFETIME_MS, ShortcutTabs } from "../src/background/shortcut-tabs";

function shortcutTabs(options: { open?: number[]; active?: number; focused?: boolean; clock?: { now: number } }): ShortcutTabs {
  const open = new Set(options.open ?? []);
  const clock = options.clock ?? { now: 0 };
  return new ShortcutTabs(
    {
      get: (tabId) => (open.has(tabId) ? Promise.resolve({ id: tabId } as Tabs.Tab) : Promise.reject(new Error("No tab"))),
      active: () => Promise.resolve(options.active === undefined ? undefined : ({ id: options.active } as Tabs.Tab)),
      isFocused: () => Promise.resolve(options.focused ?? true),
    },
    () => clock.now,
  );
}

describe("ShortcutTabs", () => {
  it("reads the tab of the latest shortcut press, once", async () => {
    const tabs = shortcutTabs({ open: [3, 7], active: 9 });
    tabs.push(1, 3);
    tabs.push(1, 7);

    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 7 } });
    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 9 } });
  });

  it("does not hand a press the app coalesced to the next press on another tab", async () => {
    const clock = { now: 1000 };
    const tabs = shortcutTabs({ open: [3, 7], active: 7, clock });
    // Desktop hotkey first: its request reads the active tab, then the browser command queues the same press.
    expect(await tabs.take(1, true)).toEqual({ ok: true, tab: { id: 7 } });
    tabs.push(1, 7);
    // The app coalesced that press. A second later the user is on tab 3 and the browser command fires first.
    clock.now += 1000;
    tabs.push(1, 3);

    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 3 } });
  });

  it("drops presses from an earlier connection", async () => {
    const tabs = shortcutTabs({ open: [3], active: 9 });
    tabs.push(1, 3);

    expect(await tabs.take(2, false)).toEqual({ ok: true, tab: { id: 9 } });
  });

  it("answers no-active-tab when the shortcut's tab is gone", async () => {
    const tabs = shortcutTabs({ active: 9 });
    tabs.push(1, 3);

    const result = await tabs.take(1, false);

    expect(!result.ok && result.error.code).toBe("no-active-tab");
  });

  it("answers browser-not-focused for the desktop hotkey when no window has focus", async () => {
    const tabs = shortcutTabs({ active: 9, focused: false });

    const result = await tabs.take(1, true);

    expect(result).toEqual({
      ok: false,
      error: { code: "browser-not-focused", message: "Focus the browser with your draft and press the shortcut again." },
    });
  });

  it("reads the active tab for the desktop hotkey while the browser has focus", async () => {
    expect(await shortcutTabs({ active: 9, focused: true }).take(1, true)).toEqual({ ok: true, tab: { id: 9 } });
  });

  it("drops pending presses for the desktop hotkey and reads the focused window instead", async () => {
    const tabs = shortcutTabs({ open: [3], active: 9 });
    tabs.push(1, 3);

    expect(await tabs.take(1, true)).toEqual({ ok: true, tab: { id: 9 } });
    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 9 } });
  });

  it("forgets a press nobody asked for within its lifetime", async () => {
    const clock = { now: 1000 };
    const tabs = shortcutTabs({ open: [3, 4], active: 9, clock });
    tabs.push(1, 3);
    clock.now += PRESS_LIFETIME_MS;
    tabs.push(1, 4);
    clock.now += 1;

    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 4 } });
    expect(await tabs.take(1, false)).toEqual({ ok: true, tab: { id: 9 } });
  });

  it("ignores focus for a request that does not require it", async () => {
    expect(await shortcutTabs({ active: 9, focused: false }).take(1, false)).toEqual({ ok: true, tab: { id: 9 } });
  });
});
