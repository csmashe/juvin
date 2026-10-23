// Wire types and helpers for docs/connector-protocol.md (protocol version 1).

export const PROTOCOL_VERSION = 1;
export const HOST_NAME = "com.juvin.host";
export const FRAME_CAP_BYTES = 1_048_576;
/** The longest `menu.choose` instruction the app accepts, once trimmed. */
export const MAX_INSTRUCTION_LENGTH = 2000;

export type BrowserName = "firefox" | "chrome" | "edge" | "chromium" | "unknown";

export type Completeness =
  | "entire-thread"
  | "loaded-messages-only"
  | "visible-text-only"
  | "draft-only"
  | "truncated";

export type ContextErrorCode =
  | "no-active-tab"
  | "no-adapter"
  | "no-editor"
  | "no-selection"
  | "page-not-ready"
  | "editor-gone"
  | "browser-not-focused"
  | "internal";

export type ApplyErrorCode = "draft-changed" | "editor-gone" | "unsupported-content" | "internal";

export type MenuErrorCode = "editor-gone" | "page-not-ready" | "unsupported" | "internal";

export type MenuState = "running" | "applied" | "answer" | "draft-changed" | "error";

export interface ErrorPayload<TCode extends string = string> {
  code: TCode;
  message: string;
}

export interface ThreadMessage {
  from: string;
  to?: string[];
  sent?: string;
  body: string;
}

export interface Thread {
  completeness: Completeness;
  /** Absent means 0. */
  collapsedCount?: number;
  messages: ThreadMessage[];
}

export interface Capture {
  adapter: string;
  pageUrl?: string;
  capturedAt?: string;
  subject?: string;
  editor: { id: string; hash: string };
  /**
   * `markdown` is the selection in the Markdown subset and `formatHash` fingerprints its formatting; both only for a rich editor.
   * `hasEmbeddedContent` is true when the selection holds an image or other content Apply would delete.
   */
  selection: { text: string; start: number; end: number; markdown?: string; formatHash?: string; hasEmbeddedContent?: boolean };
  thread: Thread;
}

export interface Action {
  id: string;
  title: string;
}

export interface Health {
  mode: "local" | "codex";
  modelReachable: boolean | null;
}

export interface ConfigMessage {
  v: 1;
  type: "config";
  actions: Action[];
  adapters: string[];
  shortcut: string;
  /** The US key position the shortcut is pressed at, when the user's layout moved its key. */
  shortcutKey?: string;
  health: Health;
}

export interface PingMessage {
  v: 1;
  type: "ping";
  id: string;
}

export interface PongMessage {
  v: 1;
  type: "pong";
  id: string;
}

export interface ContextRequestMessage {
  v: 1;
  type: "context.request";
  id: string;
  /** Recapture from the tab that owns this editor instead of the shortcut's tab. */
  editorId?: string;
  /** Sent for the desktop hotkey: with no pending shortcut, answer browser-not-focused unless a browser window has focus. */
  requireFocus?: true;
}

export interface ApplyRequestMessage {
  v: 1;
  type: "apply.request";
  id: string;
  editorId: string;
  expectedHash: string;
  start: number;
  end: number;
  text: string;
  /** Absent for plain text. */
  format?: "markdown";
  /** The capture's `selection.formatHash`; present exactly when `format` is set. */
  expectedFormatHash?: string;
}

export interface TestPageRequestMessage {
  v: 1;
  type: "testpage.request";
  id: string;
}

export interface MenuShowMessage {
  v: 1;
  type: "menu.show";
  id: string;
  menuId: string;
  editorId: string;
  /** The five bubbles in key order, 1 to 5. */
  slots: Action[];
  /** The right-click list. */
  more: Action[];
  /** The status line, already localized, in display order. */
  status: string[];
}

