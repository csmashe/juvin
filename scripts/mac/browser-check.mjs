#!/usr/bin/env node
// The browser round trip of M-12 without key presses. Starts a fresh Chrome or Firefox profile, loads
// the built extension, and watches the app's log: the extension connects through the registered
// juvin-host, a capture on the bundled test page arrives, and quitting the browser disconnects it and
// leaves no new juvin-host running.
//
// Chrome: branded Chrome ignores --load-extension, so the extension is loaded over CDP with
// Extensions.loadUnpacked (needs --enable-unsafe-extension-debugging and a non-default profile).
// Chrome commands cannot be fired from outside, so its capture goes through `juvin --trigger`, which
// only captures when no system hook listens and the browser has focus; otherwise the capture is
// noted as not checked. Firefox: Marionette installs the extension as a temporary add-on and fires
// the extension command by dispatching a command event on its key element, which needs no focus.
//
// Usage: node browser-check.mjs chrome|firefox --binary PATH --extension DIR --app-log FILE
//        --app-exe PATH --out DIR [--port N] [--timeout SECONDS]
// Prints progress, then NOTE: lines for the summary. Exits 0 on pass, 1 on failure.
import { execFileSync, spawn } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { connect } from "node:net";
import { homedir, tmpdir } from "node:os";
import { join } from "node:path";

const CHROME_ID = "okfcbcoboinjniofikfnmllkbaeadelo";
const FIREFOX_ID = "extension@juvin.app";
const MANIFEST = "com.juvin.host.json";
const EXPECTED_CAPTURE = { chars: 161, messages: 3, completeness: "EntireThread" };
const CAPTURE_LINE = /Captured (\d+) draft chars, (\d+) messages, (\w+), adapter (\S+)\./;

const [browserName, ...rest] = process.argv.slice(2);
const args = {};
for (let i = 0; i < rest.length; i += 2) {
  args[rest[i].replace(/^--/, "")] = rest[i + 1];
}
for (const required of ["binary", "extension", "app-log", "app-exe", "out"]) {
  if (!args[required]) {
    console.error(`missing --${required}`);
    process.exit(2);
  }
}
const timeoutMs = Number(args.timeout ?? 60) * 1000;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const note = (text) => console.log(`NOTE: ${text}`);
const log = (text) => console.log(`[${new Date().toISOString().slice(11, 19)}] ${text}`);

class Failure extends Error {}
const fail = (text) => {
  throw new Failure(text);
};

// Reads the app log by line, keeping lines a wait did not consume for the next wait.
class AppLog {
  constructor(path) {
    this.path = path;
    // The text after the last newline is an unfinished line, so it is not counted yet.
    this.cursor = this.lines().length - 1;
  }
  lines() {
    return existsSync(this.path) ? readFileSync(this.path, "utf8").split("\n") : [""];
  }
  // Echoes each new line once with the time it was seen, so a slow step shows where the time went.
  echoNew(lines) {
    for (this.echoed ??= this.cursor; this.echoed < lines.length - 1; this.echoed++) {
      log(`app: ${lines[this.echoed]}`);
    }
  }
  async waitFor(pattern, ms = timeoutMs) {
    const deadline = Date.now() + ms;
    while (Date.now() < deadline) {
      const lines = this.lines();
      this.echoNew(lines);
      for (let i = this.cursor; i < lines.length - 1; i++) {
        const match = pattern.exec(lines[i]);
        if (match) {
          this.cursor = i + 1;
          return match;
        }
      }
      await sleep(250);
    }
    return null;
  }
}

function hostPids() {
  try {
    return new Set(execFileSync("pgrep", ["-x", "juvin-host"], { encoding: "utf8" }).split("\n").filter(Boolean));
  } catch {
    return new Set();
  }
}

async function noNewHost(before) {
  for (let i = 0; i < 20; i++) {
    const left = [...hostPids()].filter((pid) => !before.has(pid));
    if (left.length === 0) {
      return;
    }
    await sleep(500);
  }
  fail("a juvin-host started by this browser is still running after the browser quit");
}

// A browser can still be writing its profile for a moment after it exits.
function removeProfile(profile) {
  try {
    rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
  } catch (error) {
    log(`could not remove ${profile}: ${error.code ?? error.message}`);
  }
}

