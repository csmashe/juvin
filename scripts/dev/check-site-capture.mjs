#!/usr/bin/env node
// Checks that capture still recognizes real Gmail and Outlook on the web, so a page change that breaks a
// site adapter is noticed before a user reports it. Connects to a running, signed-in Firefox or Chrome,
// loads the built extension, opens a new draft (or a reply in the conversation given), types a marker,
// selects part of it, and asks the content script for a capture the way the background does. The desktop
// app is not needed.
//
// A site passes when its own adapter answered (not the generic fallback), the conversation was read
// (not "visible text only"), the selection came back unchanged, and a reply saw at least one message.
// Only counts and pass or fail are printed, never mail content or browser error text. It types only into the
// draft its own press opened, refusing a page that already shows one, then removes the marker and discards the
// draft; a site fails when that cannot be confirmed. Nothing is sent.
//
// Start the browser headless on a copy of a signed-in profile first; a window that is covered or on another
// workspace draws no frames, and the sites' drafts and dialogs then never finish opening or closing:
//   Firefox or Zen: firefox -headless -marionette -remote-allow-system-access -no-remote -profile <copy>
//                   (with MOZ_REMOTE_ALLOW_SYSTEM_ACCESS=1 on Firefox 159 and later)
//   Chrome:         google-chrome --headless=new --remote-debugging-port=9222 --enable-unsafe-extension-debugging
//                   --remote-allow-origins=* --user-data-dir=<copy>
//
// Usage: node scripts/dev/check-site-capture.mjs firefox|chrome [--port N] [--extension DIR]
//        [--site gmail[=CONVERSATION_URL]] [--site outlook[=CONVERSATION_URL]]
// Without --site both sites are checked with a new draft. Build the extension first (npm run build).
// Exits 0 when every site passes, 1 when one fails, 2 on bad usage.
import { connect } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const FIREFOX_ID = "extension@juvin.app";
// newDraft is the page a new draft starts from, and composeButton, when set, the control that opens it there, so
// the editor can be tied to the script's own press. Outlook's compose address opens a fresh tab instead.
const SITES = {
  gmail: {
    newDraft: "https://mail.google.com/mail/u/0/#inbox",
    composeButton: `[...document.querySelectorAll('[role="button"]')].find((element) => shown(element) && element.textContent.trim() === "Compose")`,
    origins: ["https://mail.google.com/*"],
  },
  outlook: {
    newDraft: "https://outlook.live.com/mail/0/deeplink/compose",
    origins: [
      "https://outlook.live.com/*",
      "https://outlook.office.com/*",
      "https://outlook.office365.com/*",
      "https://outlook.cloud.microsoft/*",
    ],
  },
};
const ALL_ORIGINS = Object.values(SITES).flatMap((site) => site.origins);
const SELECTED = "capture check";
const marker = `Juvin ${SELECTED} ${Date.now()}`;

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const log = (text) => console.log(`[${new Date().toISOString().slice(11, 19)}] ${text}`);

class Failure extends Error {}
const fail = (text) => {
  throw new Failure(text);
};

function usage() {
  console.error(
    "Usage: node scripts/dev/check-site-capture.mjs firefox|chrome [--port N] [--extension DIR] [--site gmail[=URL]] [--site outlook[=URL]]",
  );
  process.exit(2);
}

function parseArgs(argv) {
  const [browserName, ...rest] = argv;
  if (browserName !== "firefox" && browserName !== "chrome") {
    usage();
  }
  const options = { browserName, sites: [] };
  for (let i = 0; i < rest.length; i += 2) {
    const value = rest[i + 1];
    if (value === undefined) {
      usage();
    }
    switch (rest[i]) {
      case "--port":
        options.port = Number(value);
        break;
      case "--extension":
        options.extension = resolve(value);
        break;
      case "--site": {
        const [name, ...url] = value.split("=");
        if (!SITES[name]) {
          usage();
        }
        options.sites.push({ name, conversation: url.length ? url.join("=") : undefined });
        break;
      }
      default:
        usage();
    }
  }
  if (options.sites.length === 0) {
    options.sites = Object.keys(SITES).map((name) => ({ name }));
  }
  const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "../..");
  options.extension ??= join(repoRoot, "extension/dist", browserName);
  options.port ??= browserName === "firefox" ? 2828 : 9222;
  return options;
}