/** `text` is shown to the user and never logged: an answer, an unapplied result, or a localized error. */
export type MenuUpdateMessage =
  | { v: 1; type: "menu.update"; menuId: string; state: "running"; action: string }
  | { v: 1; type: "menu.update"; menuId: string; state: "applied" }
  | { v: 1; type: "menu.update"; menuId: string; state: "answer" | "draft-changed"; text: string }
  /** `result` is a finished rewrite that could not be applied, offered for Copy. */
  | { v: 1; type: "menu.update"; menuId: string; state: "error"; text: string; canRetry: boolean; result?: string };

/** Sent both ways: the app superseded the menu, or the user dismissed it. */
export interface MenuCloseMessage {
  v: 1;
  type: "menu.close";
  menuId: string;
}

export interface ErrorMessage {
  v: 1;
  type: "error";
  id?: string;
  error: ErrorPayload;
}

export type IncomingMessage =
  | ConfigMessage
  | PingMessage
  | PongMessage
  | ContextRequestMessage
  | ApplyRequestMessage
  | TestPageRequestMessage
  | MenuShowMessage
  | MenuUpdateMessage
  | MenuCloseMessage
  | ErrorMessage;

export interface HelloMessage {
  v: 1;
  type: "hello";
  extension: { id: string; version: string };
  browser: { name: BrowserName; version: string };
}

export interface ShortcutPressedMessage {
  v: 1;
  type: "shortcut.pressed";
}

export type ContextResponseMessage =
  | { v: 1; type: "context.response"; id: string; ok: true; capture: Capture }
  | { v: 1; type: "context.response"; id: string; ok: false; error: ErrorPayload<ContextErrorCode> };

export type ApplyResponseMessage =
  | { v: 1; type: "apply.response"; id: string; ok: true }
  | { v: 1; type: "apply.response"; id: string; ok: false; error: ErrorPayload<ApplyErrorCode> };

export type TestPageResponseMessage =
  | { v: 1; type: "testpage.response"; id: string; ok: true }
  | { v: 1; type: "testpage.response"; id: string; ok: false; error: ErrorPayload<"internal"> };

export type MenuResponseMessage =
  | { v: 1; type: "menu.response"; id: string; ok: true }
  | { v: 1; type: "menu.response"; id: string; ok: false; error: ErrorPayload<MenuErrorCode> };

/** `action` is an id from the menu, `custom` with the user's instruction, `review`, or `recapture`. */
export interface MenuChooseMessage {
  v: 1;
  type: "menu.choose";
  menuId: string;
  action: string;
  instruction?: string;
}

export type OutgoingMessage =
  | HelloMessage
  | ShortcutPressedMessage
  | PingMessage
  | PongMessage
  | ContextResponseMessage
  | ApplyResponseMessage
  | TestPageResponseMessage
  | MenuResponseMessage
  | MenuChooseMessage
  | MenuCloseMessage
  | ErrorMessage;

export function hello(
  extension: { id: string; version: string },
  browser: { name: BrowserName; version: string },
): HelloMessage {
  return { v: 1, type: "hello", extension, browser };
}

export function shortcutPressed(): ShortcutPressedMessage {
  return { v: 1, type: "shortcut.pressed" };
}

export function ping(id: string): PingMessage {
  return { v: 1, type: "ping", id };
}

export function pong(id: string): PongMessage {
  return { v: 1, type: "pong", id };
}

export function contextOk(id: string, capture: Capture): ContextResponseMessage {
  return { v: 1, type: "context.response", id, ok: true, capture };
}

export function contextError(id: string, error: ErrorPayload<ContextErrorCode>): ContextResponseMessage {
  return { v: 1, type: "context.response", id, ok: false, error };
}

export function applyOk(id: string): ApplyResponseMessage {
  return { v: 1, type: "apply.response", id, ok: true };
}

export function applyError(id: string, error: ErrorPayload<ApplyErrorCode>): ApplyResponseMessage {
  return { v: 1, type: "apply.response", id, ok: false, error };
}

export function testPageOk(id: string): TestPageResponseMessage {
  return { v: 1, type: "testpage.response", id, ok: true };
}

