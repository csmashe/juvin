// Draws the action menu the app sends beside the draft editor, in a closed shadow root, and reports what the user picks.

import type { Action, MenuUpdateMessage } from "../shared/protocol";
import type { MenuNotice, MenuShowRequest, MenuShowResult } from "../shared/runtime-messages";
import type { Editor, EditorRegistry } from "./editor-registry";
import { actionIcon, closeIcon, juvinMark } from "./menu-icons";
import { BUBBLE_SIZE, HOST_STYLE, MENU_CSS, RING_CENTER, RING_RADIUS, RING_SIZE } from "./menu-styles";

export const MENU_STRINGS = {
  menuLabel: "Juvin actions",
  markLabel: "Juvin",
  close: "Close",
  draftChangedHeading: "The draft changed while Juvin was working.",
  draftChangedBody: "Copy the result, or recapture the draft and pick the action again.",
  copy: "Copy",
  copied: "Copied",
  copyFailed: "Copy failed",
  recapture: "Recapture",
  tryAgain: "Try again",
  undoKey: "Ctrl+Z",
  undoKeyMac: "Cmd+Z",
  appliedNote: (undoKey: string) => `Draft rewritten. Press ${undoKey} to undo.`,
};

const MAX_SLOTS = 5;
const GAP = 12;
const MARGIN = 8;
const EDITOR_POLL_MS = 500;
const COPIED_MS = 1500;
const NOTE_FADE_MS = 3600;
const NOTE_REMOVE_MS = 4000;
const MENU_FALLBACK_SIZE = { width: RING_SIZE.width + 40, height: RING_SIZE.height + 40 };
const NOTE_FALLBACK_SIZE = { width: 280, height: 34 };
// Screen angles clockwise from 12 o'clock, starting at the lower left so keys 1 to 5 run clockwise.
// Bubble centres relative to the mark, an arch open at the bottom where the card attaches: keys 1 to 5 run clockwise from the lower left.
const SLOT_OFFSETS = [
  { x: -156, y: 56 },
  { x: -158, y: -62 },
  { x: 0, y: -142 },
  { x: 158, y: -62 },
  { x: 156, y: 56 },
];
const SVG_NS = "http://www.w3.org/2000/svg";

export interface Size {
  width: number;
  height: number;
}

export interface Box {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/** To the right of the target when it fits, else above, below, or left, and always inside the viewport. */
export function placeBeside(target: Box, size: Size, viewport: Size): { left: number; top: number } {
  let left: number;
  let top: number;
  if (target.right + GAP + size.width <= viewport.width - MARGIN) {
    left = target.right + GAP;
    top = target.top;
  } else if (target.top - GAP - size.height >= MARGIN) {
    left = target.left;
    top = target.top - GAP - size.height;
  } else if (target.bottom + GAP + size.height <= viewport.height - MARGIN) {
    left = target.left;
    top = target.bottom + GAP;
  } else if (target.left - GAP - size.width >= MARGIN) {
    left = target.left - GAP - size.width;
    top = target.top;
  } else {
    left = target.right + GAP;
    top = target.top;
  }

  return { left: clamp(left, MARGIN, viewport.width - size.width - MARGIN), top: clamp(top, MARGIN, viewport.height - size.height - MARGIN) };
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(value, max));
}

export function isMacPlatform(nav: Navigator = navigator): boolean {
  const platform = (nav as Navigator & { userAgentData?: { platform?: string } }).userAgentData?.platform ?? nav.platform ?? "";
  return /mac/i.test(platform);
}

export interface MenuRendererOptions {
  /** Tests only: the closed shadow root is otherwise unreachable from outside. */
  onShadowRoot?: (root: ShadowRoot) => void;
  isMac?: boolean;
}

interface Choice {
  action: string;
  instruction?: string;
}

interface OpenMenu {
  menuId: string;
  editor: Editor;
  host: HTMLElement;
  slots: Action[];
  ring: HTMLElement;
  bubbles: HTMLButtonElement[];
  card: HTMLElement;
  focused: number | null;
  running: boolean;
  /** A choice the app has not answered yet; it would ignore a second one meanwhile. */
  pending: boolean;
  /** A card holding text the user can still copy outlives the editor; the menu's own actions are gone with it. */
  keepsResult: boolean;
  /** Where the editor last was, so a retained card stays in the viewport after the editor is gone. */
  anchor?: Box;
  lastChoice?: Choice;
  frame?: number;
  timers: Set<ReturnType<typeof setTimeout>>;
  listeners: AbortController;
}

