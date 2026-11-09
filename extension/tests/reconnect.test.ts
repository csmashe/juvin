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

class FakeConnection {
  state: Status = { connected: false, connecting: false };
  attempts = 0;

  status(): Status {
    return this.state;
  }

  ensureConnected(): Promise<void> {
    this.attempts++;
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

  it("tries once per throttle window on browser activity", () => {
    reconnector.activity("tab");
    reconnector.activity("window");
    reconnector.activity("tab-loaded");
    expect(connection.attempts).toBe(1);

    now += ACTIVITY_THROTTLE_MS - 1;
    reconnector.activity("tab");
    expect(connection.attempts).toBe(1);

    now += 1;
    reconnector.activity("tab");
    expect(connection.attempts).toBe(2);
  });

  it("does not try on activity while connected or connecting", () => {
    connection.state = { connected: true, connecting: false };
    reconnector.activity("tab");
    connection.state = { connected: false, connecting: true };
    reconnector.activity("tab");

    expect(connection.attempts).toBe(0);
  });

  it("does not retry while the native host is not registered", async () => {
    await reconnector.failed("Native host has exited.");
    await reconnector.failed("Specified native messaging host not found.");
    expect(alarms.size).toBe(0);

    connection.state = { connected: false, connecting: false, lastError: "No such native application com.juvin.host" };
    reconnector.activity("tab");
    expect(connection.attempts).toBe(0);
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

    for (const reason of [undefined, "Native host has exited.", "Juvin did not answer. Is the Juvin app running?", "The connection to Juvin closed.", "Error when communicating with the native messaging host."]) {
      expect(isHostMissing(reason), reason).toBe(false);
    }
  });
});
