// Wire types and helpers for docs/connector-protocol.md (protocol version 1).

export const PROTOCOL_VERSION = 1;
export const HOST_NAME = "com.juvin.host";
export const FRAME_CAP_BYTES = 1_048_576;

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

export type ApplyErrorCode = "draft-changed" | "editor-gone" | "internal";

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
  collapsedCount: number;
  messages: ThreadMessage[];
}

export interface Capture {
  adapter: string;
  pageUrl: string;
  capturedAt: string;
  subject?: string;
  editor: { id: string; hash: string };
  selection: { text: string; start: number; end: number };
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

export type OutgoingMessage =
  | HelloMessage
  | ShortcutPressedMessage
  | PingMessage
  | PongMessage
  | ContextResponseMessage
  | ApplyResponseMessage
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

export function errorMessage(id: string | undefined, code: string, message: string): ErrorMessage {
  return id === undefined ? { v: 1, type: "error", error: { code, message } } : { v: 1, type: "error", id, error: { code, message } };
}

export type ParseResult = { ok: true; message: IncomingMessage } | { ok: false; reply: ErrorMessage };

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

function isOffsets(start: unknown, end: unknown): boolean {
  return typeof start === "number" && typeof end === "number" && Number.isInteger(start) && Number.isInteger(end) && start >= 0 && end >= start;
}

function isErrorPayload(value: unknown): value is ErrorPayload {
  return isRecord(value) && isString(value.code) && isString(value.message);
}

/** Validates a message received from the app. Unknown properties are ignored, as the spec requires. */
export function parseIncoming(raw: unknown): ParseResult {
  if (!isRecord(raw)) {
    return { ok: false, reply: errorMessage(undefined, "bad-message", "Message is not a JSON object.") };
  }

  const id = isString(raw.id) ? raw.id : undefined;
  if (typeof raw.v !== "number") {
    return { ok: false, reply: errorMessage(id, "bad-message", "Message has no numeric version.") };
  }

  if (raw.v !== PROTOCOL_VERSION) {
    return { ok: false, reply: errorMessage(id, "unsupported-version", "Unsupported protocol version.") };
  }

  if (!isString(raw.type)) {
    return { ok: false, reply: errorMessage(id, "bad-message", "Message has no type.") };
  }

  const bad = (what: string): ParseResult => ({ ok: false, reply: errorMessage(id, "bad-message", `Malformed ${what} message.`) });

  switch (raw.type) {
    case "config":
      if (!Array.isArray(raw.actions) || !raw.actions.every(isAction) || !Array.isArray(raw.adapters) || !raw.adapters.every(isString) || !isString(raw.shortcut) || !isHealth(raw.health)) {
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
      if (id === undefined || !isString(raw.editorId) || !isString(raw.expectedHash) || !isOffsets(raw.start, raw.end) || !isString(raw.text)) {
        return bad("apply.request");
      }

      return {
        ok: true,
        message: { v: 1, type: "apply.request", id, editorId: raw.editorId, expectedHash: raw.expectedHash, start: raw.start as number, end: raw.end as number, text: raw.text },
      };
    case "error":
      if (!isErrorPayload(raw.error)) {
        return bad("error");
      }

      return { ok: true, message: errorMessage(id, raw.error.code, raw.error.message) };
    default:
      return { ok: false, reply: errorMessage(id, "unknown-type", "Unknown message type.") };
  }
}