// Page scripts, each a function body. Editors are found without the extension's selectors, so a broken adapter
// cannot hide behind a broken check.
const EDITORS = '[contenteditable="true"][role="textbox"]';
const COUNT_EDITORS = `return document.querySelectorAll('${EDITORS}').length;`;

// press() sends the event sequence of a real click, which some Gmail and Outlook controls need.
const HELPERS = `
  const shown = (element) => element.getBoundingClientRect().width > 0;
  const press = (element) => {
    for (const type of ["pointerdown", "mousedown", "pointerup", "mouseup", "click"]) {
      element.dispatchEvent(new (type.startsWith("pointer") ? PointerEvent : MouseEvent)(type, { bubbles: true, cancelable: true, view: window }));
    }
  };`;

// Outlook's toolbar Reply button does not open the inline editor, so the message's own Reply comes first.
const REPLY_BUTTON = `['[role="menuitem"][aria-label="Reply"]', '[aria-label="Reply"]']
  .flatMap((selector) => [...document.querySelectorAll(selector)])
  .find(shown)`;

const buttonScript = (button) => `${HELPERS} return Boolean(${button});`;
// Presses the control only while no editor is on the page, checked in the same call.
const pressScript = (button) => `${HELPERS}
  if (document.querySelector('${EDITORS}')) { return "editor"; }
  const button = ${button};
  if (!button) { return "missing"; }
  press(button);
  return "pressed";`;

// Returns the selection length, 0 when the marker was not found, -1 when no editor exists, -2 when the editor
// is hidden, -3 when more than one editor exists, and -4 when the editor does not hold the focus a press gives a
// new reply or compose window. Negative results return before anything is written.
const prepareScript = (requireFocus) => `${HELPERS}
  const editors = document.querySelectorAll('${EDITORS}');
  if (editors.length !== 1) { return editors.length ? -3 : -1; }
  const editor = editors[0];
  const rect = editor.getBoundingClientRect();
  if (rect.width < 40 || rect.height < 10) { return -2; }
  if (${requireFocus} && !(editor.closest('[role="dialog"]') ?? editor).contains(document.activeElement)) { return -4; }
  editor.setAttribute("data-juvin-check-draft", "");
  editor.focus();
  const selection = document.getSelection();
  const start = document.createRange();
  start.setStart(editor, 0);
  selection.removeAllRanges();
  selection.addRange(start);
  document.execCommand("insertText", false, ${JSON.stringify(marker)});
  const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
  let node = walker.nextNode();
  while (node && !node.data.includes(${JSON.stringify(marker)})) { node = walker.nextNode(); }
  if (!node) { return 0; }
  const offset = node.data.indexOf(${JSON.stringify(SELECTED)});
  const range = document.createRange();
  range.setStart(node, offset);
  range.setEnd(node, offset + ${SELECTED.length});
  selection.removeAllRanges();
  selection.addRange(range);
  return selection.toString().length;`;

// Removes the marker, then presses the discard control of this run's draft: the one in the nearest container
// around it, provided that container holds no other editor.
const CLEAN_UP = `${HELPERS}
  const editor = document.querySelector("[data-juvin-check-draft]");
  if (!editor) { return "no tagged editor"; }
  const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
  let node = walker.nextNode();
  while (node && !node.data.includes(${JSON.stringify(marker)})) { node = walker.nextNode(); }
  if (node) {
    editor.focus();
    const offset = node.data.indexOf(${JSON.stringify(marker)});
    const range = document.createRange();
    range.setStart(node, offset);
    range.setEnd(node, offset + ${marker.length});
    document.getSelection().removeAllRanges();
    document.getSelection().addRange(range);
    document.execCommand("delete");
  }
  for (let container = editor.parentElement; container; container = container.parentElement) {
    const discard = [...container.querySelectorAll('[role="button"][aria-label^="Discard draft"], button[aria-label="Discard"]')].find(shown);
    if (discard) {
      if (container.querySelectorAll('${EDITORS}').length > 1) { break; }
      press(discard);
      return "pressed";
    }
  }
  return "no discard";`;

const CONFIRM_BUTTON = `[...document.querySelectorAll('[role="dialog"] button, [role="alertdialog"] button')]
  .find((button) => shown(button) && /^(OK|Discard)$/.test(button.textContent.trim()))`;

