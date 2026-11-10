import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Status } from "../src/shared/runtime-messages";

interface FakeAlarm {
  name: string;
  scheduledTime: number;
  periodInMinutes?: number;
  delayInMinutes?: number;
}

const alarms = new Map<string, FakeAlarm>();

vi.mock("webextension-polyfill", () => ({
  default: {
    alarms: {
      create: (name: string, info: { delayInMinutes?: number; periodInMinutes?: number }) => {
        alarms.set(name, { name, scheduledTime: 0, ...info });
        return Promise.resolve();
      },
      get: (name: string) => Promise.resolve(alarms.get(name)),
      clear: (name: string) => Promise.resolve(alarms.delete(name)),
    },
  },
}));

const { ACTIVITY_THROTTLE_MS, BACKOFF_MINUTES, RECONNECT_ALARM, Reconnector, isHostMissing } = await import("../src/background/reconnect");
const { VERSION_REJECTED } = await import("../src/background/connection");

class FakeConnection {
  state: Status = { connected: false, connecting: false };
  attempts = 0;

  status(): Status {
    return this.state;
  }

  /** Like the real connection, opens a port only when there is none. */
  ensureConnected(): Promise<void> {
    if (this.state.connected) {
      return Promise.resolve();
    }

    if (!this.state.connecting) {
      this.attempts++;
    }

    return Promise.reject(new Error("Native host has exited."));
  }
}

