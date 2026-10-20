#!/usr/bin/env bash
# Runs the automated rows of docs/testing/macos-checklist.md on this Mac, side by side where they do
# not share state, into a results folder with one log and one status per row and a summary.md.
# Runs on the Mac with its own /bin/bash 3.2, after scripts/mac/prepare.sh.
#
# Usage: scripts/mac/run-automated.sh [--ci] [--only M-07,M-09] [--results DIR] [--list]
#   --ci        the GitHub runner: a missing model server is a failure, Launch at login is turned on
#               in the settings so M-23 has a plist to check, and M-26 sends to a dead local DSN
#   --only      run only these rows (ids from --list); rows needing the app start it when it is not running
#   --results   write into this folder (default artifacts/mac-results/<UTC timestamp>)
#   --list      print the rows and exit
# Exit status: 0 when no row failed, 1 otherwise. A skipped row says why in summary.md.
set -uo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
# shellcheck source=scripts/mac/kit-env.sh
. "$REPO_ROOT/scripts/mac/kit-env.sh"
KIT=$REPO_ROOT/scripts/mac

# id|group|title. Rows in one group run one after another; groups run side by side. Groups starting
# with "a" run before the app starts, "b" ones with it running; a1 must end before the app starts.
ROWS="M-04|a-tests|dotnet test of the full solution (Release)
M-04-mac|a-tests|macOS-only test set (Keychain, Accessibility, LaunchAgent, registrar, sockets)
M-08|a-keychain|Keychain test result and no Juvin item in the login Keychain
M-30|a-rewrite|Local mode rewrite through juvin-cli
M-03|a1|dmg smoke test, Info.plist and codesign of the installed app
M-26|a1|Crash report from a scratch build with a temporary throw
M-05|b-data|Data folder and runtime files
M-10|b-manifests|Host manifests for Chrome and Firefox
M-23|b-login|LaunchAgent plist after Launch at login is on
M-11|b1|Host round trip over native messaging framing, no browser
M-06|b1|Single instance, --trigger, and --background
M-12-chrome|b1|Browser round trip in Chrome
M-12-firefox|b1|Browser round trip in Firefox"

usage() {
    echo "Usage: $0 [--ci] [--only M-04,M-11] [--results DIR] [--list]" >&2
    exit 2
}

