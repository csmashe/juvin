// Messages exchanged inside the extension (background, content script, popup). Never sent to the app.

import type { ApplyErrorCode, Capture, ContextErrorCode, ErrorPayload } from "./protocol";

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
}

export type ContentRequest = CaptureRequest | ApplyRequest;

export type CaptureResult = { ok: true; capture: Capture } | { ok: false; error: ErrorPayload<ContextErrorCode> };

export type ApplyResult = { ok: true } | { ok: false; error: ErrorPayload<ApplyErrorCode> };

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
  return kind === "juvin.capture" || kind === "juvin.apply";
}

export function isPopupRequest(value: unknown): value is PopupRequest {
  const kind = (value as { kind?: unknown } | null)?.kind;
  return kind === "juvin.status" || kind === "juvin.connect" || kind === "juvin.ping";
}
