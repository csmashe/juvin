import browser, { type Runtime } from "webextension-polyfill";
import { detectBrowser, type BrowserIdentity, type UaBrand } from "../shared/browser-detect";
import { log, warn } from "../shared/log";
import { hello, HOST_NAME, parseIncoming, ping, pong, type ConfigMessage, type IncomingMessage, type OutgoingMessage } from "../shared/protocol";
import type { Status } from "../shared/runtime-messages";

const CONNECT_TIMEOUT_MS = 30_000;
const PING_TIMEOUT_MS = 5_000;
const BADGE_COLOR = "#F5A623";

/** Sends a reply on the connection that delivered the request; a no-op once that connection is gone. */
export type Reply = (message: OutgoingMessage) => void;

type RequestHandler = (message: IncomingMessage, reply: Reply) => void;

interface Waiter {
  resolve: () => void;
  reject: (error: Error) => void;
}

async function browserIdentity(): Promise<BrowserIdentity> {
  const brands = (navigator as Navigator & { userAgentData?: { brands?: UaBrand[] } }).userAgentData?.brands;
  const getBrowserInfo = (browser.runtime as Partial<Runtime.Static>).getBrowserInfo;
  const browserInfo = getBrowserInfo ? await getBrowserInfo() : undefined;
  return detectBrowser({ userAgent: navigator.userAgent, brands, browserInfo });
}

/**
 * The one native messaging port to the app. Connects on demand with no retry loop; a drop sets the badge
 * and the next user action reconnects. The received config lives in memory only.
 */
export class Connection {
  private port?: Runtime.Port;
  private config?: ConfigMessage;
  private lastError?: string;
  private waiters: Waiter[] = [];
  private readonly pendingPings = new Map<string, Waiter>();
  private nextId = 1;
  private generation_ = 0;

  constructor(private readonly onRequest: RequestHandler) {}

  status(): Status {
    return { connected: this.config !== undefined, connecting: this.port !== undefined && this.config === undefined, lastError: this.lastError, shortcut: this.config?.shortcut };
  }

  /** Counts connections; replies and queued work from an earlier generation are stale. */
  get generation(): number {
    return this.generation_;
  }

  /** Resolves once the app has answered hello with config. */
  ensureConnected(): Promise<void> {
    if (this.port && this.config) {
      return Promise.resolve();
    }

    const ready = new Promise<void>((resolve, reject) => this.waiters.push({ resolve, reject }));
    if (!this.port) {
      void this.open();
    }

    return ready;
  }

  send(message: OutgoingMessage): void {
    if (!this.port) {
      warn("send.dropped", { type: message.type });
      return;
    }

    this.port.postMessage(message);
  }

  async ping(): Promise<number> {
    await this.ensureConnected();
    const id = `p${this.nextId++}`;
    const started = performance.now();
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingPings.delete(id);
        reject(new Error("Juvin did not answer the ping."));
      }, PING_TIMEOUT_MS);
      this.pendingPings.set(id, {
        resolve: () => {
          clearTimeout(timer);
          resolve();
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      this.send(ping(id));
    });
    return Math.round(performance.now() - started);
  }

  private async open(): Promise<void> {
    let port: Runtime.Port;
    try {
      port = browser.runtime.connectNative(HOST_NAME);
    } catch (e) {
      this.fail(e instanceof Error ? e.message : "Could not start the Juvin host.");
      return;
    }

    this.port = port;
    const generation = ++this.generation_;
    const timer = setTimeout(() => {
      if (this.port === port && !this.config) {
        port.disconnect();
        this.dropped(port, "Juvin did not answer. Is the Juvin app running?");
      }
    }, CONNECT_TIMEOUT_MS);

    port.onMessage.addListener((raw: unknown) => this.receive(raw, generation));
    port.onDisconnect.addListener((p: Runtime.Port) => {
      clearTimeout(timer);
      this.dropped(port, p.error?.message ?? browser.runtime.lastError?.message ?? "The connection to Juvin closed.");
    });

    log("connect.start", { host: HOST_NAME });
    const manifest = browser.runtime.getManifest();
    const identity = await browserIdentity();
    if (this.port === port) {
      port.postMessage(hello({ id: browser.runtime.id, version: manifest.version }, identity));
    }
  }

  private receive(raw: unknown, generation: number): void {
    const parsed = parseIncoming(raw);
    if (!parsed.ok) {
      warn("receive.invalid", { code: parsed.reply.error.code });
      if (parsed.replyDue) {
        this.send(parsed.reply);
      }

      return;
    }

    const message = parsed.message;
    switch (message.type) {
      case "config":
        log("config", { actions: message.actions.length, adapters: message.adapters.length, mode: message.health.mode });
        this.config = message;
        this.connected();
        return;
      case "ping":
        this.send(pong(message.id));
        return;
      case "pong":
        this.pendingPings.get(message.id)?.resolve();
        this.pendingPings.delete(message.id);
        return;
      case "error":
        warn("app.error", { id: message.id, code: message.error.code });
        return;
      default:
        this.onRequest(message, (reply) => {
          if (generation === this.generation_ && this.port) {
            this.send(reply);
          } else {
            warn("reply.stale", { type: reply.type });
          }
        });
    }
  }

  private connected(): void {
    this.lastError = undefined;
    void browser.action.setBadgeText({ text: "" });
    const waiters = this.waiters;
    this.waiters = [];
    waiters.forEach((w) => w.resolve());
  }

  private dropped(port: Runtime.Port, reason: string): void {
    if (this.port !== port) {
      return;
    }

    this.port = undefined;
    this.config = undefined;
    this.pendingPings.forEach((w) => w.reject(new Error(reason)));
    this.pendingPings.clear();
    this.fail(reason);
  }

  private fail(reason: string): void {
    warn("connect.lost", { reason });
    this.lastError = reason;
    void browser.action.setBadgeText({ text: "!" });
    void browser.action.setBadgeBackgroundColor({ color: BADGE_COLOR });
    const waiters = this.waiters;
    this.waiters = [];
    waiters.forEach((w) => w.reject(new Error(reason)));
  }
}
