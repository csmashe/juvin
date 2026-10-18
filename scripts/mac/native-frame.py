#!/usr/bin/env python3
"""Speaks native messaging framing to juvin-host the way a browser does.

Writes one frame (a 4-byte little-endian length, then UTF-8 JSON) to the host's stdin, reads one
frame back from its stdout, prints that JSON on one line, then closes stdin and waits for the host
to exit. Exits 0 when a frame came back, 1 otherwise.

Usage: native-frame.py <juvin-host path> '<json message>' [timeout seconds]
"""

import json
import struct
import subprocess
import sys
import threading


def main():
    if len(sys.argv) not in (3, 4):
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2
    host, message = sys.argv[1], json.loads(sys.argv[2])
    timeout = float(sys.argv[3]) if len(sys.argv) == 4 else 10.0
    payload = json.dumps(message).encode()
    process = subprocess.Popen([host], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    process.stdin.write(struct.pack("<I", len(payload)) + payload)
    process.stdin.flush()

    reply = {}

    def read_frame():
        header = process.stdout.read(4)
        if len(header) == 4:
            (length,) = struct.unpack("<I", header)
            reply["frame"] = process.stdout.read(length)

    reader = threading.Thread(target=read_frame, daemon=True)
    reader.start()
    reader.join(timeout)
    process.stdin.close()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    stderr = process.stderr.read().decode(errors="replace").strip()
    print("host exit code: %d" % process.returncode, file=sys.stderr)
    if stderr:
        print("host stderr: %s" % stderr, file=sys.stderr)
    if "frame" not in reply:
        print("no frame came back within %.0f seconds" % timeout, file=sys.stderr)
        return 1
    print(json.dumps(json.loads(reply["frame"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
