import { contextOk, FRAME_CAP_BYTES, type Capture, type ContextResponseMessage } from "../shared/protocol";

// Headroom for differences between JSON.stringify and the browser's own serializer.
const SERIALIZER_HEADROOM_BYTES = 1024;

const encoder = new TextEncoder();

export function byteLength(value: unknown): number {
  return encoder.encode(JSON.stringify(value)).length;
}

/**
 * Builds the success response, dropping the oldest thread messages until the frame fits under the cap.
 * Returns null when even an empty thread does not fit (the selection alone is too large).
 */
export function buildTrimmedContextResponse(
  id: string,
  capture: Capture,
  capBytes: number = FRAME_CAP_BYTES - SERIALIZER_HEADROOM_BYTES,
): ContextResponseMessage | null {
  let response = contextOk(id, capture);
  let size = byteLength(response);
  if (size <= capBytes) {
    return response;
  }

  const messages = [...capture.thread.messages];
  // Drop by estimated size first, then confirm with a real measurement. The trimmed thread reports
  // "truncated", so the estimate counts that completeness value instead of the original one.
  size += byteLength("truncated") - byteLength(capture.thread.completeness);
  while (messages.length > 0 && size > capBytes) {
    size -= byteLength(messages.shift()) + 1;
  }

  for (;;) {
    response = contextOk(id, { ...capture, thread: { ...capture.thread, completeness: "truncated", messages } });
    if (byteLength(response) <= capBytes) {
      return response;
    }

    if (messages.length === 0) {
      return null;
    }

    messages.shift();
  }
}
