import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Capture } from "../src/shared/protocol";
import type { ContentRequest } from "../src/shared/runtime-messages";

type Listener = (...args: never[]) => unknown;

class FakeEvent<T extends Listener> {
  readonly listeners: T[] = [];

  addListener(listener: T): void {
    this.listeners.push(listener);
  }

  fire(...args: Parameters<T>): unknown[] {
    return this.listeners.map((listener) => listener(...args));
  }
}

interface FakePort {
  posted: unknown[];
  postMessage: (message: unknown) => void;
  disconnect: () => void;
  onMessage: FakeEvent<(message: unknown) => void>;
  onDisconnect: FakeEvent<(port: FakePort) => void>;
}

interface Harness {
  ports: FakePort[];
  port: () => FakePort;
  /** Requests the content script received, in order. */
  content: ContentRequest[];
  /** How the content script answers; undefined means no content script. */
  answer: (request: ContentRequest) => unknown;
  /** How tabs.get answers; replace it to hold a lookup open. */
  getTab: (tabId: number) => Promise<{ id: number; windowId?: number }>;
  runtimeMessage: (message: unknown, sender?: { tab?: { id: number } }) => unknown;
  tabUpdated: FakeEvent<(tabId: number, changeInfo: { status?: string }) => void>;
  os: string;
  /** Windows the extension asked to focus, in order. */
  focused: number[];
  /** Tabs the extension asked to make active, in order. */
  activated: number[];
}

const TAB = 5;
const WINDOW = 3;

const CONFIG = { v: 1, type: "config", actions: [], adapters: ["test-page"], shortcut: "Ctrl+Shift+F", health: { mode: "local", modelReachable: null } };

const SLOTS = ["missing", "soften", "clearer", "shorter", "grammar"].map((id) => ({ id, title: id }));

const CAPTURE: Capture = {
  adapter: "test-page",
  pageUrl: "https://example.com/",
  capturedAt: "2026-10-09T14:03:21.512Z",
  editor: { id: "e-1", hash: "sha256:00" },
  selection: { text: "Hi team", start: 0, end: 7 },
  thread: { completeness: "entire-thread", collapsedCount: 0, messages: [] },
};

function fakePort(): FakePort {
  const port: FakePort = {
    posted: [],
    postMessage: (message) => port.posted.push(message),
    disconnect: () => undefined,
    onMessage: new FakeEvent(),
    onDisconnect: new FakeEvent(),
  };
  return port;
}

function defaultAnswer(request: ContentRequest): unknown {
  switch (request.kind) {
    case "juvin.capture":
      return { ok: true, capture: CAPTURE };
    case "juvin.apply":
    case "juvin.menu.show":
    case "juvin.menu.update":
    case "juvin.menu.close":
    case "juvin.menu.disconnected":
    case "juvin.menu.check":
    case "juvin.shortcut":
      return { ok: true };
  }
}

function fakeBrowser(h: Harness): unknown {
  const onMessage = new FakeEvent<(message: unknown, sender: unknown) => unknown>();
  h.runtimeMessage = (message, sender = { tab: { id: TAB } }) => onMessage.fire(message, sender)[0];
  const noop = { addListener: () => undefined };
  return {
    runtime: {
      id: "extension@juvin.app",
      connectNative: () => {
        const port = fakePort();
        h.ports.push(port);
        return port;
      },
      getManifest: () => ({ version: "0.1.0" }),
      getURL: (path: string) => `moz-extension://abc/${path}`,
      getPlatformInfo: () => Promise.resolve({ os: h.os }),
      sendMessage: () => Promise.reject(new Error("No receiver.")),
      onMessage,
      onStartup: noop,
      onInstalled: noop,
    },
    action: { setBadgeText: () => Promise.resolve(), setBadgeBackgroundColor: () => Promise.resolve() },
    tabs: {
      get: (tabId: number) => h.getTab(tabId),
      update: (tabId: number, info: { active?: boolean }) => {
        if (info.active) {
          h.activated.push(tabId);
        }

        return Promise.resolve({ id: tabId });
      },
      query: () => Promise.resolve([{ id: TAB }]),
      sendMessage: (_tabId: number, request: ContentRequest) => {
        h.content.push(request);
        const reply = h.answer(request);
        return reply === undefined ? Promise.reject(new Error("No content script.")) : Promise.resolve(reply);
      },
      onRemoved: noop,
      onUpdated: h.tabUpdated,
    },
    commands: { onCommand: noop },
    windows: {
      getLastFocused: () => Promise.resolve({ focused: true }),
      update: (windowId: number, info: { focused?: boolean }) => {
        if (info.focused) {
          h.focused.push(windowId);
        }

        return Promise.resolve({ id: windowId });
      },
    },
    scripting: { executeScript: () => Promise.reject(new Error("Not allowed.")) },
  };
}

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