function launch(binary, argv, outFile) {
  const child = spawn(binary, argv, { stdio: ["ignore", "pipe", "pipe"] });
  const chunks = [];
  child.stdout.on("data", (data) => chunks.push(data));
  child.stderr.on("data", (data) => chunks.push(data));
  child.on("exit", () => writeFileSync(outFile, Buffer.concat(chunks)));
  return child;
}

async function waitForExit(child, ms = 20000) {
  if (child.exitCode !== null) {
    return true;
  }
  return Promise.race([new Promise((resolve) => child.once("exit", () => resolve(true))), sleep(ms).then(() => false)]);
}

// Checks one capture line against the test page's draft and thread.
function checkCapture(match) {
  const [, chars, messages, completeness, adapter] = match;
  const ok =
    Number(chars) === EXPECTED_CAPTURE.chars &&
    Number(messages) === EXPECTED_CAPTURE.messages &&
    completeness === EXPECTED_CAPTURE.completeness &&
    adapter === "test-page";
  return { ok, text: `${chars} draft chars, ${messages} messages, ${completeness}, adapter ${adapter}` };
}

const SELECT_DRAFT = `
  const editor = document.getElementById("juvin-test-editor");
  if (!editor) { return -1; }
  editor.focus();
  const range = document.createRange();
  range.selectNodeContents(editor);
  const selection = window.getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
  return selection.toString().length;`;

async function chrome(appLog) {
  const port = Number(args.port ?? 9333);
  const profile = mkdtempSync(join(tmpdir(), "juvin-kit-chrome-"));
  // A non-default profile reads its own NativeMessagingHosts folder, so the registered manifest goes there too.
  const registered = join(homedir(), "Library/Application Support/Google/Chrome/NativeMessagingHosts", MANIFEST);
  if (!existsSync(registered)) {
    fail(`no Chrome host manifest at ${registered}`);
  }
  mkdirSync(join(profile, "NativeMessagingHosts"), { recursive: true });
  copyFileSync(registered, join(profile, "NativeMessagingHosts", MANIFEST));

  const before = hostPids();
  const child = launch(
    args.binary,
    [
      `--user-data-dir=${profile}`,
      `--remote-debugging-port=${port}`,
      "--enable-unsafe-extension-debugging",
      "--remote-allow-origins=*",
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-search-engine-choice-screen",
      "--use-mock-keychain",
      "--password-store=basic",
      "about:blank",
    ],
    join(args.out, "chrome-output.log"),
  );
  try {
    let version;
    for (let i = 0; i < 60 && !version; i++) {
      try {
        version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
      } catch {
        await sleep(500);
      }
    }
    if (!version) {
      fail("Chrome did not open its DevTools port within 30 seconds");
    }
    log(`${version.Browser} is up`);

    const socket = new WebSocket(version.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = () => reject(new Failure("could not open the DevTools socket"));
    });
    let nextId = 1;
    const pending = new Map();
    socket.onmessage = (event) => {
      const message = JSON.parse(event.data);
      if (message.id && pending.has(message.id)) {
        pending.get(message.id)(message);
        pending.delete(message.id);
      }
    };
    const send = (method, params = {}, sessionId) =>
      new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, (message) =>
          message.error ? reject(new Failure(`${method}: ${message.error.message}`)) : resolve(message.result),
        );
        socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
      });

    const loaded = await send("Extensions.loadUnpacked", { path: args.extension });
    log(`loaded the extension as ${loaded.id}`);
    note(`Chrome extension id ${loaded.id}`);
    if (loaded.id !== CHROME_ID) {
      fail(`the extension loaded as ${loaded.id}, not the pinned ${CHROME_ID}`);
    }
    const connected = await appLog.waitFor(/\] chrome (\S+) connected/);
    if (!connected) {
      fail("the app log shows no chrome connection within the timeout");
    }
    note(`chrome ${connected[1]} connected`);

    const { targetId } = await send("Target.createTarget", { url: `chrome-extension://${CHROME_ID}/test-page.html` });
    const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
    let selected = -1;
    for (let i = 0; i < 40 && selected < 0; i++) {
      const result = await send(
        "Runtime.evaluate",
        { expression: `(() => { if (document.readyState !== "complete") { return -1; } ${SELECT_DRAFT} })()`, returnByValue: true },
        sessionId,
      );
      selected = result.result.value;
      if (selected < 0) {
        await sleep(250);
      }
    }
    if (selected <= 0) {
      fail("the test page did not load or its draft could not be selected");
    }
    await send("Page.bringToFront", {}, sessionId);
    await sleep(500);
    try {
      execFileSync(args["app-exe"], ["--trigger"], { stdio: "ignore", timeout: 10000 });
    } catch {
      note("Chrome capture not checked: juvin --trigger failed");
    }
    const outcome = await appLog.waitFor(
      /Captured \d+ draft chars.*|Capture declined \(([\w-]+)\)|Trigger ignored while (\S+) listens|Hotkey capture with no browser/,
      15000,
    );
    const capture = outcome && CAPTURE_LINE.exec(outcome[0]);
    if (capture) {
      const checked = checkCapture(capture);
      if (!checked.ok) {
        fail(`Chrome captured ${checked.text}, expected ${EXPECTED_CAPTURE.chars} chars, 3 messages, EntireThread, test-page`);
      }
      note(`Chrome capture through --trigger: ${checked.text}`);
    } else {
      note(`Chrome capture not checked: ${outcome ? outcome[0].replace(/^.*\] /, "") : "no capture line within 15 seconds"}`);
    }

    await send("Browser.close").catch(() => {});
    socket.close();
    if (!(await appLog.waitFor(/\] chrome disconnected/, 20000))) {
      fail("the app log shows no chrome disconnect after Chrome quit");
    }
    await waitForExit(child);
    await noNewHost(before);
    note("Chrome quit: the app logged the disconnect and no juvin-host was left");
  } finally {
    if (child.exitCode === null) {
      child.kill("SIGKILL");
      await waitForExit(child, 5000);
    }
    removeProfile(profile);
  }
}