CI_MODE=false
ONLY=
OUT=
while [[ $# -gt 0 ]]; do
    case $1 in
        --ci) CI_MODE=true ;;
        --only)
            [[ $# -ge 2 ]] || usage
            ONLY=,$2,
            shift
            ;;
        --results)
            [[ $# -ge 2 ]] || usage
            OUT=$2
            shift
            ;;
        --list)
            echo "$ROWS" | awk -F'|' '{ printf "%-13s %s\n", $1, $3 }'
            exit 0
            ;;
        *) usage ;;
    esac
    shift
done

if [[ -n $ONLY ]]; then
    for id in $(echo "$ONLY" | tr ',' ' '); do
        echo "$ROWS" | grep -q "^$id|" || {
            echo "error: no row $id; see --list" >&2
            exit 2
        }
    done
fi
[[ $(uname -s) == Darwin ]] || {
    echo "error: this script runs on macOS" >&2
    exit 1
}

STAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUT=${OUT:-$REPO_ROOT/artifacts/mac-results/$STAMP}
mkdir -p "$OUT" "$KIT_STATE"
OUT=$(cd "$OUT" && pwd)
echo "Results: $OUT"

selected() { [[ -z $ONLY || $ONLY == *",$1,"* ]]; }
title_of() { echo "$ROWS" | awk -F'|' -v id="$1" '$1 == id { print $3 }'; }
rows_in() { echo "$ROWS" | awk -F'|' -v g="$1" '$2 == g { print $1 }'; }
clock() { date -u +%H:%M:%S; }

# Inside a row: note adds a line to the summary, skip and fail end the row with that status.
note() {
    echo "NOTE: $*"
    printf '%s\n' "$*" >>"$OUT/$ROW.notes"
}
skip() {
    note "$*"
    echo skip >"$OUT/$ROW.status"
    exit 0
}
fail() {
    note "$*"
    echo fail >"$OUT/$ROW.status"
    exit 1
}
# Copies the NOTE: lines a helper printed into the row's notes.
notes_from() {
    grep '^NOTE: ' "$1" | sed 's/^NOTE: //' >>"$OUT/$ROW.notes" || true
}

run_row() {
    local id=$1 started=$SECONDS status
    echo "[$(clock)] $id: started"
    ROW=$id
    ("row_${id//-/_}") >"$OUT/$id.log" 2>&1
    status=$?
    if [[ ! -f $OUT/$id.status ]]; then
        if [[ $status -eq 0 ]]; then
            echo pass >"$OUT/$id.status"
        else
            echo "exited with status $status" >>"$OUT/$id.notes"
            echo fail >"$OUT/$id.status"
        fi
    fi
    echo $((SECONDS - started)) >"$OUT/$id.seconds"
    touch "$OUT/$id.done"
    echo "[$(clock)] $id: $(cat "$OUT/$id.status") ($((SECONDS - started)) s)"
}

run_group() {
    local id
    for id in $(rows_in "$1"); do
        if selected "$id"; then
            run_row "$id"
        fi
    done
}

# Waits for another row of this run; prints its status, or nothing when it is not part of the run.
wait_for_row() {
    local id=$1 limit=${2:-3600} waited=0
    selected "$id" || return 0
    while [[ ! -f $OUT/$id.done ]] && ((waited < limit)); do
        sleep 5
        waited=$((waited + 5))
    done
    cat "$OUT/$id.status" 2>/dev/null || echo timeout
}

juvin_pid() { pgrep -x juvin | head -n1; }

# Merges JSON into settings.json, seeding a missing one with the macOS default shortcut. Juvin must not be running.
merge_settings() {
    mkdir -p "$DATA_DIR"
    python3 - "$DATA_DIR/settings.json" "$1" <<'EOF'
import json, os, sys
path, update = sys.argv[1], json.loads(sys.argv[2])
current = json.load(open(path)) if os.path.exists(path) else {"schemaVersion": 1, "rewriteShortcut": "Super+Shift+F"}
current.update(update)
with open(path + ".kit-tmp", "w") as f:
    json.dump(current, f, indent=2)
os.replace(path + ".kit-tmp", path)
EOF
}

# Starts the installed app through Launch Services with its output in a log the browser rows read.
# A Juvin the kit did not start has no known log; APP_LOG stays empty then.
ensure_app() {
    local pid
    APP_LOG=
    pid=$(juvin_pid)
    if [[ -n $pid ]]; then
        if [[ -f $KIT_STATE/app.pid && $(cat "$KIT_STATE/app.pid") == "$pid" && -f $(cat "$KIT_STATE/app.log" 2>/dev/null) ]]; then
            APP_LOG=$(cat "$KIT_STATE/app.log")
            echo "[$(clock)] Juvin $pid is running, log $APP_LOG"
        else
            echo "[$(clock)] Juvin $pid is running but was not started by the kit; its log is not known"
        fi
        return 0
    fi
    [[ -x $APP_EXE ]] || {
        echo "error: $APP is not installed; run scripts/mac/prepare.sh" >&2
        return 1
    }
    if $CI_MODE && selected M-23; then
        merge_settings '{"launchAtLogin": true}'
    fi
    APP_LOG=$OUT/juvin-app.log
    : >"$APP_LOG"
    open -g --stdout "$APP_LOG" --stderr "$APP_LOG" "$APP"
    for _ in $(seq 60); do
        if [[ -S $DATA_DIR/Runtime/control.sock ]] && grep -q '\[App\] Control socket ready' "$APP_LOG"; then
            pid=$(juvin_pid)
            echo "$pid" >"$KIT_STATE/app.pid"
            echo "$APP_LOG" >"$KIT_STATE/app.log"
            echo "[$(clock)] started Juvin $pid, log $APP_LOG"
            sleep 2
            return 0
        fi
        sleep 1
    done
    echo "error: Juvin did not open its control socket within 60 seconds" >&2
    tail -n 40 "$APP_LOG" >&2
    return 1
}

DMG=$REPO_ROOT/dist/Juvin-$KIT_VERSION-arm64.dmg

# Over SSH the login Keychain is locked, and the Keychain tests fail with -25308 (errSecInteractionNotAllowed).
# NoMac sets NOMAC_MAC_PASSWORD on the Mac; the GitHub runner's login Keychain is already unlocked.
unlock_login_keychain() {
    if [[ -z ${NOMAC_MAC_PASSWORD:-} ]]; then
        $CI_MODE || note "NOMAC_MAC_PASSWORD is not set, so the login Keychain was not unlocked; over SSH the Keychain tests fail with -25308"
        return 0
    fi
    if security unlock-keychain -p "$NOMAC_MAC_PASSWORD" "$HOME/Library/Keychains/login.keychain-db"; then
        note "unlocked the login Keychain with NOMAC_MAC_PASSWORD"
    else
        note "security unlock-keychain failed; the Keychain tests may fail with -25308"
    fi
}

row_M_04() {
    cd "$REPO_ROOT" || fail "no repository"
    unlock_login_keychain
    local test_status=0
    dotnet test Juvin.slnx -c Release --logger "trx;LogFilePrefix=full" --results-directory "$OUT/M-04-trx" || test_status=$?
    note "dotnet test exited with status $test_status"
    python3 "$KIT/trx-summary.py" "$OUT/M-04-trx" --allow-skip "Windows only." --allow-skip "Linux only." \
        --require KeychainTests.SetGetOverwriteAndDelete_RoundTrip --require MacAccessibilityPermissionSetupTaskTests \
        | tee "$OUT/M-04.trx.txt"
    local status=${PIPESTATUS[0]}
    notes_from "$OUT/M-04.trx.txt"
    [[ $status -eq 0 && $test_status -eq 0 ]] || fail "see M-04.log"
}

row_M_04_mac() {
    cd "$REPO_ROOT" || fail "no repository"
    unlock_login_keychain
    local filter="FullyQualifiedName~Mac|FullyQualifiedName~Keychain|FullyQualifiedName~LaunchAgent|FullyQualifiedName~UnixSocket|FullyQualifiedName~UnixConnectorSocket|FullyQualifiedName~SharpHook"
    local test_status=0
    dotnet test tests/Juvin.Platform.Tests -c Release --filter "$filter" --logger "trx;LogFilePrefix=mac" \
        --results-directory "$OUT/M-04-mac-trx" || test_status=$?
    note "dotnet test exited with status $test_status"
    # The filter also catches the Linux and Windows halves of shared classes, which skip here.
    python3 "$KIT/trx-summary.py" "$OUT/M-04-mac-trx" --allow-skip "Windows only." --allow-skip "Linux only." \
        --require KeychainTests --require MacAccessibilityPermissionSetupTaskTests \
        --require LaunchAgentLoginItemTests --require MacNativeHostRegistrarTests \
        | tee "$OUT/M-04-mac.trx.txt"
    local status=${PIPESTATUS[0]}
    notes_from "$OUT/M-04-mac.trx.txt"
    [[ $status -eq 0 && $test_status -eq 0 ]] || fail "see M-04-mac.log"
}

row_M_08() {
    local tests found=0
    tests=$(wait_for_row M-04)
    if [[ -n $tests ]]; then
        python3 "$KIT/trx-summary.py" "$OUT/M-04-trx" --require-only --require KeychainTests.SetGetOverwriteAndDelete_RoundTrip \
            | tee "$OUT/M-08.trx.txt"
        local status=${PIPESTATUS[0]}
        notes_from "$OUT/M-08.trx.txt"
        [[ $status -eq 0 ]] || fail "the Keychain round trip did not pass in M-04"
    else
        note "M-04 is not part of this run; the Keychain test result was not read"
    fi
    security find-generic-password -s Juvin >/dev/null 2>&1 || found=$?
    case $found in
        0) fail "the login Keychain holds an item under service Juvin" ;;
        44) note "security find-generic-password -s Juvin finds nothing" ;;
        *) fail "security find-generic-password exited with $found" ;;
    esac
}

