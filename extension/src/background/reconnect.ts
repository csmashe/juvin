import browser, { type Alarms } from "webextension-polyfill";
import { log } from "../shared/log";
import type { Status } from "../shared/runtime-messages";
import { VERSION_REJECTED } from "./connection";

export const RECONNECT_ALARM = "juvin.reconnect";

// Chrome fires an alarm no sooner than 30 seconds after it is set; Firefox has no floor.
export const BACKOFF_MINUTES = [0.5, 1, 2, 5] as const;

export const ACTIVITY_THROTTLE_MS = 30_000;

// Chrome's errors for a host that is not registered or not allowed, and Firefox's single message for both.
const HOST_MISSING = /native messaging host (not found|is forbidden)|invalid native messaging host name|no such native application/i;

/** True when the host is not registered; such an attempt starts no process. */
export function isHostMissing(reason: string | undefined): boolean {
  return reason !== undefined && HOST_MISSING.test(reason);
}

export interface Reconnectable {
  status(): Status;
  ensureConnected(): Promise<void>;
}

/**
 * Reconnects after the app restarts while nothing in the browser would: an alarm that backs off from 30 seconds to
 * every 5 minutes, and browser activity at most once per throttle window since the last attempt from any source. A
 * missing host or a rejected protocol version stops the alarm, since only a registration or an update can fix those.
 * The backoff step lives in the alarm's period, so it survives the background being unloaded while idle.
 */
export class Reconnector {
  private lastAttempt = -Infinity;

  constructor(
    private readonly connection: Reconnectable,
    private readonly now: () => number = () => Date.now(),
  ) {}

  /** For an explicit request (the popup, the browser command, startup): connects now, whatever the throttle. */
  connect(trigger: string): Promise<void> {
    const status = this.connection.status();
    if (!status.connected && !status.connecting) {
      this.lastAttempt = this.now();
      log("connect.start", { trigger });
    }

    return this.connection.ensureConnected();
  }

  /** Starts the backoff unless it is already running. */
  async failed(reason: string): Promise<void> {
    this.lastAttempt = this.now();
    if (isHostMissing(reason) || reason === VERSION_REJECTED) {
      await browser.alarms.clear(RECONNECT_ALARM);
      return;
    }

    if (!(await browser.alarms.get(RECONNECT_ALARM))) {
      this.schedule(BACKOFF_MINUTES[0]);
    }
  }

  connected(): void {
    void browser.alarms.clear(RECONNECT_ALARM);
  }

  activity(trigger: string): void {
    if (this.now() - this.lastAttempt >= ACTIVITY_THROTTLE_MS) {
      this.attempt(trigger);
    }
  }

  alarm(alarm: Alarms.Alarm): void {
    if (alarm.name !== RECONNECT_ALARM) {
      return;
    }

    if (this.connection.status().connected) {
      this.connected();
      return;
    }

    // Advanced before the attempt, so an attempt cut short by the background unloading still leaves the next one.
    this.schedule(BACKOFF_MINUTES.find((minutes) => minutes > (alarm.periodInMinutes ?? 0)) ?? BACKOFF_MINUTES[3]);
    this.attempt("alarm");
  }

  private schedule(minutes: number): void {
    if (this.connection.status().connected) {
      return;
    }

    void browser.alarms.create(RECONNECT_ALARM, { delayInMinutes: minutes, periodInMinutes: minutes });
  }

  private attempt(trigger: string): void {
    this.connect(trigger).catch(() => undefined);
  }
}
