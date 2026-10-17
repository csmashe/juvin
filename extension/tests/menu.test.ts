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
    expect(root.querySelector(".bubble .key")).toBeNull();
    expect(root.querySelector(".status")).toBeNull();
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
    h.root().querySelector(".bubble .title")!.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true, composed: true }));
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

  it("reports whether it still has the menu an update or a check names", () => {
    h.renderer.show(request(h));
    expect(h.renderer.has("menu-1")).toBe(true);
    expect(h.renderer.update(update({ state: "running", action: "soften" }))).toBe(true);
    h.renderer.close("menu-1");
    expect(h.renderer.has("menu-1")).toBe(false);
    expect(h.renderer.update(update({ state: "running", action: "soften" }))).toBe(false);
  });

  it("shows a lost connection during a run without Try again", () => {
    h.renderer.show(request(h));
    key("4");
    h.renderer.update(update({ state: "error", text: "Failed.", canRetry: true, result: "kept rewrite" }));
    h.renderer.update(update({ state: "running", action: "shorter" }));

    expect(h.renderer.disconnected("menu-other")).toBe(false);
    expect(h.renderer.disconnected("menu-1")).toBe(true);

    const root = h.root();
    expect(root.querySelector(".card .message")!.textContent).toBe(MENU_STRINGS.disconnected);
    expect(root.querySelector(".ring")!.classList.contains("running")).toBe(false);
    expect(buttonByText(root, MENU_STRINGS.tryAgain)).toBeUndefined();
    expect(buttonByText(root, MENU_STRINGS.copy)).toBeUndefined();
  });

  it("keeps a standing answer to copy when the connection is lost", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    h.renderer.disconnected("menu-1");
    const root = h.root();
    expect(root.querySelector(".card .message")!.textContent).toBe(MENU_STRINGS.disconnected);
    expect(root.querySelector(".card .result")!.textContent).toBe("- A point");
    expect(buttonByText(root, MENU_STRINGS.copy)).toBeDefined();
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

  it("closes from the card's close button and reports it", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    const close = h.root().querySelector<HTMLButtonElement>(".card .close")!;
    expect(close.getAttribute("aria-label")).toBe(MENU_STRINGS.close);
    close.click();
    expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
    expect(hosts()).toHaveLength(0);
  });

  it("removes the menu and its listeners on close", () => {
    h.renderer.show(request(h));
    expect(h.renderer.close("menu-other")).toBe(false);
    expect(hosts()).toHaveLength(1);
    expect(h.renderer.close("menu-1")).toBe(true);
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

  describe("Ask for a change", () => {
    const mark = () => h.root().querySelector<HTMLButtonElement>("button.mark")!;
    const input = () => h.root().querySelector<HTMLInputElement>(".card input")!;
    const typeKey = (target: Element, key: string) => {
      const event = new KeyboardEvent("keydown", { key, bubbles: true, composed: true, cancelable: true });
      target.dispatchEvent(event);
      return event;
    };

    it("opens a text box on the mark and focuses it", () => {
      h.renderer.show(request(h));
      mark().click();
      expect(h.root().querySelector(".card")!.hasAttribute("hidden")).toBe(false);
      expect(input().placeholder).toBe(MENU_STRINGS.askPlaceholder);
      expect(input().getAttribute("aria-label")).toBe(MENU_STRINGS.askPlaceholder);
      expect(h.root().activeElement).toBe(input());
      expect(buttonByText(h.root(), MENU_STRINGS.run)!.className).toBe("primary");
    });

    it("keeps the typed text when the mark is clicked again", () => {
      h.renderer.show(request(h));
      mark().click();
      input().value = "less formal";
      mark().click();
      expect(h.root().querySelectorAll(".card input")).toHaveLength(1);
      expect(input().value).toBe("less formal");
    });

    it("leaves digits, arrows, and Enter to the box and still closes on Escape", () => {
      h.renderer.show(request(h));
      mark().click();
      expect(typeKey(input(), "1").defaultPrevented).toBe(false);
      expect(typeKey(input(), "ArrowDown").defaultPrevented).toBe(false);
      expect(h.root().querySelector(".focused")).toBeNull();
      typeKey(input(), "Enter");
      expect(h.notices).toEqual([]);
      expect(typeKey(input(), "Escape").defaultPrevented).toBe(true);
      expect(h.notices).toEqual([{ kind: "juvin.menu.closed", menuId: "menu-1" }]);
      expect(hosts()).toHaveLength(0);
    });

    it("runs the trimmed instruction from Enter or Run and ignores an empty one", () => {
      h.renderer.show(request(h));
      mark().click();
      input().value = "   ";
      buttonByText(h.root(), MENU_STRINGS.run)!.click();
      expect(h.notices).toEqual([]);
      input().value = "  Make it less formal.  ";
      typeKey(input(), "Enter");
      expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "custom", instruction: "Make it less formal." }]);
    });

    it("keeps typed keys from the page's capture-phase shortcuts", () => {
      const seen: string[] = [];
      const shortcut = (event: Event) => seen.push(`${event.type}:${(event as KeyboardEvent).key}`);
      for (const type of ["keydown", "keypress", "keyup"]) {
        document.addEventListener(type, shortcut, { capture: true });
      }

      try {
        h.renderer.show(request(h));
        mark().click();
        for (const type of ["keydown", "keypress", "keyup"]) {
          input().dispatchEvent(new KeyboardEvent(type, { key: "e", bubbles: true, composed: true, cancelable: true }));
        }

        input().value = "Shorter.";
        typeKey(input(), "Enter");
        expect(seen).toEqual([]);
        expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "custom", instruction: "Shorter." }]);
        expect(key("2").defaultPrevented).toBe(true);
        expect(seen).toEqual([]);
      } finally {
        for (const type of ["keydown", "keypress", "keyup"]) {
          document.removeEventListener(type, shortcut, { capture: true });
        }
      }
    });

    it("replaces another card with the Ask card", () => {
      h.renderer.show(request(h));
      h.renderer.update(update({ state: "error", text: "Failed.", canRetry: false }));
      mark().click();
      expect(h.root().querySelector(".card .message")).toBeNull();
      expect(input()).not.toBeNull();
    });

    it("does nothing while a run is in progress", () => {
      h.renderer.show(request(h));
      h.renderer.update(update({ state: "running", action: "soften" }));
      mark().click();
      expect(h.root().querySelector(".card")!.hasAttribute("hidden")).toBe(true);
    });

    it("asks follow-ups under an answer and replaces it with the next answer", () => {
      h.renderer.show(request(h));
      key("1");
      h.renderer.update(update({ state: "running", action: "missing" }));
      h.renderer.update(update({ state: "answer", text: "- The deadline.\n- The budget." }));
      const root = h.root();
      expect(root.querySelector(".card .message")!.textContent).toBe("- The deadline.\n- The budget.");
      expect(input().placeholder).toBe(MENU_STRINGS.followUpPlaceholder);
      expect(root.activeElement).toBeNull();

      mark().click();
      expect(root.activeElement).toBe(input());
      expect(root.querySelector(".card .message")).not.toBeNull();

      input().value = " What about tone? ";
      buttonByText(root, MENU_STRINGS.ask)!.click();
      expect(h.notices.at(-1)).toEqual({ kind: "juvin.menu.choose", menuId: "menu-1", action: "custom", instruction: "What about tone?" });
      h.renderer.update(update({ state: "running", action: "custom" }));
      h.renderer.update(update({ state: "answer", text: "- The tone is fine." }));
      expect(root.querySelectorAll(".card .message")).toHaveLength(1);
      expect(root.querySelector(".card .message")!.textContent).toBe("- The tone is fine.");
      expect(input().value).toBe("");
      expect(root.activeElement).toBe(input());
    });

    it("brings the answer card back from the mark while an answer stands", () => {
      h.renderer.show({ ...request(h), more: SLOTS });
      key("1");
      h.renderer.update(update({ state: "running", action: "missing" }));
      h.renderer.update(update({ state: "answer", text: "- The deadline." }));
      mark().dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, composed: true }));
      expect(h.root().querySelector(".card .row")).not.toBeNull();

      mark().click();
      expect(h.root().querySelector(".card .message")!.textContent).toBe("- The deadline.");
      expect(input().placeholder).toBe(MENU_STRINGS.followUpPlaceholder);
      expect(h.root().activeElement).toBe(input());

      h.renderer.update(update({ state: "running", action: "soften" }));
      h.renderer.update(update({ state: "error", text: "Failed.", canRetry: true }));
      mark().click();
      expect(input().placeholder).toBe(MENU_STRINGS.askPlaceholder);
    });

    it("gives focus back to the editor when the menu closes from the box", () => {
      h.editor.focus();
      h.renderer.show(request(h));
      mark().click();
      expect(document.activeElement).toBe(hosts()[0]);
      typeKey(input(), "Escape");
      expect(document.activeElement).toBe(h.editor);
    });

    it("keeps the follow-up box focused after Copy", () => {
      h.renderer.show(request(h));
      key("1");
      h.renderer.update(update({ state: "running", action: "missing" }));
      h.renderer.update(update({ state: "answer", text: "- A point" }));
      mark().click();
      expect(h.root().activeElement).toBe(input());
      buttonByText(h.root(), MENU_STRINGS.copy)!.click();
      expect(h.root().activeElement).toBe(input());
      expect(typeKey(input(), "1").defaultPrevented).toBe(false);
    });

    it("gives focus back to the editor when a run removes the box", () => {
      h.editor.focus();
      h.renderer.show(request(h));
      mark().click();
      input().value = "Shorter.";
      typeKey(input(), "Enter");
      h.renderer.update(update({ state: "running", action: "custom" }));
      expect(document.activeElement).toBe(h.editor);
    });
  });

  describe("right-click list", () => {
    const MORE = [...SLOTS, { id: "custom-3f2a9b", title: "Pirate voice" }];
    const openList = (more = MORE) => {
      h.renderer.show({ ...request(h), more });
      const event = new MouseEvent("contextmenu", { bubbles: true, cancelable: true, composed: true });
      h.root().querySelector("button.mark")!.dispatchEvent(event);
      return event;
    };
    const rows = () => Array.from(h.root().querySelectorAll<HTMLButtonElement>(".card .row"));

    it("shows the context facts, every action, and Review", () => {
      expect(openList().defaultPrevented).toBe(true);
      const root = h.root();
      expect(Array.from(root.querySelectorAll(".facts .fact"), (f) => f.textContent)).toEqual(["6 messages", "Entire thread", "Local: qwen3"]);
      expect(rows().map((r) => r.textContent)).toEqual([...MORE.map((a) => a.title), MENU_STRINGS.review]);
      expect(rows()[5]!.dataset.action).toBe("custom-3f2a9b");
      expect(rows()[5]!.querySelector(".icon svg")).not.toBeNull();
      expect(root.querySelectorAll(".card .divider")).toHaveLength(2);
    });

    it("sends the chosen action like a bubble", () => {
      openList();
      rows()[5]!.click();
      expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "custom-3f2a9b" }]);
    });

    it("shows only the facts and Review when there is nothing more", () => {
      openList([]);
      expect(rows().map((r) => r.textContent)).toEqual([MENU_STRINGS.review]);
      expect(h.root().querySelector(".list")).toBeNull();
    });

    it("keeps a refused rewrite with Copy on the list and Ask cards", () => {
      h.renderer.show({ ...request(h), more: MORE });
      key("3");
      h.renderer.update(update({ state: "running", action: "clearer" }));
      h.renderer.update(update({ state: "draft-changed", text: "the rewrite" }));
      h.root().querySelector<HTMLButtonElement>("button.mark")!.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, composed: true }));
      expect(h.root().querySelector(".card .result")!.textContent).toBe("the rewrite");
      expect(buttonByText(h.root(), MENU_STRINGS.copy)).toBeDefined();
      expect(rows().length).toBe(MORE.length + 1);

      h.root().querySelector<HTMLButtonElement>("button.mark")!.click();
      expect(h.root().querySelector(".card .result")!.textContent).toBe("the rewrite");
      expect(h.root().querySelector<HTMLInputElement>(".card input")!.placeholder).toBe(MENU_STRINGS.askPlaceholder);

      h.renderer.update(update({ state: "running", action: "soften" }));
      h.renderer.update(update({ state: "error", text: "Failed.", canRetry: true }));
      h.root().querySelector<HTMLButtonElement>("button.mark")!.click();
      expect(h.root().querySelector(".card .result")).toBeNull();
    });

    it("keeps a standing answer with Copy on the list card", () => {
      h.renderer.show({ ...request(h), more: MORE });
      key("1");
      h.renderer.update(update({ state: "running", action: "missing" }));
      h.renderer.update(update({ state: "answer", text: "- A point" }));
      h.root().querySelector<HTMLButtonElement>("button.mark")!.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, composed: true }));
      expect(h.root().querySelector(".card .message")!.textContent).toBe("- A point");
      expect(buttonByText(h.root(), MENU_STRINGS.copy)).toBeDefined();
      h.editor.remove();
      vi.advanceTimersByTime(600);
      expect(hosts()).toHaveLength(1);
    });

    it("sends review and closes without reporting the menu closed", () => {
      openList();
      rows().at(-1)!.click();
      expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "review" }]);
      expect(hosts()).toHaveLength(0);
    });

    it("does nothing while a run is in progress", () => {
      h.renderer.show({ ...request(h), more: MORE });
      h.renderer.update(update({ state: "running", action: "soften" }));
      h.root().querySelector("button.mark")!.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true }));
      expect(h.root().querySelector(".card")!.hasAttribute("hidden")).toBe(true);
    });

    it("moves through the rows with the arrow keys and picks with Enter", () => {
      openList();
      key("ArrowDown");
      expect(h.root().querySelector(".bubble.focused")).not.toBeNull();
      key("ArrowDown");
      expect(rows()[0]!.classList.contains("focused")).toBe(true);
      expect(h.root().querySelector(".bubble.focused")).toBeNull();
      key("ArrowDown");
      key("ArrowDown");
      expect(rows()[2]!.classList.contains("focused")).toBe(true);
      expect(key("Enter").defaultPrevented).toBe(true);
      expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "clearer" }]);
    });
  });

  it("reaches and activates every card control from the keyboard", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "draft-changed", text: "the rewrite" }));
    const root = h.root();
    const focused = () => Array.from(root.querySelectorAll(".focused"), (e) => e.getAttribute("aria-label") ?? e.textContent);
    key("ArrowDown");
    expect(focused()).toEqual(["What am I missing?"]);
    key("ArrowDown");
    expect(focused()).toEqual([MENU_STRINGS.copy]);
    key("ArrowRight");
    expect(focused()).toEqual([MENU_STRINGS.recapture]);
    key("ArrowRight");
    expect(focused()).toEqual([MENU_STRINGS.close]);
    key("ArrowRight");
    expect(focused()).toEqual([MENU_STRINGS.close]);
    key("ArrowLeft");
    key("ArrowUp");
    expect(focused()).toEqual([MENU_STRINGS.copy]);
    key("ArrowUp");
    expect(focused()).toEqual(["What am I missing?"]);
    key("ArrowDown");
    key("ArrowRight");
    expect(document.activeElement).not.toBe(root.host);
    key("Enter");
    expect(h.notices).toEqual([{ kind: "juvin.menu.choose", menuId: "menu-1", action: "recapture" }]);
  });

  it("reaches the mark from the keyboard and opens Ask and the list from it", () => {
    h.renderer.show({ ...request(h), more: SLOTS });
    key("ArrowUp");
    expect(h.root().querySelector(".bubble.focused")!.textContent).toBe("Make clearer");
    key("ArrowUp");
    expect(h.root().querySelector(".bubble.focused")).toBeNull();
    expect(h.root().querySelector(".mark.focused")).not.toBeNull();
    key("ArrowDown");
    expect(h.root().querySelector(".mark.focused")).toBeNull();
    expect(h.root().querySelector(".bubble.focused")).not.toBeNull();

    key("ArrowUp");
    key("ArrowUp");
    key("ArrowUp");
    expect(key("Enter").defaultPrevented).toBe(true);
    expect(h.root().querySelector<HTMLInputElement>(".card input")!.placeholder).toBe(MENU_STRINGS.askPlaceholder);

    expect(key("Escape").defaultPrevented).toBe(true);
    h.renderer.show({ ...request(h), more: SLOTS });
    expect(key("F10", { shiftKey: true }).defaultPrevented).toBe(true);
    expect(h.root().querySelectorAll(".card .row")).toHaveLength(SLOTS.length + 1);
    h.renderer.show({ ...request(h), more: SLOTS });
    key("ContextMenu");
    expect(h.root().querySelectorAll(".card .row")).toHaveLength(SLOTS.length + 1);
  });

  it("reaches the card from the keyboard after the editor is gone", () => {
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "answer", text: "- A point" }));
    h.editor.remove();
    vi.advanceTimersByTime(600);
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowUp", bubbles: true }));
    expect(h.root().querySelector(".bubble.focused")).toBeNull();
    expect(h.root().querySelector(".card button.focused")!.textContent).toBe(MENU_STRINGS.copy);
  });

  it("moves a retained card out of a modal dialog that was removed", () => {
    const dialog = document.createElement("dialog");
    dialog.append(h.editor);
    document.body.append(dialog);
    vi.spyOn(h.editor, "closest").mockImplementation(((selector: string) => (selector === "dialog:modal" ? dialog : null)) as typeof h.editor.closest);
    h.renderer.show(request(h));
    h.renderer.update(update({ state: "error", text: "Editor gone.", canRetry: false, result: "the rewrite" }));
    dialog.remove();
    vi.advanceTimersByTime(600);
    const host = h.root().host;
    expect(host.isConnected).toBe(true);
    expect(host.parentElement).toBe(document.body);
    expect(h.root().querySelector(".card .result")!.textContent).toBe("the rewrite");
    expect(h.notices).toEqual([]);
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