const CONFIRM_DISCARD = `${HELPERS}
  const confirm = ${CONFIRM_BUTTON};
  if (confirm) { press(confirm); }
  return Boolean(confirm);`;

// A remounted editor carries no tag, so every shown editor counts, not only this run's.
const DRAFT_STATE = `${HELPERS}
  const editor = document.querySelector("[data-juvin-check-draft]");
  return {
    present: Boolean(editor),
    editorsShown: [...document.querySelectorAll('${EDITORS}')].filter(shown).length,
    confirmOpen: Boolean(${CONFIRM_BUTTON}),
  };`;

// Runs in the extension page and captures from the site's one active tab, which the script brought to the front.
function captureScript(api, origins) {
  return `
    const api = ${api};
    const tabs = await api.tabs.query({ active: true, url: ${JSON.stringify(origins)} });
    if (tabs.length !== 1) { return JSON.stringify({ activeTabs: tabs.length }); }
    const result = await api.tabs.sendMessage(tabs[0].id, { kind: "juvin.capture" });
    if (!result || !result.ok) {
      return JSON.stringify({ error: result && result.error ? result.error.code : "no answer" });
    }
    const { capture } = result;
    return JSON.stringify({
      adapter: capture.adapter,
      completeness: capture.thread.completeness,
      messages: capture.thread.messages.length,
      collapsed: capture.thread.collapsedCount ?? 0,
      selectionMatches: capture.selection.text === ${JSON.stringify(SELECTED)},
    });`;
}

function verdict(site, summary) {
  if (summary.activeTabs !== undefined) {
    return { ok: false, text: `${summary.activeTabs} active tabs of the site were found, not 1; close the others` };
  }
  if (summary.error) {
    return { ok: false, text: `capture declined (${summary.error})` };
  }
  const problems = [];
  if (summary.adapter !== site.name) {
    problems.push(`the ${summary.adapter} adapter answered`);
  }
  if (summary.completeness === "visible-text-only") {
    problems.push("only the visible text was read");
  }
  if (!summary.selectionMatches) {
    problems.push("the selection came back changed");
  }
  if (site.conversation && summary.messages === 0) {
    problems.push("the reply saw no messages");
  }
  const facts = `adapter ${summary.adapter}, ${summary.completeness}, ${summary.messages} messages, ${summary.collapsed} collapsed`;
  return problems.length ? { ok: false, text: `${problems.join("; ")} (${facts})` } : { ok: true, text: facts };
}

async function poll(read, ms, step = 500) {
  const deadline = Date.now() + ms;
  for (;;) {
    const value = await read();
    if (value || Date.now() >= deadline) {
      return value;
    }
    await sleep(step);
  }
}

async function settledEditorCount(page) {
  let last = -1;
  let steady = 0;
  for (let i = 0; i < 30 && steady < 3; i++) {
    await sleep(1000);
    const count = await page.run(COUNT_EDITORS);
    steady = count === last ? steady + 1 : 0;
    last = count;
  }
  return last;
}

// Types the marker, selects part of it, and captures. state.wrote stays true unless a call rules a write out, so
// cleanup follows any possible write. A press before the page has started does nothing, so it repeats.
async function exercise(site, page, state) {
  const button = site.conversation ? REPLY_BUTTON : SITES[site.name].composeButton;
  const buttonFound = !button || (await poll(() => page.run(buttonScript(button)), 60000, 1000));
  if (button && buttonFound && (await settledEditorCount(page)) !== 0) {
    return { ok: false, text: "the page already has an open draft; use one without a saved draft" };
  }
  const prepare = prepareScript(Boolean(button));
  let pressed = !button;
  let prepared;
  const typed = buttonFound && await poll(
    async () => {
      if (pressed) {
        const wrote = state.wrote;
        state.wrote = true;
        prepared = await page.run(prepare);
        if (typeof prepared === "number" && prepared < 0) {
          state.wrote = wrote;
        }
        state.opened ||= button && prepared <= -2;
        if (prepared > 0 || prepared === -3) {
          return prepared;
        }
        if (prepared !== -1 || !button) {
          return null;
        }
      }
      const outcome = await page.run(pressScript(button));
      if (outcome === "editor" && !pressed) {
        return -5;
      }
      pressed ||= outcome === "pressed";
      return null;
    },
    60000,
    2000,
  );
  if (typed === -3) {
    return { ok: false, text: "more than one draft editor opened" };
  }
  if (typed === -5) {
    return { ok: false, text: "the page already has an open draft; use one without a saved draft" };
  }
  if (!typed && prepared === -4) {
    return { ok: false, text: "the editor that opened did not take the focus, so it is not known to be this run's" };
  }
  if (!typed) {
    const origin = await page.run("return location.origin;");
    if (typeof origin !== "string") {
      return { ok: false, text: "the page stopped answering" };
    }
    if (!SITES[site.name].origins.some((pattern) => pattern.startsWith(`${origin}/`))) {
      return { ok: false, text: `the page went to ${origin}; is the profile signed in?` };
    }
    const text = !buttonFound ? `no ${site.conversation ? "Reply" : "Compose"} button on the page` : "no draft editor appeared, or the marker could not be selected";
    return { ok: false, text };
  }
  await sleep(500);
  return verdict(site, JSON.parse(await page.capture(SITES[site.name].origins)));
}