async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe("Reconnector", () => {
  let connection: FakeConnection;
  let now: number;
  let reconnector: InstanceType<typeof Reconnector>;

  beforeEach(() => {
    alarms.clear();
    vi.spyOn(console, "info").mockImplementation(() => undefined);
    connection = new FakeConnection();
    now = 1_000_000;
    reconnector = new Reconnector(connection, () => now);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function fire(): void {
    reconnector.alarm(alarms.get(RECONNECT_ALARM)!);
  }

  it("backs off from 30 seconds to every 5 minutes, trying on each alarm", async () => {
    await reconnector.failed("Native host has exited.");
    const periods = [alarms.get(RECONNECT_ALARM)?.periodInMinutes];
    for (let i = 0; i < 5; i++) {
      fire();
      // The failed attempt keeps the schedule the alarm already advanced.
      await reconnector.failed("Native host has exited.");
      periods.push(alarms.get(RECONNECT_ALARM)?.periodInMinutes);
    }

    expect(periods).toEqual([0.5, 1, 2, 5, 5, 5]);
    expect(alarms.get(RECONNECT_ALARM)?.delayInMinutes).toBe(5);
    expect(connection.attempts).toBe(5);
    expect(BACKOFF_MINUTES[0]).toBeGreaterThanOrEqual(0.5);
  });

  it("starts over after a connect", async () => {
    await reconnector.failed("Native host has exited.");
    fire();
    fire();
    expect(alarms.get(RECONNECT_ALARM)?.periodInMinutes).toBe(2);

    reconnector.connected();
    await settle();
    expect(alarms.size).toBe(0);

    await reconnector.failed("The connection to Juvin closed.");
    expect(alarms.get(RECONNECT_ALARM)?.periodInMinutes).toBe(0.5);
  });

  it("clears an alarm that fires once connected", async () => {
    await reconnector.failed("Native host has exited.");
    connection.state = { connected: true, connecting: false };
    fire();
    await settle();

    expect(alarms.size).toBe(0);
    expect(connection.attempts).toBe(0);
  });

  it("advances the schedule without a second attempt while one is in flight", async () => {
    await reconnector.failed("Native host has exited.");
    connection.state = { connected: false, connecting: true };
    fire();

    expect(alarms.get(RECONNECT_ALARM)?.periodInMinutes).toBe(1);
    expect(connection.attempts).toBe(0);
  });

  it("ignores other alarms", async () => {
    reconnector.alarm({ name: "other", scheduledTime: 0 });

    expect(alarms.size).toBe(0);
    expect(connection.attempts).toBe(0);
  });

  it("picks up the backoff from an alarm left by a background that was unloaded", async () => {
    alarms.set(RECONNECT_ALARM, { name: RECONNECT_ALARM, scheduledTime: 0, periodInMinutes: 2, delayInMinutes: 2 });
    const fresh = new Reconnector(connection, () => now);

    await fresh.failed("Native host has exited.");
    expect(alarms.get(RECONNECT_ALARM)?.periodInMinutes).toBe(2);

    fresh.alarm(alarms.get(RECONNECT_ALARM)!);
    expect(alarms.get(RECONNECT_ALARM)?.periodInMinutes).toBe(5);
    expect(connection.attempts).toBe(1);
  });

  it("tries once per throttle window on browser activity", () => {
    reconnector.activity("tab");
    reconnector.activity("window");
    reconnector.activity("editor");
    expect(connection.attempts).toBe(1);

    now += ACTIVITY_THROTTLE_MS - 1;
    reconnector.activity("tab");
    expect(connection.attempts).toBe(1);

    now += 1;
    reconnector.activity("tab");
    expect(connection.attempts).toBe(2);
  });

  it("shares the throttle window with the alarm, explicit connects, and failures", async () => {
    await reconnector.failed("Native host has exited.");
    reconnector.activity("tab");
    expect(connection.attempts).toBe(0);

    now += ACTIVITY_THROTTLE_MS;
    fire();
    reconnector.activity("tab");
    expect(connection.attempts).toBe(1);

    now += ACTIVITY_THROTTLE_MS;
    await reconnector.connect("popup").catch(() => undefined);
    expect(connection.attempts).toBe(2);
    reconnector.activity("window");
    expect(connection.attempts).toBe(2);

    await reconnector.connect("command").catch(() => undefined);
    expect(connection.attempts).toBe(3);
  });

  it("does not try on activity while connected or connecting", () => {
    connection.state = { connected: true, connecting: false };
    reconnector.activity("tab");
    connection.state = { connected: false, connecting: true };
    reconnector.activity("tab");

    expect(connection.attempts).toBe(0);
  });

  it("stops the alarm but keeps throttled activity attempts while the native host is not registered", async () => {
    await reconnector.failed("Native host has exited.");
    await reconnector.failed("Specified native messaging host not found.");
    expect(alarms.size).toBe(0);

    connection.state = { connected: false, connecting: false, lastError: "No such native application com.juvin.host" };
    reconnector.activity("tab");
    expect(connection.attempts).toBe(0);

    now += ACTIVITY_THROTTLE_MS;
    reconnector.activity("tab");
    expect(connection.attempts).toBe(1);
    await reconnector.failed("No such native application com.juvin.host");
    expect(alarms.size).toBe(0);
  });

  it("stops the alarm when the app rejects the protocol version", async () => {
    await reconnector.failed("Native host has exited.");
    await reconnector.failed(VERSION_REJECTED);
    expect(alarms.size).toBe(0);

    now += ACTIVITY_THROTTLE_MS;
    reconnector.activity("window");
    expect(connection.attempts).toBe(1);
  });

  it("stops the alarm when a portal refuses the host, and still tries on activity", async () => {
    await reconnector.failed("Native host has exited.");
    await reconnector.failed("An unexpected error occurred");
    expect(alarms.size).toBe(0);

    now += ACTIVITY_THROTTLE_MS;
    reconnector.activity("window");
    expect(connection.attempts).toBe(1);
  });

  it("tells a missing or forbidden host from one that could not reach the app", () => {
    for (const reason of [
      "Specified native messaging host not found.",
      "Access to the specified native messaging host is forbidden.",
      "Invalid native messaging host name specified.",
      "No such native application com.juvin.host",
    ]) {
      expect(isHostMissing(reason), reason).toBe(true);
    }

    for (const reason of [undefined, "Native host has exited.", "Juvin did not answer. Is the Juvin app running?", "The connection to Juvin closed.", "Error when communicating with the native messaging host.", "Failed to start native messaging host.", "An unexpected error occurred", VERSION_REJECTED]) {
      expect(isHostMissing(reason), reason).toBe(false);
    }
  });
});
