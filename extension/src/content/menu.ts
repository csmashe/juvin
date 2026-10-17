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
  askPlaceholder: "Ask for a change",
  run: "Run",
  followUpPlaceholder: "Ask a follow-up",
  ask: "Ask",
  context: "About this draft",
  moreActions: "All actions",
  review: "Review",
  undoKey: "Ctrl+Z",
  undoKeyMac: "Cmd+Z",
  disconnected: "The connection to the Juvin app was lost. Press the shortcut again to reconnect.",
  appliedNote: (undoKey: string) => `Draft rewritten. Press ${undoKey} to undo.`,
};

const MAX_SLOTS = 5;
const MAX_INSTRUCTION_LENGTH = 2000;
/** The mark's place in the drawn focus, where no bubble is. */
const MARK = -1;
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
  more: Action[];
  status: string[];
  ring: HTMLElement;
  mark: HTMLButtonElement;
  bubbles: HTMLButtonElement[];
  card: HTMLElement;
  /** The bubble with the drawn focus; at most one of this and cardFocused is set. */
  focused: number | null;
  /** Index into cardControls(). */
  cardFocused: number | null;
  running: boolean;
  /** A choice the app has not answered yet; it would ignore a second one meanwhile. */
  pending: boolean;
  /** A card holding text the user can still copy outlives the editor; the menu's own actions are gone with it. */
  keepsResult: boolean;
  /** Where the editor last was, so a retained card stays in the viewport after the editor is gone. */
  anchor?: Box;
  lastChoice?: Choice;
  /** The standing What am I missing? answer; Ask continues it until another action runs. */
  answer?: string;
  /** A rewrite the editor refused, kept on the Ask and list cards until another action runs. */
  result?: string;
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
    mark.addEventListener("click", () => this.showAsk());
    mark.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      this.showList();
    });
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
    // Clicks in the menu leave focus and the selection in the editor; only a result block may be selected by hand, and only a text box focused.
    panel.addEventListener("mousedown", (event) => {
      if (!(event.target instanceof Element && event.target.closest(".result, input"))) {
        event.preventDefault();
      }
    });
    mountPoint(editor).append(host);

    const listeners = new AbortController();
    const menu: OpenMenu = { menuId: request.menuId, editor, host, slots, more: request.more, status: request.status, ring, mark, bubbles, card, focused: null, cardFocused: null, running: false, pending: false, keepsResult: false, timers: new Set(), listeners };
    this.menu = menu;

    const win = doc.defaultView ?? window;
    const { signal } = listeners;
    win.addEventListener("keydown", (event) => this.onKeyDown(event), { capture: true, signal });
    // Keys typed in the menu's text box never reach the page, whose shortcuts would otherwise act on them.
    for (const type of ["keypress", "keyup"]) {
      win.addEventListener(type, (event) => event.composedPath().includes(host) && event.stopImmediatePropagation(), { capture: true, signal });
    }

    win.addEventListener("pointerdown", (event) => this.onPointerDown(event), { capture: true, signal });
    win.addEventListener("scroll", () => this.scheduleReposition(), { capture: true, passive: true, signal });
    win.addEventListener("resize", () => this.scheduleReposition(), { passive: true, signal });
    const poll = setInterval(() => this.checkEditor(), EDITOR_POLL_MS);
    signal.addEventListener("abort", () => clearInterval(poll));

    this.reposition();
    return { ok: true };
  }

  has(menuId: string): boolean {
    return this.menu?.menuId === menuId;
  }

  /** False when this menu is no longer open, so the app can stop work nobody sees. */
  update(update: MenuUpdateMessage): boolean {
    const menu = this.menu;
    if (!menu || menu.menuId !== update.menuId) {
      return false;
    }

    menu.pending = false;
    switch (update.state) {
      case "running":
        this.setRunning(menu, update.action);
        return true;
      case "applied": {
        const editor = menu.editor;
        this.teardown();
        this.showNote(editor);
        return true;
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
    return true;
  }

  close(menuId: string): boolean {
    if (this.menu?.menuId !== menuId) {
      return false;
    }

    this.teardown();
    return true;
  }

  /** The app is gone, so nothing can be retried; a result the user could copy stays. */
  disconnected(menuId: string): boolean {
    const menu = this.menu;
    if (menu?.menuId !== menuId) {
      return false;
    }

    menu.pending = false;
    this.showError(menu, MENU_STRINGS.disconnected, false, menu.result ?? menu.answer);
    this.reposition();
    return true;
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
    if (!menu) {
      return;
    }

    const key = event.key;
    // Keys typed in the menu's text box are stopped before the page's capture listeners see them; the browser still types them.
    if (event.composedPath().includes(menu.host)) {
      event.stopImmediatePropagation();
      if (event.ctrlKey || event.altKey || event.metaKey || event.isComposing) {
        return;
      }

      if (key === "Escape") {
        event.preventDefault();
        this.dismiss();
      } else if (key === "Enter") {
        event.preventDefault();
        this.submitInstruction(menu);
      }

      return;
    }

    if (event.ctrlKey || event.altKey || event.metaKey || event.isComposing) {
      return;
    }

    const digit = key.length === 1 && key >= "1" && key <= "9" ? Number(key) : 0;
    if (key === "Escape") {
      this.dismiss();
    } else if (key === "ContextMenu" || (key === "F10" && event.shiftKey)) {
      this.showList();
    } else if (digit > 0 && digit <= menu.slots.length) {
      this.pick(digit - 1);
    } else if (key === "ArrowLeft" || key === "ArrowRight" || key === "ArrowUp" || key === "ArrowDown") {
      this.moveFocus(menu, key);
    } else if (key !== "Enter" || !this.activateFocused(menu)) {
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

  /** Moves the drawn focus without moving DOM focus: between bubbles toward the arrow, through the card's buttons in order, and between the two. */
  private moveFocus(menu: OpenMenu, key: string): void {
    if (menu.running) {
      return;
    }

    const controls = cardControls(menu);
    const bubblesLive = menu.editor.isConnected;
    if (menu.cardFocused !== null) {
      const next = menu.cardFocused + (key === "ArrowDown" || key === "ArrowRight" ? 1 : -1);
      if (next < controls.length && next >= 0) {
        this.drawFocus(menu, null, next);
      } else if (next < 0 && key === "ArrowUp" && bubblesLive) {
        this.drawFocus(menu, lowestBubble(menu), null);
      }

      return;
    }

    const [dx, dy] = key === "ArrowLeft" ? [-1, 0] : key === "ArrowRight" ? [1, 0] : key === "ArrowUp" ? [0, -1] : [0, 1];
    const origin = menu.focused === null ? { x: 0, y: 0 } : slotPoint(menu.focused);
    const best = bubblesLive ? nearestBubble(menu, origin, dx, dy, menu.focused) : null;
    if (best !== null) {
      this.drawFocus(menu, best, null);
    } else if ((key === "ArrowDown" || !bubblesLive) && controls.length > 0) {
      this.drawFocus(menu, null, 0);
    } else if (bubblesLive && menu.focused !== null && menu.focused !== MARK) {
      // Past the outermost bubble the arrow lands on the mark, so Ask is reachable without the mouse.
      this.drawFocus(menu, MARK, null);
    }
  }

  private drawFocus(menu: OpenMenu, bubble: number | null, card: number | null): void {
    menu.bubbles[menu.focused ?? -1]?.classList.remove("focused");
    cardControls(menu)[menu.cardFocused ?? -1]?.classList.remove("focused");
    menu.focused = bubble;
    menu.cardFocused = card;
    menu.bubbles[bubble ?? -1]?.classList.add("focused");
    menu.mark.classList.toggle("focused", bubble === MARK);
    const control = cardControls(menu)[card ?? -1];
    control?.classList.add("focused");
    control?.scrollIntoView?.({ block: "nearest" });
  }

  private activateFocused(menu: OpenMenu): boolean {
    if (menu.cardFocused !== null) {
      cardControls(menu)[menu.cardFocused]?.click();
      return true;
    }

    if (menu.focused === MARK) {
      this.showAsk();
      return true;
    }

    if (menu.focused !== null) {
      this.pick(menu.focused);
      return true;
    }

    return false;
  }

  private pick(index: number): void {
    const slot = this.menu?.slots[index];
    if (slot) {
      this.choose(slot.id);
    }
  }

  private choose(action: string, instruction?: string): boolean {
    const menu = this.menu;
    if (!menu || menu.running || menu.pending || !menu.editor.isConnected) {
      return false;
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
    return true;
  }

  /** The app opens the desktop panel and sends no close for this menu. */
  private review(): void {
    if (this.choose("review")) {
      this.teardown();
    }
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
    this.drawFocus(menu, null, null);
    this.hideCard(menu);
    if (action !== "custom") {
      menu.answer = undefined;
    }

    menu.result = undefined;
    menu.running = true;
    menu.ring.classList.add("running");
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
    menu.cardFocused = null;
    menu.card.hidden = true;
    this.releaseFocus(menu);
    menu.card.replaceChildren();
    menu.keepsResult = false;
  }

  private openCard(menu: OpenMenu): HTMLElement {
    this.clearRunning(menu);
    this.drawFocus(menu, menu.focused, null);
    const close = this.button(menu, "", false, () => this.dismiss());
    close.className = "close";
    close.setAttribute("aria-label", MENU_STRINGS.close);
    close.append(closeIcon(menu.host.ownerDocument));
    this.releaseFocus(menu);
    menu.card.replaceChildren(close);
    menu.card.hidden = false;
    menu.keepsResult = false;
    return menu.card;
  }

  private showAnswer(menu: OpenMenu, text: string): void {
    menu.answer = text;
    const card = this.openCard(menu);
    const followUp = this.askRow(menu, MENU_STRINGS.followUpPlaceholder, MENU_STRINGS.ask);
    card.append(this.block(menu, "message", text), this.buttons(menu, [this.copyButton(menu, text)]), followUp.row);
    menu.keepsResult = true;
    // The user was typing a follow-up, so the next one goes in the same place.
    if (menu.lastChoice?.action === "custom") {
      followUp.input.focus();
    }
  }

  /** The mark opens Ask for a change; a card that already has a text box keeps it and focuses it. */
  private showAsk(): void {
    const menu = this.menu;
    if (!menu || menu.running || !menu.editor.isConnected) {
      return;
    }

    const existing = menu.card.hidden ? null : menu.card.querySelector("input");
    if (existing) {
      existing.focus();
      return;
    }

    // The app treats Ask as a follow-up while an answer stands, so the answer card comes back rather than a fresh Ask card.
    if (menu.answer !== undefined) {
      this.showAnswer(menu, menu.answer);
      this.reposition();
      menu.card.querySelector("input")?.focus();
      return;
    }

    const ask = this.askRow(menu, MENU_STRINGS.askPlaceholder, MENU_STRINGS.run);
    this.openCard(menu).append(...this.retainedResult(menu), ask.row);
    this.reposition();
    ask.input.focus();
  }

  private askRow(menu: OpenMenu, placeholder: string, label: string): { row: HTMLElement; input: HTMLInputElement } {
    const input = menu.host.ownerDocument.createElement("input");
    input.type = "text";
    input.maxLength = MAX_INSTRUCTION_LENGTH;
    input.placeholder = placeholder;
    input.autocomplete = "off";
    input.setAttribute("aria-label", placeholder);
    const row = this.buttons(menu, [this.button(menu, label, true, () => this.submitInstruction(menu))]);
    row.classList.add("ask");
    row.prepend(input);
    return { row, input };
  }

  private submitInstruction(menu: OpenMenu): void {
    const instruction = menu.card.querySelector("input")?.value.trim();
    if (instruction) {
      this.choose("custom", instruction);
    }
  }

  private showList(): void {
    const menu = this.menu;
    if (!menu || menu.running || !menu.editor.isConnected) {
      return;
    }

    const doc = menu.host.ownerDocument;
    const facts = doc.createElement("div");
    facts.className = "facts";
    facts.setAttribute("aria-label", MENU_STRINGS.context);
    facts.append(...menu.status.map((line) => this.block(menu, "fact", line)));

    const card = this.openCard(menu);
    card.append(...this.retainedResult(menu), facts, this.block(menu, "divider", ""));
    if (menu.more.length > 0) {
      const list = doc.createElement("div");
      list.className = "list";
      list.setAttribute("aria-label", MENU_STRINGS.moreActions);
      list.append(...menu.more.map((action) => this.row(menu, action.title, action.id, () => this.choose(action.id))));
      card.append(list, this.block(menu, "divider", ""));
    }

    card.append(this.row(menu, MENU_STRINGS.review, undefined, () => this.review()));
    this.reposition();
  }

  /** The refused rewrite or the standing answer with its Copy, so browsing the list or asking does not lose it. */
  private retainedResult(menu: OpenMenu): HTMLElement[] {
    const kept = menu.result ?? menu.answer;
    if (kept === undefined) {
      return [];
    }

    menu.keepsResult = true;
    return [this.block(menu, menu.result === undefined ? "message" : "result", kept), this.buttons(menu, [this.copyButton(menu, kept)]), this.block(menu, "divider", "")];
  }

  private row(menu: OpenMenu, title: string, actionId: string | undefined, onClick: () => void): HTMLButtonElement {
    const doc = menu.host.ownerDocument;
    const row = this.button(menu, "", false, onClick);
    row.className = "row";
    row.setAttribute("role", "menuitem");
    if (actionId !== undefined) {
      row.dataset.action = actionId;
      const icon = doc.createElement("span");
      icon.className = "icon";
      icon.append(actionIcon(doc, actionId));
      row.append(icon);
    }

    const label = doc.createElement("span");
    label.className = "title";
    label.textContent = title;
    row.append(label);
    return row;
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
    menu.result = text;
  }

  private showError(menu: OpenMenu, text: string, canRetry: boolean, result: string | undefined): void {
    const card = this.openCard(menu);
    card.append(this.block(menu, "message", text));
    const buttons: HTMLButtonElement[] = [];
    if (result !== undefined) {
      card.append(this.block(menu, "result", result));
      buttons.push(this.copyButton(menu, result));
      menu.keepsResult = true;
      menu.result = result;
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
      // A modal dialog the menu was mounted in may have closed or left with the editor.
      const host = this.menu.host;
      if (!host.isConnected || host.closest("dialog:not([open])")) {
        host.ownerDocument.body.append(host);
      }
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

    this.releaseFocus(menu);
    menu.host.remove();
  }

  /** Gives focus back to the editor before the menu's text box goes away. */
  private releaseFocus(menu: OpenMenu): void {
    if (menu.host.ownerDocument.activeElement === menu.host && menu.editor.isConnected) {
      menu.editor.focus({ preventScroll: true });
    }
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

function nearestBubble(menu: OpenMenu, origin: { x: number; y: number }, dx: number, dy: number, skip: number | null): number | null {
  let best: number | null = null;
  let bestScore = Infinity;
  for (let index = 0; index < menu.slots.length; index++) {
    const point = slotPoint(index);
    const along = (point.x - origin.x) * dx + (point.y - origin.y) * dy;
    if (index === skip || along <= 0.05) {
      continue;
    }

    const across = Math.abs((point.x - origin.x) * dy - (point.y - origin.y) * dx);
    const score = along + 2 * across;
    if (score < bestScore) {
      bestScore = score;
      best = index;
    }
  }

  return best;
}

/** The first of the bubbles nearest the card. */
function lowestBubble(menu: OpenMenu): number | null {
  let lowest: number | null = null;
  for (let index = 0; index < menu.slots.length; index++) {
    if (lowest === null || slotPoint(index).y > slotPoint(lowest).y) {
      lowest = index;
    }
  }

  return lowest;
}

/** The card's buttons in reading order, with its close control last. */
function cardControls(menu: OpenMenu): HTMLButtonElement[] {
  if (menu.card.hidden) {
    return [];
  }

  const close = menu.card.querySelector<HTMLButtonElement>(".close");
  return [...menu.card.querySelectorAll<HTMLButtonElement>("button:not(.close)"), ...(close ? [close] : [])];
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
  const root = parent.getRootNode();
  const focused = (root instanceof ShadowRoot ? root.activeElement : null) ?? doc.activeElement;
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