// True only after this run's own discard press, once its tab closed (Outlook's compose page) or no editor shows
// and no confirmation is open, with its editor gone (Gmail) or, on Outlook, hidden after the confirmation. The
// page had no editor before the press, so any shown editor may be this draft.
async function cleanUp(site, page) {
  if ((await page.run(CLEAN_UP).catch(() => null)) !== "pressed") {
    return false;
  }
  let confirmed = false;
  return poll(
    async () => {
      if (!(await page.exists())) {
        return true;
      }
      confirmed ||= Boolean(await page.run(CONFIRM_DISCARD).catch(() => false));
      const draft = await page.run(DRAFT_STATE).catch(() => null);
      return Boolean(
        draft && draft.editorsShown === 0 && !draft.confirmOpen && (!draft.present || (site.name === "outlook" && confirmed)),
      );
    },
    8000,
    1000,
  );
}

// Counts animation frames for a second. A covered or minimized window gets none, and the sites' drafts and dialogs
// then never finish opening or closing.
const FRAMES = `return await new Promise((done) => {
  let frames = 0;
  const tick = () => { frames++; requestAnimationFrame(tick); };
  requestAnimationFrame(tick);
  setTimeout(() => done(frames), 1000);
});`;

async function checkSite(site, openPage) {
  const page = await openPage(site.conversation ?? SITES[site.name].newDraft);
  const state = { wrote: false, opened: false };
  let result;
  let error;
  try {
    // Chrome's new tab may still be loading, which loses the first count.
    result = (await poll(() => page.run(FRAMES), 10000, 500))
      ? await exercise(site, page, state)
      : { ok: false, text: "the browser window is not drawing; keep it visible on screen" };
  } catch (thrown) {
    error = thrown;
  }
  const cleaned = !state.wrote || (await cleanUp(site, page).catch(() => false));
  await page.close().catch(() => log("could not close the tab"));
  const leftover = `the draft could not be confirmed discarded, check the Drafts folder for "${marker}"`;
  if (error && !(error instanceof Failure)) {
    if (!cleaned) {
      log(leftover);
    }
    throw error;
  }
  result ??= { ok: false, text: error.message };
  if (!cleaned) {
    return { ok: false, text: `${result.text}; ${leftover}` };
  }
  // An editor that opened but could not be tied to the press is left alone and may stay as a draft.
  return state.opened && !state.wrote ? { ...result, text: `${result.text}; a draft may have been opened, check the Drafts folder` } : result;
}

// Outstanding browser commands by id. Each has a deadline and all fail when the connection closes, so a stalled
// or dropped browser still reaches cleanup and the leftover-draft warning. Failures carry only the command name
// and an error code: a browser's message can quote a page address, which on Gmail carries search terms.
class Requests {
  pending = new Map();
  nextId = 1;
  closed = false;
  start(name) {
    const id = this.nextId++;
    const promise = new Promise((done, reject) => {
      const timer = setTimeout(() => this.settle(id, "timed out"), 60000);
      this.pending.set(id, { name, done, reject, timer });
    });
    if (this.closed) {
      this.settle(id, "connection closed");
    }
    return { id, promise };
  }
  settle(id, code, result) {
    const entry = this.pending.get(id);
    if (!entry) {
      return;
    }
    this.pending.delete(id);
    clearTimeout(entry.timer);
    if (code) {
      entry.reject(new Failure(`${entry.name} failed (${code})`));
    } else {
      entry.done(result);
    }
  }
  close() {
    this.closed = true;
    for (const id of [...this.pending.keys()]) {
      this.settle(id, "connection closed");
    }
  }
}

