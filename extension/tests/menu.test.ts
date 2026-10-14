// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MenuUpdateMessage } from "../src/shared/protocol";
import type { MenuNotice, MenuShowRequest } from "../src/shared/runtime-messages";
import { EditorRegistry } from "../src/content/editor-registry";
import { MENU_STRINGS, MenuRenderer, placeBeside } from "../src/content/menu";

const SLOTS = [
  { id: "missing", title: "What am I missing?" },
  { id: "soften", title: "Soften" },
  { id: "clearer", title: "Make clearer" },
  { id: "shorter", title: "Make shorter" },
  { id: "grammar", title: "Fix grammar" },
];

interface Harness {
  renderer: MenuRenderer;
  editor: HTMLElement;
  editorId: string;
  notices: MenuNotice[];
  root: () => ShadowRoot;
  roots: ShadowRoot[];
}

const renderers: MenuRenderer[] = [];

function setup(isMac = false): Harness {
  document.body.innerHTML = `<div id="editor" contenteditable="true">hello</div><p id="outside">page</p>`;
  const editor = document.getElementById("editor")!;
  const registry = new EditorRegistry();
  const editorId = registry.idFor(editor);
  const notices: MenuNotice[] = [];
  const roots: ShadowRoot[] = [];
  const renderer = new MenuRenderer(registry, (notice) => void notices.push(notice), { onShadowRoot: (root) => roots.push(root), isMac });
  renderers.push(renderer);
  return { renderer, editor, editorId, notices, roots, root: () => roots[roots.length - 1]! };
}

function request(h: Harness, menuId = "menu-1"): MenuShowRequest {
  return { kind: "juvin.menu.show", menuId, editorId: h.editorId, slots: SLOTS, more: [], status: ["6 messages", "Entire thread", "Local: qwen3"] };
}

function key(key: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
  document.getElementById("editor")!.dispatchEvent(event);
  return event;
}

function update(update: Record<string, unknown>): MenuUpdateMessage {
  return { v: 1, type: "menu.update", menuId: "menu-1", ...update } as MenuUpdateMessage;
}

function hosts(): Element[] {
  return Array.from(document.querySelectorAll("juvin-menu"));
}

function buttonByText(root: ShadowRoot, text: string): HTMLButtonElement | undefined {
  return Array.from(root.querySelectorAll<HTMLButtonElement>(".actions button")).find((b) => b.textContent === text);
}