interface Note {
  host: HTMLElement;
  timers: ReturnType<typeof setTimeout>[];
}

export class MenuRenderer {
  private menu: OpenMenu | undefined;
  private note: Note | undefined;

  constructor(
    private readonly registry: EditorRegistry,
    private readonly notify: (notice: MenuNotice) => void | Promise<unknown>,
    private readonly options: MenuRendererOptions = {},
  ) {}

  show(request: MenuShowRequest): MenuShowResult {
    const editor = this.registry.find(request.editorId);
    if (!editor) {
      return { ok: false, error: { code: "editor-gone", message: "The draft editor is no longer on the page." } };
    }

    this.teardown();
    this.clearNote();

    const doc = editor.ownerDocument;
    const { host, root } = this.createHost(doc);
    const panel = doc.createElement("div");
    panel.className = "root";
    panel.setAttribute("role", "menu");
    panel.setAttribute("aria-label", MENU_STRINGS.menuLabel);

    const slots = request.slots.slice(0, MAX_SLOTS);
    const ring = doc.createElement("div");
    ring.className = "ring";
    ring.append(spokes(doc, slots.length));
    const mark = doc.createElement("button");
    mark.className = "mark";
    mark.type = "button";
    mark.tabIndex = -1;
    mark.setAttribute("aria-label", MENU_STRINGS.markLabel);
    mark.append(juvinMark(doc));
    ring.append(mark);

    const bubbles = slots.map((slot, index) => {
      const bubble = this.createBubble(doc, slot, index);
      bubble.addEventListener("click", () => this.pick(index));
      ring.append(bubble);
      return bubble;
    });


    const card = doc.createElement("div");
    card.className = "card";
    card.hidden = true;

    panel.append(ring, card);
    root.append(panel);
    // Clicks in the menu leave focus and the selection in the editor; only a result block may be selected by hand.
    panel.addEventListener("mousedown", (event) => {
      if (!(event.target instanceof Element && event.target.closest(".result"))) {
        event.preventDefault();
      }
    });
    mountPoint(editor).append(host);

    const listeners = new AbortController();
    const menu: OpenMenu = { menuId: request.menuId, editor, host, slots, ring, bubbles, card, focused: null, running: false, pending: false, keepsResult: false, timers: new Set(), listeners };
    this.menu = menu;

    const win = doc.defaultView ?? window;
    const { signal } = listeners;
    win.addEventListener("keydown", (event) => this.onKeyDown(event), { capture: true, signal });
    win.addEventListener("pointerdown", (event) => this.onPointerDown(event), { capture: true, signal });
    win.addEventListener("scroll", () => this.scheduleReposition(), { capture: true, passive: true, signal });
    win.addEventListener("resize", () => this.scheduleReposition(), { passive: true, signal });
    const poll = setInterval(() => this.checkEditor(), EDITOR_POLL_MS);
    signal.addEventListener("abort", () => clearInterval(poll));

    this.reposition();
    return { ok: true };
  }

  update(update: MenuUpdateMessage): void {
    const menu = this.menu;
    if (!menu || menu.menuId !== update.menuId) {
      return;
    }

    menu.pending = false;
    switch (update.state) {
      case "running":
        this.setRunning(menu, update.action);
        return;
      case "applied": {
        const editor = menu.editor;
        this.teardown();
        this.showNote(editor);
        return;
      }
      case "answer":
        this.showAnswer(menu, update.text);
        break;
      case "draft-changed":
        this.showDraftChanged(menu, update.text);
        break;
      case "error":
        this.showError(menu, update.text, update.canRetry, update.result);
        break;
    }

    this.reposition();
  }

  close(menuId: string): void {
    if (this.menu?.menuId === menuId) {
      this.teardown();
    }
  }

  private createHost(doc: Document): { host: HTMLElement; root: ShadowRoot } {
    const host = doc.createElement("juvin-menu");
    host.setAttribute("style", HOST_STYLE);
    const root = host.attachShadow({ mode: "closed" });
    const style = doc.createElement("style");
    style.textContent = MENU_CSS;
    root.append(style);
    this.options.onShadowRoot?.(root);
    return { host, root };
  }