// Marionette frames are "<byte length>:<json>"; commands are [0, id, name, params], replies [1, id, error, result].
class Marionette {
  static async open(port, ms) {
    const deadline = Date.now() + ms;
    while (Date.now() < deadline) {
      try {
        const client = new Marionette(await new Promise((resolve, reject) => {
          const socket = connect(port, "127.0.0.1", () => resolve(socket));
          socket.once("error", reject);
        }));
        await client.next();
        return client;
      } catch {
        await sleep(500);
      }
    }
    fail(`Marionette did not answer on port ${port}`);
  }
  constructor(socket) {
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    this.waiters = [];
    this.frames = [];
    this.id = 1;
    socket.on("data", (data) => {
      this.buffer = Buffer.concat([this.buffer, data]);
      for (;;) {
        const colon = this.buffer.indexOf(":");
        if (colon < 0) {
          break;
        }
        const length = Number(this.buffer.subarray(0, colon).toString());
        if (this.buffer.length < colon + 1 + length) {
          break;
        }
        const frame = JSON.parse(this.buffer.subarray(colon + 1, colon + 1 + length).toString());
        this.buffer = this.buffer.subarray(colon + 1 + length);
        const waiter = this.waiters.shift();
        if (waiter) {
          waiter(frame);
        } else {
          this.frames.push(frame);
        }
      }
    });
  }
  next() {
    return this.frames.length ? Promise.resolve(this.frames.shift()) : new Promise((resolve) => this.waiters.push(resolve));
  }
  async send(name, params = {}) {
    const body = Buffer.from(JSON.stringify([0, this.id++, name, params]));
    this.socket.write(`${body.length}:`);
    this.socket.write(body);
    const [, , error, result] = await this.next();
    if (error) {
      fail(`${name}: ${error.error ?? ""} ${error.message ?? JSON.stringify(error)}`);
    }
    return result;
  }
  script(script) {
    return this.send("WebDriver:ExecuteScript", { script, args: [] }).then((result) => result.value);
  }
}

