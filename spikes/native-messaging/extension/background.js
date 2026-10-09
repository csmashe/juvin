const api = typeof browser !== "undefined" ? browser : chrome;
const HOST = "com.juvin.spike";
const REPORT_URL = "http://127.0.0.1:18765/report";

function report(result) {
  api.storage.local.set({ result });
  fetch(REPORT_URL, { method: "POST", body: JSON.stringify(result) }).catch((e) => console.error("report failed", e));
}

function run(trigger) {
  const sentAt = Date.now();
  const result = { trigger, extensionId: api.runtime.id, userAgent: navigator.userAgent, startedAt: new Date(sentAt).toISOString() };
  let port;
  try {
    port = api.runtime.connectNative(HOST);
  } catch (e) {
    result.error = "connectNative threw: " + e.message;
    report(result);
    console.error(result);
    return;
  }
  port.onMessage.addListener((msg) => {
    if (msg.type === "pong") {
      result.roundTripMs = Date.now() - sentAt;
      result.pong = msg;
      port.postMessage({ type: "ack", roundTripMs: result.roundTripMs });
    } else if (msg.type === "bye") {
      result.ok = true;
      report(result);
      console.log("round trip ok", result);
      port.disconnect();
    }
  });
  port.onDisconnect.addListener(() => {
    const err = port.error || api.runtime.lastError;
    if (err && !result.ok) {
      result.error = "disconnected: " + err.message;
      report(result);
      console.error(result);
    }
  });
  port.postMessage({ type: "hello", extensionId: api.runtime.id, userAgent: navigator.userAgent, sentAt });
}

api.runtime.onInstalled.addListener(() => run("onInstalled"));
api.runtime.onStartup.addListener(() => run("onStartup"));
api.action.onClicked.addListener(() => run("actionClicked"));