  private createBubble(doc: Document, slot: Action, index: number): HTMLButtonElement {
    const bubble = doc.createElement("button");
    bubble.className = "bubble";
    bubble.type = "button";
    bubble.tabIndex = -1;
    bubble.setAttribute("role", "menuitem");
    bubble.dataset.action = slot.id;
    const center = slotCenter(index);
    bubble.style.left = `${Math.round(center.x - BUBBLE_SIZE.width / 2)}px`;
    bubble.style.top = `${Math.round(center.y - BUBBLE_SIZE.height / 2)}px`;

    const icon = doc.createElement("span");
    icon.className = "icon";
    icon.append(actionIcon(doc, slot.id));

    const title = doc.createElement("span");
    title.className = "title";
    title.textContent = slot.title;
    bubble.append(icon, title);
    return bubble;
  }

  private onKeyDown(event: KeyboardEvent): void {
    const menu = this.menu;
    if (!menu || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) {
      return;
    }

    const key = event.key;
    const digit = key.length === 1 && key >= "1" && key <= "9" ? Number(key) : 0;
    if (key === "Escape") {
      this.dismiss();
    } else if (digit > 0 && digit <= menu.slots.length) {
      this.pick(digit - 1);
    } else if (key === "ArrowLeft" || key === "ArrowRight" || key === "ArrowUp" || key === "ArrowDown") {
      this.moveFocus(menu, key);
    } else if (key === "Enter" && menu.focused !== null) {
      this.pick(menu.focused);
    } else {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
  }

  private onPointerDown(event: PointerEvent): void {
    const menu = this.menu;
    if (menu && !event.composedPath().includes(menu.host)) {
      this.dismiss();
    }
  }

  /** Moves the drawn focus to the nearest bubble in the arrow's direction, without moving DOM focus. */
  private moveFocus(menu: OpenMenu, key: string): void {
    if (menu.running) {
      return;
    }

    const [dx, dy] = key === "ArrowLeft" ? [-1, 0] : key === "ArrowRight" ? [1, 0] : key === "ArrowUp" ? [0, -1] : [0, 1];
    const origin = menu.focused === null ? { x: 0, y: 0 } : slotPoint(menu.focused);
    let best: number | null = null;
    let bestScore = Infinity;
    for (let index = 0; index < menu.slots.length; index++) {
      const point = slotPoint(index);
      const along = (point.x - origin.x) * dx + (point.y - origin.y) * dy;
      if (index === menu.focused || along <= 0.05) {
        continue;
      }

      const across = Math.abs((point.x - origin.x) * dy - (point.y - origin.y) * dx);
      const score = along + 2 * across;
      if (score < bestScore) {
        bestScore = score;
        best = index;
      }
    }

    if (best === null) {
      return;
    }

    menu.bubbles[menu.focused ?? -1]?.classList.remove("focused");
    menu.bubbles[best]?.classList.add("focused");
    menu.focused = best;
  }

  private pick(index: number): void {
    const slot = this.menu?.slots[index];
    if (slot) {
      this.choose(slot.id);
    }
  }

  private choose(action: string, instruction?: string): void {
    const menu = this.menu;
    if (!menu || menu.running || menu.pending || !menu.editor.isConnected) {
      return;
    }

    menu.pending = true;
    menu.lastChoice = instruction === undefined ? { action } : { action, instruction };
    const sent = this.notify(instruction === undefined ? { kind: "juvin.menu.choose", menuId: menu.menuId, action } : { kind: "juvin.menu.choose", menuId: menu.menuId, action, instruction });
    // A choice the background never received gets no update, so the menu must not wait for one.
    void Promise.resolve(sent).catch(() => {
      if (this.menu === menu) {
        menu.pending = false;
      }
    });
  }

  private dismiss(): void {
    const menuId = this.menu?.menuId;
    if (menuId === undefined) {
      return;
    }

    this.teardown();
    void Promise.resolve(this.notify({ kind: "juvin.menu.closed", menuId })).catch(() => undefined);
  }

  private setRunning(menu: OpenMenu, action: string): void {
    this.hideCard(menu);
    menu.running = true;
    menu.ring.classList.add("running");
    menu.bubbles[menu.focused ?? -1]?.classList.remove("focused");
    menu.focused = null;
    const bubble = menu.bubbles.find((b) => b.dataset.action === action);
    if (bubble) {
      bubble.classList.add("active");
      const spinner = menu.host.ownerDocument.createElement("span");
      spinner.className = "spinner";
      bubble.querySelector(".icon")?.append(spinner);
    }
  }

  private clearRunning(menu: OpenMenu): void {
    menu.running = false;
    menu.ring.classList.remove("running");
    for (const bubble of menu.bubbles) {
      bubble.classList.remove("active");
      bubble.querySelector(".spinner")?.remove();
    }
  }

  private hideCard(menu: OpenMenu): void {
    menu.card.hidden = true;
    menu.card.replaceChildren();
    menu.keepsResult = false;
  }

  private openCard(menu: OpenMenu): HTMLElement {
    this.clearRunning(menu);
    const close = this.button(menu, "", false, () => this.dismiss());
    close.className = "close";
    close.setAttribute("aria-label", MENU_STRINGS.close);
    close.append(closeIcon(menu.host.ownerDocument));
    menu.card.replaceChildren(close);
    menu.card.hidden = false;
    menu.keepsResult = false;
    return menu.card;
  }

  private showAnswer(menu: OpenMenu, text: string): void {
    const card = this.openCard(menu);
    card.append(this.block(menu, "message", text), this.buttons(menu, [this.copyButton(menu, text)]));
    menu.keepsResult = true;
    // Slice C adds the follow-up box here.
  }

  private showDraftChanged(menu: OpenMenu, text: string): void {
    const card = this.openCard(menu);
    const recapture = this.button(menu, MENU_STRINGS.recapture, true, () => this.choose("recapture"));
    card.append(
      this.block(menu, "heading", MENU_STRINGS.draftChangedHeading),
      this.block(menu, "message", MENU_STRINGS.draftChangedBody),
      this.block(menu, "result", text),
      this.buttons(menu, [this.copyButton(menu, text), recapture]),
    );
    menu.keepsResult = true;
  }

  private showError(menu: OpenMenu, text: string, canRetry: boolean, result: string | undefined): void {
    const card = this.openCard(menu);
    card.append(this.block(menu, "message", text));
    const buttons: HTMLButtonElement[] = [];
    if (result !== undefined) {
      card.append(this.block(menu, "result", result));
      buttons.push(this.copyButton(menu, result));
      menu.keepsResult = true;
    }

    const retry = menu.lastChoice;
    if (canRetry && retry) {
      buttons.push(this.button(menu, MENU_STRINGS.tryAgain, true, () => this.choose(retry.action, retry.instruction)));
    }

    if (buttons.length > 0) {
      card.append(this.buttons(menu, buttons));
    }
  }

  private block(menu: OpenMenu, className: string, text: string): HTMLElement {
    const element = menu.host.ownerDocument.createElement("div");
    element.className = className;
    element.textContent = text;
    return element;
  }

  private buttons(menu: OpenMenu, buttons: HTMLButtonElement[]): HTMLElement {
    const row = menu.host.ownerDocument.createElement("div");
    row.className = "actions";
    row.append(...buttons);
    return row;
  }

  private button(menu: OpenMenu, label: string, primary: boolean, onClick: () => void): HTMLButtonElement {
    const button = menu.host.ownerDocument.createElement("button");
    button.type = "button";
    button.tabIndex = -1;
    button.textContent = label;
    if (primary) {
      button.className = "primary";
    }

    button.addEventListener("click", onClick);
    return button;
  }

  private copyButton(menu: OpenMenu, text: string): HTMLButtonElement {
    const button = this.button(menu, MENU_STRINGS.copy, false, () => {
      void copyText(menu.card, text)
        .then((copied) => (button.textContent = copied ? MENU_STRINGS.copied : MENU_STRINGS.copyFailed))
        .finally(() => {
          const timer = setTimeout(() => {
            menu.timers.delete(timer);
            button.textContent = MENU_STRINGS.copy;
          }, COPIED_MS);
          menu.timers.add(timer);
        });
    });
    return button;
  }

  private checkEditor(): boolean {
    if (!this.menu || this.menu.editor.isConnected) {
      return true;
    }

    if (!this.menu.keepsResult) {
      this.dismiss();
    } else {
      this.menu.ring.classList.add("gone");
    }

    return false;
  }

  private scheduleReposition(): void {
    const menu = this.menu;
    if (!menu || menu.frame !== undefined) {
      return;
    }

    menu.frame = requestAnimationFrame(() => {
      menu.frame = undefined;
      if (this.menu === menu) {
        this.reposition();
      }
    });
  }

  private reposition(): void {
    const menu = this.menu;
    if (!menu) {
      return;
    }

    if (this.checkEditor()) {
      menu.anchor = menu.editor.getBoundingClientRect();
    }

    if (this.menu === menu && menu.anchor) {
      placeHost(menu.host, menu.anchor, MENU_FALLBACK_SIZE);
    }
  }

  private teardown(): void {
    const menu = this.menu;
    if (!menu) {
      return;
    }

    this.menu = undefined;
    menu.listeners.abort();
    if (menu.frame !== undefined) {
      cancelAnimationFrame(menu.frame);
    }

    for (const timer of menu.timers) {
      clearTimeout(timer);
    }

    menu.host.remove();
  }

  private showNote(editor: Editor): void {
    if (!editor.isConnected) {
      return;
    }

    const doc = editor.ownerDocument;
    const { host, root } = this.createHost(doc);
    const note = doc.createElement("div");
    note.className = "note";
    note.setAttribute("role", "status");
    note.textContent = MENU_STRINGS.appliedNote((this.options.isMac ?? isMacPlatform()) ? MENU_STRINGS.undoKeyMac : MENU_STRINGS.undoKey);
    root.append(note);
    mountPoint(editor).append(host);
    placeHost(host, editor.getBoundingClientRect(), NOTE_FALLBACK_SIZE);

    this.note = {
      host,
      timers: [setTimeout(() => note.classList.add("fading"), NOTE_FADE_MS), setTimeout(() => this.clearNote(), NOTE_REMOVE_MS)],
    };
  }

  private clearNote(): void {
    const note = this.note;
    if (!note) {
      return;
    }

    this.note = undefined;
    for (const timer of note.timers) {
      clearTimeout(timer);
    }

    note.host.remove();
  }
}

function slotCenter(index: number): { x: number; y: number } {
  const point = slotPoint(index);
  return { x: RING_CENTER.x + RING_RADIUS * point.x, y: RING_CENTER.y + RING_RADIUS * point.y };
}

/** One line from the mark to each bubble, drawn behind them. */
function spokes(doc: Document, count: number): SVGSVGElement {
  const svg = doc.createElementNS(SVG_NS, "svg");
  svg.setAttribute("class", "spokes");
  svg.setAttribute("viewBox", `0 0 ${RING_SIZE.width} ${RING_SIZE.height}`);
  svg.setAttribute("aria-hidden", "true");
  for (let index = 0; index < count; index++) {
    const end = slotCenter(index);
    const line = doc.createElementNS(SVG_NS, "line");
    line.setAttribute("x1", String(RING_CENTER.x));
    line.setAttribute("y1", String(RING_CENTER.y));
    line.setAttribute("x2", end.x.toFixed(1));
    line.setAttribute("y2", end.y.toFixed(1));
    svg.append(line);
  }

  return svg;
}

function slotPoint(index: number): { x: number; y: number } {
  const offset = SLOT_OFFSETS[index] ?? { x: 0, y: 0 };
  return { x: offset.x / RING_RADIUS, y: offset.y / RING_RADIUS };
}

// Everything outside a modal dialog is inert, so a menu for an editor inside one must live inside it too.
function mountPoint(editor: Editor): Element {
  return editor.closest("dialog:modal") ?? editor.ownerDocument.body;
}

function placeHost(host: HTMLElement, anchor: Box, fallback: Size): void {
  const win = host.ownerDocument.defaultView ?? window;
  const size = { width: host.offsetWidth || fallback.width, height: host.offsetHeight || fallback.height };
  const { left, top } = placeBeside(anchor, size, { width: win.innerWidth, height: win.innerHeight });
  host.style.setProperty("left", `${Math.round(left)}px`, "important");
  host.style.setProperty("top", `${Math.round(top)}px`, "important");
}

// Content scripts on http pages have no navigator.clipboard, so the legacy copy command is the fallback there.
async function copyText(parent: HTMLElement, text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Fall through to the legacy command.
  }

  const doc = parent.ownerDocument;
  const scratch = doc.createElement("textarea");
  scratch.value = text;
  scratch.setAttribute("readonly", "");
  scratch.style.position = "fixed";
  scratch.style.opacity = "0";
  parent.append(scratch);
  const focused = doc.activeElement;
  const selection = doc.getSelection();
  const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i).cloneRange()) : [];
  try {
    scratch.select();
    return typeof doc.execCommand === "function" && doc.execCommand("copy");
  } catch {
    return false;
  } finally {
    scratch.remove();
    if (focused instanceof HTMLElement && focused.isConnected) {
      focused.focus({ preventScroll: true });
    }

    if (selection && ranges.length > 0) {
      selection.removeAllRanges();
      for (const range of ranges) {
        selection.addRange(range);
      }
    }
  }
}