async function firefox(appLog) {
  const port = Number(args.port ?? 2829);
  const profile = mkdtempSync(join(tmpdir(), "juvin-kit-firefox-"));
  writeFileSync(
    join(profile, "user.js"),
    [
      ["marionette.port", port],
      ["browser.shell.checkDefaultBrowser", false],
      ["browser.aboutwelcome.enabled", false],
      ["browser.startup.homepage_override.mstone", "ignore"],
      ["datareporting.policy.dataSubmissionEnabled", false],
      ["toolkit.telemetry.reportingpolicy.firstRun", false],
      ["app.update.disabledForTesting", true],
      ["browser.tabs.warnOnClose", false],
    ]
      .map(([name, value]) => `user_pref(${JSON.stringify(name)}, ${JSON.stringify(value)});`)
      .join("\n") + "\n",
  );
  const before = hostPids();
  const child = launch(
    args.binary,
    ["-marionette", "-remote-allow-system-access", "-no-remote", "-profile", profile, "about:blank"],
    join(args.out, "firefox-output.log"),
  );
  try {
    const client = await Marionette.open(port, 60000);
    const session = await client.send("WebDriver:NewSession", { capabilities: {} });
    log(`Firefox ${session.capabilities?.browserVersion ?? ""} is up`);
    const installed = await client.send("Addon:Install", { path: args.extension, temporary: true });
    log(`installed ${installed.value ?? installed}`);
    if ((installed.value ?? installed) !== FIREFOX_ID) {
      fail(`the add-on installed as ${installed.value ?? installed}, not ${FIREFOX_ID}`);
    }
    const connected = await appLog.waitFor(/\] firefox (\S+) connected/);
    if (!connected) {
      fail("the app log shows no firefox connection within the timeout");
    }
    note(`firefox ${connected[1]} connected`);

    await client.send("Marionette:SetContext", { value: "chrome" });
    const uuids = JSON.parse(await client.script('return Services.prefs.getStringPref("extensions.webextensions.uuids", "{}");'));
    const uuid = uuids[FIREFOX_ID];
    if (!uuid) {
      fail("Firefox has no internal UUID for the extension");
    }
    await client.send("Marionette:SetContext", { value: "content" });
    await client.send("WebDriver:Navigate", { url: `moz-extension://${uuid}/test-page.html` });
    const selected = await client.script(SELECT_DRAFT);
    if (selected <= 0) {
      fail("the test page's draft could not be selected");
    }

    await client.send("Marionette:SetContext", { value: "chrome" });
    const fired = await client.script(`
      const keyset = document.getElementById("ext-keyset-id-extension_juvin_app");
      const key = keyset && keyset.querySelector("key");
      if (!key) { return null; }
      key.dispatchEvent(new window.Event("command", { bubbles: true }));
      return key.id || "key";`);
    if (!fired) {
      fail("Firefox has no key element for the extension command");
    }
    const outcome = await appLog.waitFor(/Captured \d+ draft chars.*|Capture declined \(([\w-]+)\)/, 20000);
    if (!outcome) {
      fail("no capture line within 20 seconds of the extension command");
    }
    const capture = CAPTURE_LINE.exec(outcome[0]);
    if (!capture) {
      fail(`the capture was declined: ${outcome[1]}`);
    }
    const checked = checkCapture(capture);
    if (!checked.ok) {
      fail(`Firefox captured ${checked.text}, expected ${EXPECTED_CAPTURE.chars} chars, 3 messages, EntireThread, test-page`);
    }
    note(`Firefox capture through the extension command: ${checked.text}`);

    await client.send("Marionette:Quit", { flags: ["eForceQuit"] }).catch(() => {});
    if (!(await appLog.waitFor(/\] firefox disconnected/, 20000))) {
      fail("the app log shows no firefox disconnect after Firefox quit");
    }
    await waitForExit(child);
    await noNewHost(before);
    note("Firefox quit: the app logged the disconnect and no juvin-host was left");
  } finally {
    if (child.exitCode === null) {
      child.kill("SIGKILL");
      await waitForExit(child, 5000);
    }
    removeProfile(profile);
  }
}

const checks = { chrome, firefox };
if (!checks[browserName]) {
  console.error("Usage: node browser-check.mjs chrome|firefox --binary PATH --extension DIR --app-log FILE --app-exe PATH");
  process.exit(2);
}
try {
  mkdirSync(args.out, { recursive: true });
  await checks[browserName](new AppLog(args["app-log"]));
  log("pass");
  process.exit(0);
} catch (error) {
  console.log(`FAIL: ${error instanceof Failure ? error.message : error.stack}`);
  process.exit(1);
}
