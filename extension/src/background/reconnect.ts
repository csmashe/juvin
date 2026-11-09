import browser, { type Alarms } from "webextension-polyfill";
import { log } from "../shared/log";
import type { Status } from "../shared/runtime-messages";

export const RECONNECT_ALARM = "juvin.reconnect";

// Chrome fires an alarm no sooner than 30 seconds after it is set; Firefox has no floor.
export const BACKOFF_MINUTES = [0.5, 1, 2, 5];

export const ACTIVITY_THROTTLE_MS = 5_000;

// Chrome's errors for a host that is not registered or not allowed, and Firefox's single message for both.
const HOST_MISSING = /native messaging host (not found|is forbidden)|invalid native messaging host name|no such native application/i;

/** True when retrying cannot help until the host is registered; the user's next action tries again. */
export function isHostMissing(reason: string | undefined): boolean {
  return reason !== undefined && HOST_MISSING.test(reason);
}

export interface Reconnectable {
  status(): Status;
  ensureConnected(): Promise<void>;
}

/**
 * Reconnects after the app restarts while nothing in the browser would: an alarm that backs off from 30 seconds to
 * every 5 minutes, and browser activity at most once per throttle window. The backoff step lives in the alarm's
 * period, so it survives the background being unloaded while idle.
 */
export class Reconnector {
  private lastActivityAttempt = -Infinity;

  constructor(
    private readonly connection: Reconnectable,
    private readonly now: () => number = Date.now,
  ) {}

  /** Starts the backoff unless it is already running. */
  async failed(reason: string): Promise<void> {
    if (isHostMissing(reason)) {
      await browser.alarms.clear(RECONNECT_ALARM);
      return;
    }

    if (!(await browser.alarms.get(RECONNECT_ALARM))) {
      this.schedule(BACKOFF_MINUTES[0]!);
    }
  }

  connected(): void {
    void browser.alarms.clear(RECONNECT_ALARM);
  }

  activity(trigger: string): void {
    const status = this.connection.status();
    if (status.connected || status.connecting || isHostMissing(status.lastError)) {
      return;
    }

    const now = this.now();
    if (now - this.lastActivityAttempt < ACTIVITY_THROTTLE_MS) {
      return;
    }

    this.lastActivityAttempt = now;
    this.attempt(trigger);
  }

  alarm(alarm: Alarms.Alarm): void {
    if (alarm.name !== RECONNECT_ALARM) {
      return;
    }

    const status = this.connection.status();
    if (status.connected) {
      this.connected();
      return;
    }

    // Advanced before the attempt, so an attempt cut short by the background unloading still leaves the next one.
    const step = BACKOFF_MINUTES.indexOf(alarm.periodInMinutes ?? 0);
    this.schedule(BACKOFF_MINUTES[Math.min(step + 1, BACKOFF_MINUTES.length - 1)]!);
    if (!status.connecting) {
      this.attempt("alarm");
    }
  }

  private schedule(minutes: number): void {
    if (this.connection.status().connected) {
      return;
    }

    void browser.alarms.create(RECONNECT_ALARM, { delayInMinutes: minutes, periodInMinutes: minutes });
  }

  private attempt(trigger: string): void {
    log("reconnect", { trigger });
    this.connection.ensureConnected().catch(() => log("connect.failed", { trigger }));
  }
}