export function testPageError(id: string, error: ErrorPayload<"internal">): TestPageResponseMessage {
  return { v: 1, type: "testpage.response", id, ok: false, error };
}

export function menuOk(id: string): MenuResponseMessage {
  return { v: 1, type: "menu.response", id, ok: true };
}

export function menuError(id: string, error: ErrorPayload<MenuErrorCode>): MenuResponseMessage {
  return { v: 1, type: "menu.response", id, ok: false, error };
}

/** `instruction` goes only with `custom`, which requires it. */
export function menuChoose(menuId: string, action: string, instruction?: string): MenuChooseMessage {
  return instruction === undefined ? { v: 1, type: "menu.choose", menuId, action } : { v: 1, type: "menu.choose", menuId, action, instruction };
}

export function menuClose(menuId: string): MenuCloseMessage {
  return { v: 1, type: "menu.close", menuId };
}

export function errorMessage(id: string | undefined, code: string, message: string): ErrorMessage {
  return id === undefined ? { v: 1, type: "error", error: { code, message } } : { v: 1, type: "error", id, error: { code, message } };
}

/** `replyDue` is false when the rejected message was itself an `error`, which is never answered with another. */
export type ParseResult = { ok: true; message: IncomingMessage } | { ok: false; reply: ErrorMessage; replyDue: boolean };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isString(value: unknown): value is string {
  return typeof value === "string";
}

function isAction(value: unknown): value is Action {
  return isRecord(value) && isString(value.id) && isString(value.title);
}

function isHealth(value: unknown): value is Health {
  return (
    isRecord(value) &&
    (value.mode === "local" || value.mode === "codex") &&
    (value.modelReachable === true || value.modelReachable === false || value.modelReachable === null)
  );
}

function isMenuUpdate(raw: Record<string, unknown>): boolean {
  switch (raw.state) {
    case "running":
      return isString(raw.action);
    case "applied":
      return true;
    case "answer":
    case "draft-changed":
      return isString(raw.text);
    case "error":
      return isString(raw.text) && typeof raw.canRetry === "boolean";
    default:
      return false;
  }
}

function toMenuUpdate(menuId: string, raw: Record<string, unknown>): MenuUpdateMessage {
  const base = { v: 1, type: "menu.update", menuId } as const;
  switch (raw.state) {
    case "running":
      return { ...base, state: "running", action: raw.action as string };
    case "applied":
      return { ...base, state: "applied" };
    case "answer":
    case "draft-changed":
      return { ...base, state: raw.state, text: raw.text as string };
    default:
      return { ...base, state: "error", text: raw.text as string, canRetry: raw.canRetry as boolean, ...(isString(raw.result) ? { result: raw.result } : {}) };
  }
}

function isOffsets(start: unknown, end: unknown): boolean {
  return typeof start === "number" && typeof end === "number" && Number.isInteger(start) && Number.isInteger(end) && start >= 0 && end >= start;
}

function isErrorPayload(value: unknown): value is ErrorPayload {
  return isRecord(value) && isString(value.code) && isString(value.message);
}

