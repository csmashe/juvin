// Messages exchanged inside the extension (background, content script, popup). Never sent to the app.

import type { Action, ApplyErrorCode, Capture, ContextErrorCode, ErrorPayload, MenuErrorCode, MenuUpdateMessage } from "./protocol";

export interface CaptureRequest {
  kind: "juvin.capture";
  /** Set on a recapture: only this editor may be read. */
  editorId?: string;
  /** Set only when the target is the bundled test page, which receives runtime messages instead of tab messages. */
  tabId?: number;
}

export interface ApplyRequest {
  kind: "juvin.apply";
  tabId?: number;
  editorId: string;
  expectedHash: string;
  start: number;
  end: number;
  text: string;
  format?: "markdown";
  expectedFormatHash?: string;
}

export interface MenuShowRequest {
  kind: "juvin.menu.show";
  tabId?: number;
  menuId: string;
  editorId: string;
  slots: Action[];
  more: Action[];
  status: string[];
}

export interface MenuUpdateRequest {
  kind: "juvin.menu.update";
  tabId?: number;
  update: MenuUpdateMessage;
}

export interface MenuCloseRequest {
  kind: "juvin.menu.close";
  tabId?: number;
  menuId: string;
}

export type ContentRequest = CaptureRequest | ApplyRequest | MenuShowRequest | MenuUpdateRequest | MenuCloseRequest;

/** From the content script's menu to the background: the user picked an entry. */
export interface MenuChosenNotice {
  kind: "juvin.menu.choose";
  menuId: string;
  action: string;
  instruction?: string;
}

/** From the content script's menu to the background: the user dismissed it, or its editor went away. */
export interface MenuClosedNotice {
  kind: "juvin.menu.closed";
  menuId: string;
}

export type MenuNotice = MenuChosenNotice | MenuClosedNotice;

export type CaptureResult = { ok: true; capture: Capture } | { ok: false; error: ErrorPayload<ContextErrorCode> };

export type ApplyResult = { ok: true } | { ok: false; error: ErrorPayload<ApplyErrorCode> };

export type MenuShowResult = { ok: true } | { ok: false; error: ErrorPayload<MenuErrorCode> };

export interface Status {
  connected: boolean;
  connecting: boolean;
  lastError?: string;
  shortcut?: string;
}

export type PopupRequest = { kind: "juvin.status" } | { kind: "juvin.connect" } | { kind: "juvin.ping" };

export type PingResult = { ok: true; latencyMs: number } | { ok: false; error: string };

export function isContentRequest(value: unknown): value is ContentRequest {
  const kind = (value as { kind?: unknown } | null)?.kind;
  return kind === "juvin.capture" || kind === "juvin.apply" || kind === "juvin.menu.show" || kind === "juvin.menu.update" || kind === "juvin.menu.close";
}

export function isMenuNotice(value: unknown): value is MenuNotice {
  const notice = value as { kind?: unknown; menuId?: unknown; action?: unknown; instruction?: unknown } | null;
  if (typeof notice?.menuId !== "string") {
    return false;
  }

  if (notice.kind === "juvin.menu.closed") {
    return true;
  }

  return notice.kind === "juvin.menu.choose" && typeof notice.action === "string" && (notice.instruction === undefined || typeof notice.instruction === "string");
}

export function isPopupRequest(value: unknown): value is PopupRequest {
  const kind = (value as { kind?: unknown } | null)?.kind;
  return kind === "juvin.status" || kind === "juvin.connect" || kind === "juvin.ping";
}