describe("MenuRenderer", () => {
  let h: Harness;

  beforeEach(() => {
    vi.useFakeTimers();
    h = setup();
  });

  afterEach(() => {
    for (const renderer of renderers.splice(0)) {
      renderer.close("menu-1");
      renderer.close("menu-2");
    }

    vi.useRealTimers();
  });

  it("draws the menu in a closed shadow root", () => {
    expect(h.renderer.show(request(h))).toEqual({ ok: true });
    const [host] = hosts();
    expect(host).toBeDefined();
    expect(host!.shadowRoot).toBeNull();
    const root = h.root();
    expect(root.mode).toBe("closed");
    expect(root.querySelectorAll(".bubble")).toHaveLength(5);
    expect(Array.from(root.querySelectorAll(".bubble .key"), (k) => k.textContent)).toEqual(["1", "2", "3", "4", "5"]);
    expect(Array.from(root.querySelectorAll(".chip"), (c) => c.textContent)).toEqual(["6 messages", "Entire thread", "Local: qwen3"]);
    expect(root.querySelector("button.mark")).not.toBeNull();
    expect(root.querySelector("style")?.textContent).toContain("#F5A623");
  });

  it("answers editor-gone for an unknown editor", () => {
    const result = h.renderer.show({ ...request(h), editorId: "e-unknown" });
    expect(result).toMatchObject({ ok: false, error: { code: "editor-gone" } });
    expect(hosts()).toHaveLength(0);
  });

  it("does not take focus from the editor", () => {
    h.editor.focus();
    h.renderer.show(request(h));
    expect(document.activeElement).toBe(h.editor);
    for (const bubble of h.root().querySelectorAll<HTMLButtonElement>(".bubble")) {
      expect(bubble.tabIndex).toBe(-1);
    }
  });

  it("sends the matching choice for keys 1 to 5", () => {
    SLOTS.forEach((slot, index) => {
      h.renderer.show(request(h));
      const event = key(String(index + 1));
      expect(event.defaultPrevented).toBe(true);
      expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.choose", menuId: "menu-1", action: slot.id });
    });
  });

  it("sends a choice for a clicked bubble", () => {
    h.renderer.show(request(h));
    h.root().querySelectorAll<HTMLButtonElement>(".bubble")[2]!.click();
    expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "clearer" }]);
  });

  it("closes on Escape and reports it", () => {
    h.renderer.show(request(h));
    expect(key("Escape").defaultPrevented).toBe(true);
    expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
    expect(hosts()).toHaveLength(0);
  });

  it("lets keys with Ctrl held and other keys through", () => {
    h.renderer.show(request(h));
    expect(key("1", { ctrlKey: true }).defaultPrevented).toBe(false);
    expect(key("z", { ctrlKey: true }).defaultPrevented).toBe(false);
    expect(key("a").defaultPrevented).toBe(false);
    expect(key("Enter").defaultPrevented).toBe(false);
    expect(h.notices).toEqual([]);
  });

  it("moves a drawn focus with the arrow keys and picks it with Enter", () => {
    h.renderer.show(request(h));
    expect(key("ArrowUp").defaultPrevented).toBe(true);
    const focused = () => Array.from(h.root().querySelectorAll(".bubble.focused"), (b) => (b as HTMLElement).dataset.action);
    expect(focused()).toEqual(["clearer"]);
    key("ArrowRight");
    expect(focused()).toEqual(["shorter"]);
    key("ArrowDown");
    expect(focused()).toEqual(["grammar"]);
    key("ArrowLeft");
    expect(focused()).toEqual(["missing"]);
    expect(document.activeElement).not.toBe(h.root().host);
    key("Enter");
    expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "missing" }]);
  });

  it("closes on a click outside and reports it", () => {
    h.renderer.show(request(h));
    document.getElementById("outside")!.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true, composed: true }));
    expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
    expect(hosts()).toHaveLength(0);
  });

  it("stays open on a click inside", () => {
    h.renderer.show(request(h));
    h.root().querySelector(".chip")!.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true, composed: true }));
    expect(h.notices).toEqual([]);
    expect(hosts()).toHaveLength(1);
  });

  it("replaces an open menu without reporting it closed", () => {
    h.renderer.show(request(h, "menu-1"));
    h.renderer.show(request(h, "menu-2"));
    expect(hosts()).toHaveLength(1);
    expect(h.notices).toEqual([]);
    key("2");
    expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-2", action: "soften" }]);
  });

  it("ignores an update for another menu", () => {
    h.renderer.show(request(h));
    h.renderer.update({ ...update({ state: "running", action: "soften" }), menuId: "menu-9" });
    expect(h.root().querySelector(".ring.running")).toBeNull();
  });

  it("shows progress on the running bubble, dims the others, and blocks choices", () => {
    h.renderer.show(request(h));
    key("2");
    h.renderer.update(update({ state: "running", action: "soften" }));
    const root = h.root();
    expect(root.querySelector(".ring.running")).not.toBeNull();
    expect(root.querySelector(".bubble.active")?.getAttribute("data-action")).toBe("soften");
    expect(root.querySelector(".bubble.active .spinner")).not.toBeNull();
    key("3");
    root.querySelectorAll<HTMLButtonElement>(".bubble")[0]!.click();
    expect(h.notices).toHaveLength(1);
    key("Escape");
    expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.closed", menuId: "menu-1" });
  });

  it("removes the menu on applied and shows a note that fades", () => {
    h.renderer.show(request(h));
    const menuRoot = h.root();
    h.renderer.update(update({ state: "applied" }));
    expect(menuRoot.host.isConnected).toBe(false);
    const noteRoot = h.root();
    expect(noteRoot).not.toBe(menuRoot);
    const note = noteRoot.querySelector(".note")!;
    expect(note.textContent).toBe(MENU_STRINGS.appliedNote("Ctrl+Z"));
    expect(hosts()).toHaveLength(1);
    vi.advanceTimersByTime(3700);
    expect(note.classList.contains("fading")).toBe(true);
    vi.advanceTimersByTime(400);
    expect(hosts()).toHaveLength(0);
    expect(h.notices).toEqual([]);
  });

  it("names Cmd+Z in the note on macOS", () => {
    h = setup(true);
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "applied" }));
    expect(h.root().querySelector(".note")!.textContent).toBe(MENU_STRINGS.appliedNote("Cmd+Z"));
  });

  it("offers Copy and Recapture when the draft changed", async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "running", action: "soften" }));
    h.renderer.update(update({ state: "draft-changed", text: "the rewrite" }));
    const root = h.root();
    expect(root.querySelector(".ring.running")).toBeNull();
    expect(root.querySelector(".card")!.hasAttribute("hidden")).toBe(false);
    expect(root.querySelector(".card .result")!.textContent).toBe("the rewrite");

    const copy = buttonByText(root, MENU_STRINGS.copy)!;
    copy.click();
    await vi.waitFor(() => expect(copy.textContent).toBe(MENU_STRINGS.copied));
    expect(writeText).toHaveBeenCalledWith("the rewrite");
    vi.advanceTimersByTime(1600);
    expect(copy.textContent).toBe(MENU_STRINGS.copy);

    buttonByText(root, MENU_STRINGS.recapture)!.click();
    expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.choose", menuId: "menu-1", action: "recapture" });
  });

  it("retries the last action from an error card", () => {
    h.renderer.show(request(h));
    key("4");
    h.renderer.update(update({ state: "running", action: "shorter" }));
    h.renderer.update(update({ state: "error", text: "The model did not answer in time.", canRetry: true }));
    const root = h.root();
    expect(root.querySelector(".card .message")!.textContent).toBe("The model did not answer in time.");
    expect(buttonByText(root, MENU_STRINGS.copy)).toBeUndefined();
    buttonByText(root, MENU_STRINGS.tryAgain)!.click();
    expect(h.notices).toEqual([
      { kind: "juvin.menu.choose", menuId: "menu-1", action: "shorter" },
      { kind: "juvin.menu.choose", menuId: "menu-1", action: "shorter" },
    ]);
  });

  it("offers Copy of an unapplied result and no Try again without canRetry", () => {
    h.renderer.show(request(h));
    key("4");
    h.renderer.update(update({ state: "error", text: "The editor is gone.", canRetry: false, result: "kept rewrite" }));
    const root = h.root();
    expect(root.querySelector(".card .result")!.textContent).toBe("kept rewrite");
    expect(buttonByText(root, MENU_STRINGS.copy)).toBeDefined();
    expect(buttonByText(root, MENU_STRINGS.tryAgain)).toBeUndefined();
  });

  it("shows an answer with Copy and stays open", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- Missing the deadline." }));
    const root = h.root();
    expect(root.querySelector(".card .message")!.textContent).toBe("- Missing the deadline.");
    expect(buttonByText(root, MENU_STRINGS.copy)).toBeDefined();
    expect(hosts()).toHaveLength(1);
  });

  it("removes the menu and its listeners on close", () => {
    h.renderer.show(request(h));
    h.renderer.close("menu-other");
    expect(hosts()).toHaveLength(1);
    h.renderer.close("menu-1");
    expect(hosts()).toHaveLength(0);
    expect(key("1").defaultPrevented).toBe(false);
    document.body.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
    expect(h.notices).toEqual([]);
  });

  it("closes and reports it when the editor leaves the page", () => {
    h.renderer.show(request(h));
    h.editor.remove();
    vi.advanceTimersByTime(600);
    expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
    expect(hosts()).toHaveLength(0);
  });

  it("keeps a card with a result when the editor leaves the page", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "running", action: "soften" }));
    h.renderer.update(update({ state: "error", text: "Editor gone.", canRetry: false, result: "the rewrite" }));
    h.editor.remove();
    vi.advanceTimersByTime(600);
    expect(h.notices).toEqual([]);
    expect(h.root().querySelector(".card .result")!.textContent).toBe("the rewrite");

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "2", bubbles: true }));
    h.root().querySelectorAll<HTMLButtonElement>(".bubble")[2]!.click();
    expect(h.notices).toEqual([]);
    expect(h.root().querySelector(".ring.gone")).not.toBeNull();

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
    expect(hosts()).toHaveLength(0);
  });

  it("closes when the editor leaves after a result was replaced by a new run", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "2", bubbles: true }));
    h.renderer.update(update({ state: "running", action: "soften" }));
    h.editor.remove();
    vi.advanceTimersByTime(600);
    expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.closed", menuId: "menu-1" });
    expect(hosts()).toHaveLength(0);
  });

  it("keeps a retained card inside the viewport after the editor is gone", () => {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    h.editor.remove();
    vi.advanceTimersByTime(600);
    const width = window.innerWidth;
    Object.defineProperty(window, "innerWidth", { value: 200, configurable: true });
    window.dispatchEvent(new Event("resize"));
    frames.forEach((frame) => frame(0));
    const host = hosts()[0] as HTMLElement;
    expect(parseInt(host.style.left, 10)).toBeLessThanOrEqual(200);
    Object.defineProperty(window, "innerWidth", { value: width, configurable: true });
    vi.unstubAllGlobals();
  });

  it("ignores a second pick until the app answers, so Try again repeats the first", () => {
    h.renderer.show(request(h));
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "2", bubbles: true }));
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "3", bubbles: true }));
    expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "soften" }]);
    h.renderer.update(update({ state: "running", action: "soften" }));
    h.renderer.update(update({ state: "error", text: "Failed.", canRetry: true }));
    buttonByText(h.root(), MENU_STRINGS.tryAgain)!.click();
    expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.choose", menuId: "menu-1", action: "soften" });
  });

  it("accepts another pick when the choice could not be delivered", async () => {
    const registry = new EditorRegistry();
    const editorId = registry.idFor(h.editor);
    const sent: string[] = [];
    const renderer = new MenuRenderer(registry, (notice) => {
      sent.push(notice.kind === "juvin.menu.choose" ? notice.action : notice.kind);
      return Promise.reject(new Error("no receiver"));
    });
    renderers.push(renderer);
    renderer.show({ ...request(h), editorId });
    key("2");
    await Promise.resolve();
    await Promise.resolve();
    key("3");
    expect(sent).toEqual(["soften", "clearer"]);
  });

  it("mounts inside a modal dialog that holds the editor", () => {
    const dialog = document.createElement("dialog");
    dialog.append(h.editor);
    document.body.append(dialog);
    vi.spyOn(h.editor, "closest").mockImplementation(((selector: string) => (selector === "dialog:modal" ? dialog : null)) as typeof h.editor.closest);
    expect(h.renderer.show(request(h))).toEqual({ ok: true });
    expect(dialog.querySelector("juvin-menu")).not.toBeNull();
    h.renderer.update(update({ state: "applied" }));
    expect(dialog.querySelectorAll("juvin-menu")).toHaveLength(1);
    expect(h.root().querySelector(".note")).not.toBeNull();
  });

  it("copies with the legacy command when the clipboard API is missing", async () => {
    Object.defineProperty(navigator, "clipboard", { value: undefined, configurable: true });
    const execCommand = vi.fn(() => true);
    Object.defineProperty(document, "execCommand", { value: execCommand, configurable: true });
    h.editor.tabIndex = 0;
    h.editor.focus();
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    const copy = buttonByText(h.root(), MENU_STRINGS.copy)!;
    copy.click();
    await vi.waitFor(() => expect(copy.textContent).toBe(MENU_STRINGS.copied));
    expect(execCommand).toHaveBeenCalledWith("copy");
    expect(h.root().querySelector("textarea")).toBeNull();
    expect(document.activeElement).toBe(h.editor);
  });

  it("repositions on scroll once per frame", () => {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
    h.renderer.show(request(h));
    const host = hosts()[0] as HTMLElement;
    let top = 100;
    h.editor.getBoundingClientRect = () => ({ left: 10, top, right: 210, bottom: top + 100 }) as DOMRect;
    window.dispatchEvent(new Event("scroll"));
    window.dispatchEvent(new Event("scroll"));
    expect(frames).toHaveLength(1);
    top = 50;
    frames[0]!(0);
    expect(host.style.left).toBe("222px");
    expect(host.style.top).toBe("50px");
    vi.unstubAllGlobals();
  });

  it("clears the note when a new menu opens", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "applied" }));
    h.renderer.show(request(h, "menu-2"));
    expect(hosts()).toHaveLength(1);
    expect(h.root().querySelector(".note")).toBeNull();
  });
});

describe("placeBeside", () => {
  const viewport = { width: 1200, height: 800 };
  const size = { width: 320, height: 300 };

  it("goes to the right when it fits", () => {
    expect(placeBeside({ left: 100, top: 200, right: 600, bottom: 400 }, size, viewport)).toEqual({ left: 612, top: 200 });
  });

  it("goes above when the right side is full", () => {
    expect(placeBeside({ left: 700, top: 400, right: 1150, bottom: 700 }, size, viewport)).toEqual({ left: 700, top: 88 });
  });

  it("goes below when neither the right nor above fits", () => {
    expect(placeBeside({ left: 100, top: 100, right: 1100, bottom: 300 }, size, viewport)).toEqual({ left: 100, top: 312 });
  });

  it("stays inside the viewport", () => {
    expect(placeBeside({ left: 0, top: 0, right: 1200, bottom: 800 }, size, viewport)).toEqual({ left: 872, top: 8 });
  });
});