/** Validates a message received from the app. Unknown properties are ignored, as the spec requires. */
export function parseIncoming(raw: unknown): ParseResult {
  if (!isRecord(raw)) {
    return { ok: false, reply: errorMessage(undefined, "bad-message", "Message is not a JSON object."), replyDue: true };
  }

  const id = isString(raw.id) ? raw.id : undefined;
  const replyDue = raw.type !== "error";
  const reject = (code: string, message: string): ParseResult => ({ ok: false, reply: errorMessage(id, code, message), replyDue });
  if (typeof raw.v !== "number") {
    return reject("bad-message", "Message has no numeric version.");
  }

  if (!Number.isInteger(raw.v) || raw.v !== PROTOCOL_VERSION) {
    return reject("unsupported-version", "Unsupported protocol version.");
  }

  if (!isString(raw.type)) {
    return reject("bad-message", "Message has no type.");
  }

  const bad = (what: string): ParseResult => reject("bad-message", `Malformed ${what} message.`);

  switch (raw.type) {
    case "config":
      if (!Array.isArray(raw.actions) || !raw.actions.every(isAction) || !Array.isArray(raw.adapters) || !raw.adapters.every(isString) || !isString(raw.shortcut) || (raw.shortcutKey !== undefined && !isString(raw.shortcutKey)) || !isHealth(raw.health)) {
        return bad("config");
      }

      return {
        ok: true,
        message: {
          v: 1,
          type: "config",
          actions: raw.actions.map((a) => ({ id: a.id, title: a.title })),
          adapters: [...raw.adapters],
          shortcut: raw.shortcut,
          ...(raw.shortcutKey === undefined ? {} : { shortcutKey: raw.shortcutKey }),
          health: { mode: raw.health.mode, modelReachable: raw.health.modelReachable },
        },
      };
    case "ping":
    case "pong":
      if (id === undefined) {
        return bad(raw.type);
      }

      return { ok: true, message: { v: 1, type: raw.type, id } };
    case "context.request":
      if (id === undefined || (raw.editorId != null && !isString(raw.editorId)) || (raw.requireFocus != null && typeof raw.requireFocus !== "boolean")) {
        return bad("context.request");
      }

      return {
        ok: true,
        message: {
          v: 1,
          type: "context.request",
          id,
          ...(isString(raw.editorId) ? { editorId: raw.editorId } : {}),
          ...(raw.requireFocus === true ? { requireFocus: true } : {}),
        },
      };
    case "apply.request":
      if (
        id === undefined ||
        !isString(raw.editorId) ||
        !isString(raw.expectedHash) ||
        !isOffsets(raw.start, raw.end) ||
        !isString(raw.text) ||
        (raw.format != null && (raw.format !== "markdown" || !isString(raw.expectedFormatHash)))
      ) {
        return bad("apply.request");
      }

      return {
        ok: true,
        message: {
          v: 1,
          type: "apply.request",
          id,
          editorId: raw.editorId,
          expectedHash: raw.expectedHash,
          start: raw.start as number,
          end: raw.end as number,
          text: raw.text,
          ...(raw.format === "markdown" && isString(raw.expectedFormatHash) ? { format: "markdown" as const, expectedFormatHash: raw.expectedFormatHash } : {}),
        },
      };
    case "testpage.request":
      if (id === undefined) {
        return bad("testpage.request");
      }

      return { ok: true, message: { v: 1, type: "testpage.request", id } };
    case "menu.show":
      if (
        id === undefined ||
        !isString(raw.menuId) ||
        !isString(raw.editorId) ||
        !Array.isArray(raw.slots) ||
        raw.slots.length !== 5 ||
        !raw.slots.every(isAction) ||
        !Array.isArray(raw.more) ||
        !raw.more.every(isAction) ||
        !Array.isArray(raw.status) ||
        !raw.status.every(isString)
      ) {
        return bad("menu.show");
      }

      return {
        ok: true,
        message: {
          v: 1,
          type: "menu.show",
          id,
          menuId: raw.menuId,
          editorId: raw.editorId,
          slots: raw.slots.map((a) => ({ id: a.id, title: a.title })),
          more: raw.more.map((a) => ({ id: a.id, title: a.title })),
          status: [...raw.status],
        },
      };
    case "menu.update":
      if (!isString(raw.menuId) || !isMenuUpdate(raw) || (raw.result != null && !isString(raw.result))) {
        return bad("menu.update");
      }

      return { ok: true, message: toMenuUpdate(raw.menuId, raw) };
    case "menu.close":
      if (!isString(raw.menuId)) {
        return bad("menu.close");
      }

      return { ok: true, message: menuClose(raw.menuId) };
    case "error":
      if (!isErrorPayload(raw.error)) {
        return bad("error");
      }

      return { ok: true, message: errorMessage(id, raw.error.code, raw.error.message) };
    default:
      return reject("unknown-type", "Unknown message type.");
  }
}