// Marionette frames are "<byte length>:<json>"; commands are [0, id, name, params], replies [1, id, error, result].
class Marionette {
  static async open(port) {
    const socket = await new Promise((done, reject) => {
      const connection = connect(port, "127.0.0.1", () => done(connection));
      connection.once("error", () => reject(new Failure(`nothing answers Marionette on port ${port}`)));
    });
    const client = new Marionette(socket);
    const timer = setTimeout(() => client.greetingFailed(new Failure("Marionette sent no greeting")), 10000);
    try {
      await client.greeting;
    } finally {
      clearTimeout(timer);
    }
    return client;
  }
  constructor(socket) {
    this.buffer = Buffer.alloc(0);
    this.requests = new Requests();
    this.greeting = new Promise((done, reject) => {
      this.greeted = done;
      this.greetingFailed = reject;
    });
    this.socket = socket;
    const closed = () => {
      this.requests.close();
      this.greetingFailed(new Failure("the Marionette connection closed"));
    };
    socket.on("close", closed);
    socket.on("error", closed);
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
        if (Array.isArray(frame)) {
          const [, id, error, result] = frame;
          this.requests.settle(id, error && (error.error ?? "unknown error"), result);
        } else {
          this.greeted();
        }
      }
    });
  }
  send(name, params = {}) {
    const { id, promise } = this.requests.start(name);
    if (!this.requests.closed) {
      const body = Buffer.from(JSON.stringify([0, id, name, params]));
      this.socket.write(`${body.length}:`);
      this.socket.write(body);
    }
    return promise;
  }
  async script(body) {
    const result = await this.send("WebDriver:ExecuteScript", { script: `return (async () => { ${body} })();`, args: [] });
    return result.value;
  }
}

async function firefox(options) {
  const client = await Marionette.open(options.port);
  const session = await client.send("WebDriver:NewSession", { capabilities: {} });
  log(`${session.capabilities?.browserName ?? "Firefox"} ${session.capabilities?.browserVersion ?? ""} is up`);
  const installed = await client.send("Addon:Install", { path: options.extension, temporary: true });
  if ((installed.value ?? installed) !== FIREFOX_ID) {
    fail(`the add-on installed as ${installed.value ?? installed}, not ${FIREFOX_ID}`);
  }

  // A temporary add-on is not granted its host permissions, and a leaving Gmail tab can hold the browser
  // on a beforeunload prompt.
  await client.send("Marionette:SetContext", { value: "chrome" });
  const uuid = await client.script(`
    const { ExtensionPermissions } = ChromeUtils.importESModule("resource://gre/modules/ExtensionPermissions.sys.mjs");
    const policy = WebExtensionPolicy.getByID(${JSON.stringify(FIREFOX_ID)});
    await ExtensionPermissions.add(${JSON.stringify(FIREFOX_ID)}, { permissions: [], origins: ${JSON.stringify(ALL_ORIGINS)} }, policy.extension);
    Services.prefs.setBoolPref("dom.disable_beforeunload", true);
    return policy.mozExtensionHostname;`);
  await client.send("Marionette:SetContext", { value: "content" });

  // The extension page opens first: a tab opening later would collapse Gmail's selection.
  const extensionTab = (await client.send("WebDriver:NewWindow", { type: "tab", focus: false })).handle;
  await client.send("WebDriver:SwitchToWindow", { handle: extensionTab, focus: false });
  await client.send("WebDriver:Navigate", { url: `moz-extension://${uuid}/popup.html` });

  const openPage = async (url) => {
    const handle = (await client.send("WebDriver:NewWindow", { type: "tab", focus: true })).handle;
    await client.send("WebDriver:SwitchToWindow", { handle, focus: true });
    await client.send("WebDriver:Navigate", { url });
    const inTab = async (tab, work) => {
      await client.send("WebDriver:SwitchToWindow", { handle: tab, focus: false });
      try {
        return await work();
      } finally {
        await client.send("WebDriver:SwitchToWindow", { handle, focus: false });
      }
    };
    return {
      run: (body) => client.script(body),
      exists: async () => (await client.send("WebDriver:GetWindowHandles")).includes(handle),
      capture: (origins) => inTab(extensionTab, () => client.script(captureScript("browser", origins))),
      // Outlook's compose page closes its own tab when the draft is discarded.
      close: async () => {
        await client.send("WebDriver:CloseWindow").catch(() => {});
        await client.send("WebDriver:SwitchToWindow", { handle: extensionTab, focus: false });
      },
    };
  };
  try {
    return await runSites(options, openPage);
  } finally {
    await client.send("WebDriver:SwitchToWindow", { handle: extensionTab, focus: false }).catch(() => {});
    await client.send("WebDriver:CloseWindow").catch(() => {});
    await client.send("Addon:Uninstall", { id: FIREFOX_ID }).catch(() => {});
    await client.send("WebDriver:DeleteSession").catch(() => {});
    client.socket.end();
  }
}