# The installed bundle's checks need no running app; the smoke test refuses to run beside one.
row_M_03() {
    [[ -z $(juvin_pid) ]] || skip "Juvin is running; quit it and rerun --only M-03"
    [[ -s $DMG ]] || fail "$DMG is missing; run scripts/mac/prepare.sh"
    /bin/bash "$REPO_ROOT/scripts/smoke-test-mac-dmg.sh" "$KIT_VERSION" "$REPO_ROOT/dist" || fail "the smoke test failed"
    note "smoke test: All smoke tests passed."
    [[ -d $APP ]] || fail "$APP is not installed"
    codesign --verify --deep --strict "$APP" || fail "codesign --verify rejects $APP"
    codesign -dv "$APP" 2>&1 | tee "$OUT/M-03.codesign.txt"
    grep -q 'Signature=adhoc' "$OUT/M-03.codesign.txt" || fail "codesign -dv does not report Signature=adhoc"
    local id
    id=$(defaults read "$APP/Contents/Info" CFBundleIdentifier)
    [[ $id == com.juvin.app ]] || fail "CFBundleIdentifier is $id"
    note "installed app: Signature=adhoc, CFBundleIdentifier $id"
    [[ -x $APP_EXE && -x $HOST_EXE ]] || fail "Contents/MacOS lacks juvin or juvin-host"
    ls -l "$APP_EXE" "$HOST_EXE" "$CLI_EXE"
    spctl -a -vv "$APP" >"$OUT/M-03.spctl.txt" 2>&1 || true
    cat "$OUT/M-03.spctl.txt"
    note "spctl -a -vv: $(tr '\n' ' ' <"$OUT/M-03.spctl.txt")"
}

