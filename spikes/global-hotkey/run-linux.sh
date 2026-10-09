#!/usr/bin/env bash
# Runs the three Linux backends. Needs ydotoold running and reachable by ydotool.
# ydotool 0.1.x (Ubuntu) uses key names: export JUVIN_YDOTOOL_KEYS=ctrl+shift+y before running.
set -u
B="$(dirname "$0")/app/bin/Debug/net10.0/juvin-hotkey-spike"
[ -x "$B" ] || B="$(dirname "$0")/app/publish/linux-x64/juvin-hotkey-spike"
"$B" selftest --backend evdev --timeout 6
"$B" selftest --backend sharphook --timeout 6
"$B" selftest --backend socket --gnome --timeout 6
