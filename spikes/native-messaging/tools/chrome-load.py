"""Launch Chrome with a temp profile and load the unpacked spike extension via CDP over the debugging pipe."""
import json, os, subprocess, sys, time

ext = sys.argv[1]
profile = sys.argv[2]
hold = int(sys.argv[3]) if len(sys.argv) > 3 else 15
r_in, w_in = os.pipe()    # we write, chrome reads on fd 3
r_out, w_out = os.pipe()  # chrome writes on fd 4, we read
extra = os.environ.get("CHROME_EXTRA_ARGS", "")
cmd = (f"exec 3<&{r_in} 4>&{w_out}; exec google-chrome --user-data-dir={profile} --no-first-run "
       f"--no-default-browser-check --remote-debugging-pipe --enable-unsafe-extension-debugging {extra} about:blank")
proc = subprocess.Popen(["bash", "-c", cmd], pass_fds=(r_in, w_out), stdout=subprocess.DEVNULL,
                        stderr=open("/tmp/juvin-nm-spike/chrome-stderr.txt", "wb"))
os.close(r_in); os.close(w_out)

def send(msg):
    os.write(w_in, json.dumps(msg).encode() + b"\0")

pending = b""
def recv():
    global pending
    while b"\0" not in pending:
        chunk = os.read(r_out, 65536)
        if not chunk:
            return None
        pending += chunk
    msg, pending = pending.split(b"\0", 1)
    return json.loads(msg)

send({"id": 1, "method": "Extensions.loadUnpacked", "params": {"path": ext}})
while True:
    m = recv()
    if m is None:
        print("pipe closed"); break
    if m.get("id") == 1:
        print("loadUnpacked ->", m); break
time.sleep(5)
ext_id = m["result"]["id"]
send({"id": 2, "method": "Target.getTargets"})
targets = []
while True:
    m = recv()
    if m and m.get("id") == 2:
        targets = m["result"]["targetInfos"]; break
sw = [t for t in targets if t["type"] == "service_worker" and ext_id in t["url"]]
print("service worker targets:", [(t["url"], t["targetId"]) for t in sw])
if sw:
    send({"id": 3, "method": "Target.attachToTarget", "params": {"targetId": sw[0]["targetId"], "flatten": True}})
    session = None
    while True:
        m = recv()
        if m and m.get("id") == 3:
            session = m["result"]["sessionId"]; break
    send({"id": 4, "sessionId": session, "method": "Runtime.evaluate",
          "params": {"expression": "chrome.storage.local.get('result').then(r => JSON.stringify(r))", "awaitPromise": True, "returnByValue": True}})
    while True:
        m = recv()
        if m and m.get("id") == 4:
            print("stored result:", m["result"].get("result", {}).get("value")); break
time.sleep(hold)
proc.terminate(); proc.wait()