# Patches a scratch copy so the UI thread throws once the app is up, runs it with reporting on and
# then off, and leaves the Sentry side to check by hand. The real settings are put back afterwards.
row_M_26() {
    [[ -n ${JUVIN_SENTRY_DSN:-} ]] || skip "JUVIN_SENTRY_DSN is not set; set it (a NoMac secret reaches every command) and rerun --only M-26"
    [[ -z $(juvin_pid) ]] || skip "Juvin is running; quit it and rerun --only M-26"
    local scratch=$KIT_STATE/m26-scratch app_file anchor dll data_existed=false backup=$KIT_STATE/m26-settings.json
    mkdir -p "$scratch"
    rsync -a --delete --exclude bin/ --exclude obj/ --exclude node_modules/ --exclude dist/ --exclude artifacts/ \
        --exclude extension/ "$REPO_ROOT/" "$scratch/" || fail "could not copy the repository"
    app_file=$scratch/src/Juvin.App/App.axaml.cs
    anchor='_ = StartHotkeysAsync(hotkeys);'
    grep -qF "$anchor" "$app_file" || fail "App.axaml.cs no longer has '$anchor'; move the kit's throw to another spot"
    python3 - "$app_file" "$anchor" <<'EOF' || fail "could not patch the scratch copy"
import sys
path, anchor = sys.argv[1], sys.argv[2]
throw = ('\n        if (System.Environment.GetEnvironmentVariable("JUVIN_KIT_CRASH") == "1")\n        {\n'
         '            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("juvin-kit canary phrase "'
         ' + System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)));\n        }\n')
text = open(path).read()
open(path, "w").write(text.replace(anchor, anchor + throw, 1))
EOF
    dotnet build "$scratch/src/Juvin.App/Juvin.App.csproj" -c Debug --nologo -v q || fail "the scratch build failed"
    dll=$scratch/src/Juvin.App/bin/Debug/net10.0/juvin.dll
    [[ -f $dll ]] || fail "no $dll"

    if [[ -d $DATA_DIR ]]; then
        data_existed=true
    fi
    rm -f "$backup"
    if [[ -f $DATA_DIR/settings.json ]]; then
        cp "$DATA_DIR/settings.json" "$backup"
    fi
    local run reporting started pid
    for run in 1 2; do
        reporting=true
        if [[ $run -eq 2 ]]; then
            reporting=false
        fi
        merge_settings "{\"hasCompletedOnboarding\": true, \"crashReportingEnabled\": $reporting}"
        started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
        JUVIN_KIT_CRASH=1 dotnet "$dll" >"$OUT/M-26.run$run.log" 2>&1 &
        pid=$!
        for _ in $(seq 90); do
            kill -0 "$pid" 2>/dev/null || break
            sleep 1
        done
        if kill -0 "$pid" 2>/dev/null; then
            kill -KILL "$pid"
            wait "$pid" 2>/dev/null
            restore_m26_settings "$data_existed" "$backup"
            fail "run $run did not crash within 90 seconds"
        fi
        wait "$pid"
        local code=$?
        grep -q 'juvin-kit canary phrase' "$OUT/M-26.run$run.log" || {
            restore_m26_settings "$data_existed" "$backup"
            fail "run $run ended with status $code before the temporary throw ran"
        }
        if grep -q '\[CrashReporting\] .* failed' "$OUT/M-26.run$run.log"; then
            note "run $run traced a crash reporting failure: $(grep '\[CrashReporting\]' "$OUT/M-26.run$run.log" | head -n1)"
        fi
        [[ $code -ne 0 ]] || {
            restore_m26_settings "$data_existed" "$backup"
            fail "run $run exited 0 after the throw"
        }
        note "run $run (crash reporting $reporting) crashed at $started with status $code"
    done
    restore_m26_settings "$data_existed" "$backup"
    note "check Sentry project juvin by hand: one new issue from the first run, none from the second (see the checklist)"
}

