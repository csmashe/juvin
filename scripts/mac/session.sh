#!/usr/bin/env bash
# Drives a rented NoMac session for docs/testing/macos-checklist.md from the Linux machine, in the
# repository root. Every subcommand can be run again safely. Only stop-block1 ever stops a Mac; block 2
# ends with a hand-off and the user stops it.
#
# Usage: scripts/mac/session.sh <command>
#   start 1|2        block 1: macOS's own prompts, Gatekeeper on, no drive, 45 minutes at most.
#                    block 2: auto-answered prompts, 4 hours, $5 cap, idle stop after an hour.
#                    Waits for the Mac, names it "nomac" in the SSH config, and syncs the repository.
#   tunnel [stop|status]
#                    keeps an SSH reverse tunnel up so the Mac reaches the LAN Ollama as 127.0.0.1:11434
#   prepare          runs scripts/mac/prepare.sh on the Mac
#   auto [args]      runs scripts/mac/run-automated.sh on the Mac (args go to it, for example
#                    --only M-10,M-23) and pulls artifacts/mac-results back here
#   pull             pulls artifacts/mac-results back here
#   status           nomac status and the kit's own state
#   handoff          pulls the results, extends the Mac when under an hour is left, and prints the
#                    desktop link and the session password
#   stop-block1      stops the Mac, only when this kit started it as block 1
# Environment:
#   NOMAC              the nomac command (default nomac on PATH, else npx -y @nomac/cli@0.13.2)
#   JUVIN_KIT_OLLAMA   the LAN model server the tunnel reaches (default 10.0.3.20:11434)
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
STATE=${XDG_STATE_HOME:-$HOME/.local/state}/juvin-mac-kit
OLLAMA=${JUVIN_KIT_OLLAMA:-10.0.3.20:11434}
RESULTS=artifacts/mac-results
BLOCK1_LIMITS=(--prompts ask --gatekeeper on --drive none --max-duration 2700 --max-cost 1)
BLOCK2_LIMITS=(--max-duration 14400 --idle-timeout 3600 --max-cost 5)
SSH_ALIAS=nomac

die() {
    echo "error: $*" >&2
    exit 1
}

usage() {
    sed -n '/^# Usage:/,/^# Environment:/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//' >&2
    exit 2
}

if [[ -n ${NOMAC:-} ]]; then
    read -r -a NOMAC_CMD <<<"$NOMAC"
elif command -v nomac >/dev/null 2>&1; then
    NOMAC_CMD=("$(command -v nomac)")
else
    NOMAC_CMD=(npx -y @nomac/cli@0.13.2)
fi
nomac() { (cd "$REPO_ROOT" && "${NOMAC_CMD[@]}" "$@"); }

mkdir -p "$STATE"
cd "$REPO_ROOT"

# Prints one field of `nomac status --json` (a dotted path such as session.state), or nothing.
status_field() {
    nomac status --json 2>/dev/null | python3 -c '
import json, sys
value = json.load(sys.stdin)
for key in sys.argv[1].split("."):
    value = value.get(key) if isinstance(value, dict) else None
print("" if value is None else value)' "$1"
}

require_ready() {
    [[ $(status_field session.state) == ready ]] || die "no ready Mac for this folder; run: scripts/mac/session.sh start 2"
}

remote_dir() {
    local dir
    dir=$(status_field project.path)
    echo "${dir:-~/drive/src/$(basename "$REPO_ROOT")}"
}

cmd_start() {
    local block=${1:-} limits state id recorded
    case $block in
        1) limits=("${BLOCK1_LIMITS[@]}") ;;
        2) limits=("${BLOCK2_LIMITS[@]}") ;;
        *) usage ;;
    esac
    state=$(status_field session.state)
    recorded=$(cat "$STATE/block" 2>/dev/null || true)
    if [[ $state == ready || $state == queued || $state == provisioning ]]; then
        [[ $recorded == "$block" ]] || die "a Mac is already running here as block ${recorded:-unknown}; finish it first (block 1 ends with stop-block1)"
        echo "Block $block's Mac is already $state; reusing it."
        limits=()
    fi
    nomac start --wait 600 "${limits[@]}"
    state=$(status_field session.state)
    [[ $state == ready ]] || die "the Mac is $state, not ready; run scripts/mac/session.sh status"
    id=$(status_field session.id)
    echo "$block" >"$STATE/block"
    echo "$id" >"$STATE/session"
    nomac ssh-config --alias "$SSH_ALIAS"
    # Sync exits 1 when a file changed on the Mac since the last sync; it still sends the rest.
    nomac sync . || echo "warning: nomac sync reported files changed on the Mac (named above); nomac pull them or sync with --overwrite"
    echo "Block $block is ready: session $id, project $(remote_dir) on the Mac."
}

tunnel_pid() {
    local pid
    pid=$(cat "$STATE/tunnel.pid" 2>/dev/null || true)
    if [[ -n $pid ]] && kill -0 "$pid" 2>/dev/null; then
        echo "$pid"
    fi
}

