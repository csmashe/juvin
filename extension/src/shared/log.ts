// Logs carry types, ids, lengths, counts, and error codes only; never email content.
export type LogFields = Record<string, string | number | boolean | undefined>;

export function log(event: string, fields: LogFields = {}): void {
  console.info(`[juvin] ${event}`, fields);
}

export function warn(event: string, fields: LogFields = {}): void {
  console.warn(`[juvin] ${event}`, fields);
}