restore_m26_settings() {
    if [[ -f $2 ]]; then
        cp "$2" "$DATA_DIR/settings.json"
    elif ! $1; then
        rm -rf "$DATA_DIR"
    else
        rm -f "$DATA_DIR/settings.json"
    fi
}

row_M_05() {
    [[ -n $(juvin_pid) ]] || fail "Juvin is not running"
    local dir perms file
    for dir in "$DATA_DIR" "$DATA_DIR/Secrets" "$DATA_DIR/Logs" "$DATA_DIR/Runtime"; do
        [[ -d $dir ]] || fail "$dir is missing"
        perms=$(stat -f '%Sp' "$dir")
        echo "$perms $dir"
        [[ $perms == drwx------ ]] || fail "$dir is $perms, not drwx------"
    done
    for file in control.sock connector.sock; do
        [[ -S $DATA_DIR/Runtime/$file ]] || fail "Runtime/$file is not a socket"
    done
    for file in control.lock connector.lock; do
        [[ -f $DATA_DIR/Runtime/$file ]] || fail "Runtime/$file is missing"
    done
    [[ -z $(find "$DATA_DIR" -name secret.key) ]] || fail "a secret.key exists under the data folder"
    note "data folder and its Secrets, Logs, Runtime are drwx------; both sockets and locks are there; no secret.key"
    note "by hand: the Privacy and data path and Open folder, and the sockets going after Exit"
}

row_M_10() {
    local family file kit=false out
    for family in chrome firefox; do
        file=$CHROME_HOSTS_DIR/com.juvin.host.json
        if [[ $family == firefox ]]; then
            file=$FIREFOX_HOSTS_DIR/com.juvin.host.json
        fi
        [[ -f $file ]] || fail "no $family manifest at $file; run prepare.sh or press Register"
        out=$(python3 - "$file" "$family" "$HOST_EXE" "$CHROME_ORIGIN" "$FIREFOX_EXTENSION_ID" "$APP_DESCRIPTION" <<'EOF'
import json, os, sys
path, family, host, origin, extension_id, app_description = sys.argv[1:]
manifest = json.load(open(path))
allow = ("allowed_extensions", [extension_id]) if family == "firefox" else ("allowed_origins", [origin])
problems = []
for key, wanted in (("name", "com.juvin.host"), ("type", "stdio"), ("path", host), allow):
    if manifest.get(key) != wanted:
        problems.append("%s is %r, expected %r" % (key, manifest.get(key), wanted))
if not os.access(manifest.get("path", ""), os.X_OK):
    problems.append("path is not executable")
expected_keys = {"name", "description", "path", "type", allow[0]}
if set(manifest) != expected_keys:
    problems.append("keys are %s" % sorted(manifest))
print("; ".join(problems) if problems else ("app" if manifest.get("description") == app_description else "kit"))
EOF
        ) || fail "$file is not valid JSON"
        echo "$family: $out"
        case $out in
            app) note "$family manifest written by the app: name, type, path, and allowlist are right, and the host is executable" ;;
            kit) kit=true ;;
            *) fail "$family manifest: $out" ;;
        esac
    done
    if $kit; then
        skip "the kit's manifests are still in place (their shape is right): press Register or Update for each browser in the wizard, then rerun --only M-10"
    fi
}