async function chrome(options) {
  let version;
  try {
    version = await (await fetch(`http://127.0.0.1:${options.port}/json/version`)).json();
  } catch {
    fail(`nothing answers DevTools on port ${options.port}`);
  }
  log(`${version.Browser} is up`);
  const socket = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((done, reject) => {
    socket.onopen = done;
    socket.onerror = () => reject(new Failure("could not open the DevTools socket (start Chrome with --remote-allow-origins=*)"));
  });
  const requests = new Requests();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.id) {
      requests.settle(message.id, message.error && String(message.error.code), message.result);
    }
  };
  socket.onclose = () => requests.close();
  const send = (method, params = {}, sessionId) => {
    const { id, promise } = requests.start(method);
    if (!requests.closed) {
      socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    }
    return promise;
  };
  const evaluate = async (sessionId, body) => {
    const { result, exceptionDetails } = await send(
      "Runtime.evaluate",
      { expression: `(async () => { ${body} })()`, awaitPromise: true, returnByValue: true },
      sessionId,
    );
    if (exceptionDetails) {
      fail(`page script failed (${exceptionDetails.exception?.className ?? "error"})`);
    }
    return result.value;
  };
  const openTab = async (url, background) => {
    const { targetId } = await send("Target.createTarget", { url, background });
    const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
    return { targetId, sessionId };
  };

  const { id: extensionId } = await send("Extensions.loadUnpacked", { path: options.extension });
  // The extension page opens first: a tab opening later would collapse Gmail's selection.
  const extensionTab = await openTab(`chrome-extension://${extensionId}/popup.html`, true);
  const openPage = async (url) => {
    const tab = await openTab(url, false);
    await send("Page.bringToFront", {}, tab.sessionId);
    return {
      // A page script error reads as null, like a page still loading; a lost browser still throws.
      run: (body) =>
        evaluate(tab.sessionId, body).catch((error) => {
          if (requests.closed) {
            throw error;
          }
          return null;
        }),
      exists: async () => (await send("Target.getTargets")).targetInfos.some((target) => target.targetId === tab.targetId),
      capture: (origins) => evaluate(extensionTab.sessionId, captureScript("chrome", origins)),
      close: () => send("Target.closeTarget", { targetId: tab.targetId }),
    };
  };
  try {
    return await runSites(options, openPage);
  } finally {
    await send("Target.closeTarget", { targetId: extensionTab.targetId }).catch(() => {});
    await send("Extensions.uninstall", { id: extensionId }).catch(() => {});
    socket.close();
  }
}

async function runSites(options, openPage) {
  let passed = true;
  for (const site of options.sites) {
    const result = await checkSite(site, openPage).catch((error) => {
      if (!(error instanceof Failure)) {
        throw error;
      }
      return { ok: false, text: error.message };
    });
    console.log(`${site.name} ${site.conversation ? "reply" : "new draft"}: ${result.ok ? "pass" : "FAIL"}, ${result.text}`);
    passed &&= result.ok;
  }
  return passed;
}

const options = parseArgs(process.argv.slice(2));
try {
  const passed = await (options.browserName === "firefox" ? firefox(options) : chrome(options));
  process.exit(passed ? 0 : 1);
} catch (error) {
  console.log(`FAIL: ${error instanceof Failure ? error.message : error.stack}`);
  process.exit(1);
}