cmd_tunnel() {
    local pid
    case ${1:-start} in
        stop)
            pid=$(tunnel_pid)
            if [[ -n $pid ]]; then
                kill -TERM "$pid"
                echo "Stopped the tunnel ($pid)."
            else
                echo "No tunnel is running."
            fi
            rm -f "$STATE/tunnel.pid"
            return 0
            ;;
        status)
            pid=$(tunnel_pid)
            if [[ -n $pid ]]; then
                echo "Tunnel $pid is up: the Mac's 127.0.0.1:11434 goes to $OLLAMA. Log: $STATE/tunnel.log"
            else
                echo "No tunnel is running."
            fi
            return 0
            ;;
        start) ;;
        *) usage ;;
    esac
    pid=$(tunnel_pid)
    if [[ -n $pid ]]; then
        echo "Tunnel $pid is already up."
    else
        require_ready
        curl -fsS -m 5 "http://$OLLAMA/v1/models" >/dev/null || die "$OLLAMA does not answer from here"
        # A loop around ssh brings the tunnel back after a dropped connection; TERM ends both.
        # shellcheck disable=SC2016 # expanded by the inner bash
        nohup bash -c '
            trap "kill \$child 2>/dev/null; exit 0" TERM
            while true; do
                ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
                    -R "11434:$1" "$2" &
                child=$!
                wait "$child"
                echo "$(date -u +%H:%M:%S) ssh exited with $?; reconnecting in 5 s"
                sleep 5
            done' tunnel "$OLLAMA" "$SSH_ALIAS" >>"$STATE/tunnel.log" 2>&1 &
        echo $! >"$STATE/tunnel.pid"
        echo "Started the tunnel ($!). Log: $STATE/tunnel.log"
    fi
    local _
    for _ in 1 2 3 4 5 6; do
        sleep 5
        if nomac exec --wait 30 -- curl -fsS -m 5 -o /dev/null -w '%{http_code}\n' http://127.0.0.1:11434/v1/models; then
            echo "The Mac reaches the model server at 127.0.0.1:11434."
            return 0
        fi
    done
    die "the Mac does not reach 127.0.0.1:11434; see $STATE/tunnel.log"
}

on_mac() {
    require_ready
    nomac ssh -- "$1"
}

cmd_prepare() {
    on_mac "/bin/bash scripts/mac/prepare.sh"
}

cmd_pull() {
    require_ready
    mkdir -p "$RESULTS"
    nomac pull "$RESULTS" || echo "warning: nothing to pull from $RESULTS yet"
    echo "Results here: $REPO_ROOT/$RESULTS"
}

cmd_auto() {
    local args="" arg status=0
    for arg in "$@"; do
        args+=" $(printf '%q' "$arg")"
    done
    on_mac "/bin/bash scripts/mac/run-automated.sh$args" || status=$?
    cmd_pull
    return "$status"
}

cmd_status() {
    nomac status || true
    echo "Kit: block $(cat "$STATE/block" 2>/dev/null || echo none), session $(cat "$STATE/session" 2>/dev/null || echo none)"
    cmd_tunnel status
}

cmd_handoff() {
    local deadline minutes
    cmd_pull
    nomac status || true
    deadline=$(status_field session.activity.hard_deadline_at || true)
    # @nomac/cli 0.13.2 gives epoch milliseconds; an ISO time is accepted too.
    minutes=$(python3 -c '
import datetime, sys
raw = sys.argv[1].strip()
try:
    end = datetime.datetime.fromtimestamp(float(raw) / 1000, datetime.timezone.utc)
except ValueError:
    end = datetime.datetime.fromisoformat(raw.replace("Z", "+00:00"))
print(int((end - datetime.datetime.now(datetime.timezone.utc)).total_seconds() // 60))' "$deadline" 2>/dev/null || true)
    if [[ $minutes =~ ^-?[0-9]+$ ]]; then
        echo "About $minutes minutes left on the Mac."
        if ((minutes < 60)); then
            nomac extend 60 || echo "warning: nomac extend 60 failed; extend the Mac yourself"
        fi
    else
        echo "warning: could not read the stop time (${deadline:-none}); see the Stops by itself line above"
    fi
    echo
    nomac desktop --no-open || echo "warning: nomac desktop failed"
    echo
    echo "Session password:"
    nomac password
    echo
    echo "The Mac is yours now. Juvin stays installed and running. Stop the Mac yourself with nomac stop"
    echo "(run in $REPO_ROOT) when you are done; until then it runs to its time or spend limit."
    if [[ -n $(tunnel_pid) ]]; then
        echo "The tunnel is still up and keeps the Mac from its idle stop: scripts/mac/session.sh tunnel stop ends it."
    fi
}

cmd_stop_block1() {
    local recorded id
    recorded=$(cat "$STATE/block" 2>/dev/null || true)
    [[ $recorded == 1 ]] || die "the running Mac was not started as block 1 by this kit (recorded: ${recorded:-none}); block 2 is handed off, never stopped here"
    id=$(status_field session.id)
    [[ -n $id && $id == "$(cat "$STATE/session")" ]] || die "the attached Mac ($id) is not the block 1 session $(cat "$STATE/session")"
    [[ $(status_field session.access.prompts) == ask ]] || die "the attached Mac does not ask for prompts, so it is not block 1"
    cmd_tunnel stop
    nomac stop --wait 120
    rm -f "$STATE/block" "$STATE/session"
    echo "Block 1's Mac is stopped. Block 2 can start: scripts/mac/session.sh start 2"
}

[[ $# -ge 1 ]] || usage
command=$1
shift
case $command in
    start) cmd_start "$@" ;;
    tunnel) cmd_tunnel "$@" ;;
    prepare) cmd_prepare ;;
    auto) cmd_auto "$@" ;;
    pull) cmd_pull ;;
    status) cmd_status ;;
    handoff) cmd_handoff ;;
    stop-block1) cmd_stop_block1 ;;
    *) usage ;;
esac