row_M_23() {
    local plist=$HOME/Library/LaunchAgents/com.juvin.app.plist out
    for _ in $(seq 20); do
        [[ -f $plist ]] && break
        sleep 1
    done
    [[ -f $plist ]] || skip "Launch at login is off: turn it on in General, then rerun --only M-23"
    plutil -lint "$plist" || fail "plutil -lint rejects the plist"
    plutil -p "$plist"
    out=$(python3 - "$plist" "$APP_EXE" <<'EOF'
import plistlib, sys
plist = plistlib.load(open(sys.argv[1], "rb"))
wanted = {"Label": "com.juvin.app", "ProgramArguments": [sys.argv[2], "--background"], "RunAtLoad": True,
          "LimitLoadToSessionType": "Aqua", "AbandonProcessGroup": True, "JuvinManaged": True}
print("; ".join("%s is %r, expected %r" % (k, plist.get(k), v) for k, v in wanted.items() if plist.get(k) != v))
EOF
    )
    [[ -z $out ]] || fail "$out"
    note "plist lints and has Label, ProgramArguments (executable, --background), RunAtLoad, Aqua, AbandonProcessGroup, JuvinManaged"
    if $CI_MODE; then
        note "the kit turned Launch at login on in settings.json before starting Juvin, which wrote the plist at startup"
    fi
}

row_M_11() {
    [[ -n $(juvin_pid) ]] || fail "Juvin is not running"
    local reply
    reply=$(python3 "$KIT/native-frame.py" "$HOST_EXE" \
        '{"v":1,"type":"hello","extension":{"id":"extension@juvin.app","version":"0.1.0"},"browser":{"name":"unknown","version":"0.0"}}') \
        || fail "juvin-host sent no frame back"
    python3 -c 'import json, sys; m = json.loads(sys.argv[1]); print("reply type %s, v %s, keys %s" % (m.get("type"), m.get("v"), sorted(m)))' "$reply"
    python3 -c 'import json, sys; m = json.loads(sys.argv[1]); sys.exit(0 if m.get("type") == "config" and m.get("v") == 1 else 1)' "$reply" \
        || fail "the reply is not a config message with v 1"
    note "a framed hello through juvin-host came back as config with v 1"
    note "the host exiting 1 without the app is part of M-03's smoke test"
}

row_M_06() {
    [[ -n $(juvin_pid) ]] || fail "Juvin is not running"
    local count status=0
    open -n "$APP" || fail "open -n failed"
    sleep 6
    count=$(pgrep -x juvin | wc -l | tr -d ' ')
    [[ $count -eq 1 ]] || fail "$count juvin processes after open -n"
    "$APP_EXE" --trigger || status=$?
    [[ $status -eq 0 ]] || fail "--trigger exited $status"
    "$APP_EXE" --background || status=$?
    [[ $status -eq 0 ]] || fail "--background exited $status"
    sleep 2
    count=$(pgrep -x juvin | wc -l | tr -d ' ')
    [[ $count -eq 1 ]] || fail "$count juvin processes after --background"
    note "open -n left one juvin; --trigger and --background exited 0"
    note "by hand: the window comes to the front, and --background shows none"
}

browser_row() {
    local name=$1 binary=$2
    [[ -n $APP_LOG ]] || skip "Juvin was started outside the kit, so its log is unknown: quit it and rerun --only $ROW"
    [[ -n $(juvin_pid) ]] || fail "Juvin is not running"
    [[ -x $binary ]] || fail "$binary is missing; run prepare.sh"
    node "$KIT/browser-check.mjs" "$name" --binary "$binary" --extension "$REPO_ROOT/extension/dist/$name" \
        --app-log "$APP_LOG" --app-exe "$APP_EXE" --out "$OUT" | tee "$OUT/$ROW.driver.txt"
    local status=${PIPESTATUS[0]}
    notes_from "$OUT/$ROW.driver.txt"
    grep '^FAIL: ' "$OUT/$ROW.driver.txt" | sed 's/^FAIL: //' >>"$OUT/$ROW.notes"
    [[ $status -eq 0 ]] || fail "see $ROW.log"
}

row_M_12_chrome() { browser_row chrome "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"; }
row_M_12_firefox() { browser_row firefox /Applications/Firefox.app/Contents/MacOS/firefox; }