function newHarness(): Harness {
  const h: Harness = {
    ports: [],
    port: () => h.ports[h.ports.length - 1]!,
    content: [],
    answer: defaultAnswer,
    getTab: (tabId) => Promise.resolve({ id: tabId, windowId: WINDOW }),
    runtimeMessage: () => undefined,
    tabUpdated: new FakeEvent(),
    os: "linux",
    focused: [],
    activated: [],
  };
  vi.doMock("webextension-polyfill", () => ({ default: fakeBrowser(h) }));
  return h;
}

async function connect(h: Harness): Promise<void> {
  const connecting = h.runtimeMessage({ kind: "juvin.connect" }, {});
  await settle();
  h.port().onMessage.fire(CONFIG);
  await connecting;
}

async function fromApp(h: Harness, message: Record<string, unknown>): Promise<void> {
  h.port().onMessage.fire({ v: 1, ...message });
  await settle();
}

function sentToApp(h: Harness, port = h.port()): unknown[] {
  return port.posted.filter((m) => (m as { type: string }).type !== "hello");
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

describe("background", () => {
  let h: Harness;

  async function openMenu(): Promise<void> {
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    await fromApp(h, { type: "menu.show", id: "m1", menuId: "menu-1", editorId: "e-1", slots: SLOTS, more: [], status: [] });
    h.port().posted.length = 0;
    h.content.length = 0;
  }

  beforeEach(async () => {
    vi.resetModules();
    vi.spyOn(console, "info").mockImplementation(() => undefined);
    vi.spyOn(console, "warn").mockImplementation(() => undefined);
    h = newHarness();
    await import("../src/background/index");
  });

  afterEach(() => {
    vi.doUnmock("webextension-polyfill");
    vi.restoreAllMocks();
  });

  it("sends a menu update to the tab once", async () => {
    await openMenu();
    await fromApp(h, { type: "menu.update", menuId: "menu-1", state: "running", action: "soften" });

    expect(h.content.filter((r) => r.kind === "juvin.menu.update")).toHaveLength(1);
    expect(sentToApp(h)).toEqual([]);
  });

  it("closes the menu in the app when the page no longer has it", async () => {
    await openMenu();
    h.answer = (request) => (request.kind === "juvin.menu.update" ? { ok: false } : defaultAnswer(request));
    await fromApp(h, { type: "menu.update", menuId: "menu-1", state: "running", action: "soften" });
    await fromApp(h, { type: "menu.update", menuId: "menu-1", state: "answer", text: "x" });

    expect(sentToApp(h)).toEqual([{ v: 1, type: "menu.close", menuId: "menu-1" }]);
    expect(h.content.filter((r) => r.kind === "juvin.menu.update")).toHaveLength(1);
  });

  it("does not close a newer menu with the same id when an old update fails late", async () => {
    await openMenu();
    let finishOld: (value: unknown) => void = () => undefined;
    h.answer = (request) =>
      request.kind === "juvin.menu.update" ? new Promise((resolve) => (finishOld = resolve)) : defaultAnswer(request);
    const oldUpdate = fromApp(h, { type: "menu.update", menuId: "menu-1", state: "running", action: "soften" });
    await settle();

    // The app restarts its menu counter, so the next menu reuses the id.
    await fromApp(h, { type: "menu.close", menuId: "menu-1" });
    h.answer = defaultAnswer;
    await openMenu();
    finishOld({ ok: false });
    await oldUpdate;
    await settle();

    expect(sentToApp(h).filter((m) => (m as { type: string }).type === "menu.close")).toEqual([]);
  });

  it("closes the menu in the app when its tab cannot be reached", async () => {
    await openMenu();
    h.answer = () => undefined;
    await fromApp(h, { type: "menu.update", menuId: "menu-1", state: "running", action: "soften" });

    expect(sentToApp(h)).toEqual([{ v: 1, type: "menu.close", menuId: "menu-1" }]);
  });

  it("closes the menu and forgets the tab's editors when the tab reloads", async () => {
    await openMenu();
    h.answer = (request) => (request.kind === "juvin.menu.check" ? { ok: false } : defaultAnswer(request));
    h.tabUpdated.fire(TAB, { status: "loading" });
    await settle();
    expect(sentToApp(h)).toEqual([]);

    h.tabUpdated.fire(TAB, { status: "complete" });
    await settle();
    expect(sentToApp(h)).toEqual([{ v: 1, type: "menu.close", menuId: "menu-1" }]);

    await fromApp(h, { type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 7, text: "Hello" });
    expect(sentToApp(h).at(-1)).toMatchObject({ type: "apply.response", id: "a1", ok: false, error: { code: "editor-gone" } });
    expect(h.content.some((r) => r.kind === "juvin.apply")).toBe(false);
  });

  it("keeps the menu when the page still draws it after loading", async () => {
    await openMenu();
    h.tabUpdated.fire(TAB, { status: "complete" });
    await settle();
    await fromApp(h, { type: "menu.update", menuId: "menu-1", state: "running", action: "soften" });

    expect(sentToApp(h)).toEqual([]);
    expect(h.content.map((r) => r.kind)).toEqual(["juvin.menu.check", "juvin.menu.update"]);
  });

  it("drops an apply whose connection closed while its tab was looked up", async () => {
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    const lookup = deferred<{ id: number }>();
    h.getTab = () => lookup.promise;
    await fromApp(h, { type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 7, text: "Hello" });
    const first = h.port();
    first.onDisconnect.fire(first);
    await connect(h);
    first.posted.length = 0;

    lookup.resolve({ id: TAB });
    await settle();

    expect(h.content.some((r) => r.kind === "juvin.apply")).toBe(false);
    expect(sentToApp(h, first)).toEqual([]);
    expect(sentToApp(h)).toEqual([]);
  });

  const APPLY = { type: "apply.request", id: "a1", editorId: "e-1", expectedHash: "sha256:00", start: 0, end: 7, text: "Hello" };

  it("brings the draft's tab and window to the front after a panel apply on macOS", async () => {
    h.os = "mac";
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    await fromApp(h, APPLY);

    expect(sentToApp(h).at(-1)).toMatchObject({ type: "apply.response", id: "a1", ok: true });
    expect(h.activated).toEqual([TAB]);
    expect(h.focused).toEqual([WINDOW]);
  });

  it("sends the app's shortcut to the tabs on macOS as the connection gets and loses config", async () => {
    h.os = "mac";
    await connect(h);
    await settle();
    expect(h.content.filter((r) => r.kind === "juvin.shortcut")).toEqual([{ kind: "juvin.shortcut", shortcut: "Ctrl+Shift+F", shortcutKey: undefined }]);

    h.port().onDisconnect.fire(h.port());
    await settle();
    expect(h.content.filter((r) => r.kind === "juvin.shortcut").at(-1)).toEqual({ kind: "juvin.shortcut", shortcut: undefined, shortcutKey: undefined });
  });

  it("sends no shortcut to the tabs off macOS", async () => {
    await connect(h);
    await settle();

    expect(h.content.some((r) => r.kind === "juvin.shortcut")).toBe(false);
  });

  it("leaves tab and window focus alone off macOS", async () => {
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    await fromApp(h, APPLY);

    expect(sentToApp(h).at(-1)).toMatchObject({ type: "apply.response", id: "a1", ok: true });
    expect(h.activated).toEqual([]);
    expect(h.focused).toEqual([]);
  });

  it("leaves tab and window focus alone after a failed apply", async () => {
    h.os = "mac";
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    h.answer = (request) => (request.kind === "juvin.apply" ? { ok: false, error: { code: "draft-changed", message: "x" } } : defaultAnswer(request));
    await fromApp(h, APPLY);

    expect(h.activated).toEqual([]);
    expect(h.focused).toEqual([]);
  });

  it("leaves tab and window focus alone for an apply from the in-page menu", async () => {
    h.os = "mac";
    await openMenu();
    await fromApp(h, APPLY);

    expect(sentToApp(h).at(-1)).toMatchObject({ type: "apply.response", id: "a1", ok: true });
    expect(h.activated).toEqual([]);
    expect(h.focused).toEqual([]);
  });

  it("tells the menu the connection is gone and refuses its choices", async () => {
    await openMenu();
    h.port().onDisconnect.fire(h.port());
    await settle();

    expect(h.content).toEqual([{ kind: "juvin.menu.disconnected", menuId: "menu-1" }]);
    await expect(h.runtimeMessage({ kind: "juvin.menu.choose", menuId: "menu-1", action: "soften" })).rejects.toThrow();
  });

  it("delivers a choice for the open menu", async () => {
    await openMenu();

    expect(h.runtimeMessage({ kind: "juvin.menu.choose", menuId: "menu-1", action: "soften" })).toBeUndefined();
    expect(sentToApp(h)).toEqual([{ v: 1, type: "menu.choose", menuId: "menu-1", action: "soften" }]);
  });

  it("does not show a menu whose connection closed while its tab was looked up", async () => {
    await connect(h);
    await fromApp(h, { type: "context.request", id: "c1" });
    const lookup = deferred<{ id: number }>();
    h.getTab = () => lookup.promise;
    await fromApp(h, { type: "menu.show", id: "m1", menuId: "menu-1", editorId: "e-1", slots: SLOTS, more: [], status: [] });
    h.port().onDisconnect.fire(h.port());

    lookup.resolve({ id: TAB });
    await settle();

    expect(h.content.some((r) => r.kind === "juvin.menu.show")).toBe(false);
  });
});

describe("Connection", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.spyOn(console, "info").mockImplementation(() => undefined);
    vi.spyOn(console, "warn").mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.doUnmock("webextension-polyfill");
    vi.restoreAllMocks();
  });

  it("fails a ping at once when the port closed as it connected", async () => {
    const h = newHarness();
    const { Connection } = await import("../src/background/connection");
    const connection = new Connection(() => undefined);
    const pinging = connection.ping();
    await settle();
    const port = h.port();
    port.onMessage.fire(CONFIG);
    port.onDisconnect.fire(port);

    await expect(pinging).rejects.toThrow("The connection to Juvin closed.");
  });

  it("reports a send without a connection and a drop to its listener", async () => {
    const h = newHarness();
    const { Connection } = await import("../src/background/connection");
    const dropped = vi.fn();
    const connection = new Connection(() => undefined, dropped);
    expect(connection.send({ v: 1, type: "shortcut.pressed" })).toBe(false);

    const connecting = connection.ensureConnected();
    await settle();
    h.port().onMessage.fire(CONFIG);
    await connecting;
    expect(connection.send({ v: 1, type: "shortcut.pressed" })).toBe(true);

    h.port().onDisconnect.fire(h.port());
    expect(dropped).toHaveBeenCalledOnce();
  });
});