row_M_30() {
    local models model tmp started
    if ! models=$(curl -fsS -m 10 http://127.0.0.1:11434/v1/models); then
        if $CI_MODE; then
            fail "nothing answers at 127.0.0.1:11434"
        fi
        skip "nothing answers at 127.0.0.1:11434: start the tunnel (session.sh tunnel) and rerun --only M-30"
    fi
    # Small general models first; the dictation-formatting models are never used.
    model=${JUVIN_KIT_MODEL:-$(python3 -c '
import json, sys
ids = [m["id"] for m in json.loads(sys.argv[1]).get("data", [])]
usable = [i for i in ids if "dictfmt" not in i.lower() and "embed" not in i.lower()]
for preferred in ("gemma3:4b", "qwen3:8b", "llama3.1:latest", "llama3.1", "juvin-stub"):
    if preferred in usable:
        print(preferred)
        break
else:
    print(usable[0] if usable else "")
' "$models")}
    [[ -n $model ]] || fail "the server lists no usable model"
    [[ $model != *dictfmt* ]] || fail "refusing the dictation model $model"
    tmp=$(mktemp -d "${TMPDIR:-/tmp}/juvin-kit-m30.XXXXXX")
    cat >"$tmp/input.json" <<'EOF'
{"draft": "hey robin\n\napril is mostly fine for me but not the first week, i have the audit then. also can we do somewhere closer this time, last year was a long drive.\n\nthx",
 "subject": "Team offsite in April",
 "thread": [{"from": "Robin Example <robin@example.com>", "to": ["me@example.com"], "sent": "2026-10-01T09:00:00Z",
             "body": "Hi all, does April work for the offsite? Same place as last year unless someone has a better idea."}]}
EOF
    started=$SECONDS
    "$CLI_EXE" rewrite --stdin --mode local --server http://127.0.0.1:11434 --model "$model" --action clearer \
        --settings "$tmp/settings.json" <"$tmp/input.json" >"$tmp/out.txt" 2>"$tmp/err.txt"
    local status=$?
    sed 's/^/  stderr: /' "$tmp/err.txt"
    local chars
    chars=$(wc -m <"$tmp/out.txt" | tr -d ' ')
    rm -rf "$tmp"
    [[ $status -eq 0 ]] || fail "juvin-cli rewrite exited $status with model $model"
    ((chars > 1)) || fail "juvin-cli rewrite printed nothing"
    note "juvin-cli rewrite with $model returned $chars chars in $((SECONDS - started)) s (synthetic draft)"
}

for id in $(echo "$ROWS" | cut -d'|' -f1); do
    selected "$id" && rm -f "$OUT/$id".{status,notes,seconds,done}
done

echo "[$(clock)] mode: $($CI_MODE && echo ci || echo session), version $KIT_VERSION"
run_group a-tests &
run_group a-keychain &
run_group a-rewrite &
run_group a1 &
A1_PID=$!
wait "$A1_PID"

APP_LOG=
NEEDS_APP=false
for id in $(rows_in b-data) $(rows_in b-manifests) $(rows_in b-login) $(rows_in b1); do
    if selected "$id"; then
        NEEDS_APP=true
    fi
done
if $NEEDS_APP && ! ensure_app; then
    for id in $(rows_in b-data) $(rows_in b-manifests) $(rows_in b-login) $(rows_in b1); do
        if selected "$id"; then
            echo fail >"$OUT/$id.status"
            echo "Juvin could not be started; see the run output" >"$OUT/$id.notes"
            touch "$OUT/$id.done"
        fi
    done
else
    run_group b-data &
    run_group b-manifests &
    run_group b-login &
    run_group b1 &
fi
wait

FAILED=0
{
    echo "# macOS automated rows"
    echo
    echo "Run $STAMP on $(sw_vers -productName) $(sw_vers -productVersion) ($(uname -m)), mode $($CI_MODE && echo ci || echo session), Juvin $KIT_VERSION$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null | sed 's/^/, commit /')."
    echo
    echo "| Row | Check | Result | Seconds | Notes |"
    echo "| --- | --- | --- | --- | --- |"
    for id in $(echo "$ROWS" | cut -d'|' -f1); do
        selected "$id" || continue
        status=$(cat "$OUT/$id.status" 2>/dev/null || echo fail)
        if [[ $status == fail ]]; then
            FAILED=$((FAILED + 1))
        fi
        notes=$(awk 'NR > 1 { printf "; " } { gsub(/\|/, "\\|"); printf "%s", $0 }' "$OUT/$id.notes" 2>/dev/null)
        echo "| $id | $(title_of "$id") | $status | $(cat "$OUT/$id.seconds" 2>/dev/null || echo -) | $notes |"
    done
} >"$OUT/summary.md"
cat "$OUT/summary.md"
echo
echo "Results: $OUT"
[[ $FAILED -eq 0 ]]
