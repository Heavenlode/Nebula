#!/usr/bin/env bash
# End-to-end NetArray replication test for the Nebula addon.
#
# Spawns a headless Nebula server hosting NetArraySyncTestWorld and a headless client, and verifies
# that the world's NetArray<byte> properties (on the root and on a static child) plus a parent value
# property replicate byte-exact through the real NetPropertiesSerializer Export/Import path over ENet
# -- the integration path the in-process unit tests deliberately bypass.
#
# PASS  = client printed [NETSYNC PASS] and logged no import/desync errors.
# FAIL  = mismatch, timeout, server bind failure, or any ImportState/desync error.
#
# Portable: works for any Godot project that includes the Nebula addon. Godot is discovered from the
# GODOT env var or PATH; the project root (project.godot) is discovered by walking up from this script.
#
# Usage:
#   ./run_netsync_test.sh                 # auto-discovers a 'godot'/'godot4' on PATH
#   GODOT=/path/to/godot ./run_netsync_test.sh
set -uo pipefail

# --- locate the Godot binary (must be a .NET/mono build) ---
GODOT_BIN="${GODOT:-}"
if [[ -z "$GODOT_BIN" ]]; then
  for name in godot godot4 Godot; do
    if command -v "$name" >/dev/null 2>&1; then GODOT_BIN="$(command -v "$name")"; break; fi
  done
fi
if [[ -z "$GODOT_BIN" ]] || { ! command -v "$GODOT_BIN" >/dev/null 2>&1 && [[ ! -x "$GODOT_BIN" ]]; }; then
  echo "ERROR: Godot 4 (.NET/mono) binary not found." >&2
  echo "  Set GODOT to your Godot binary, e.g.  GODOT=/path/to/godot $0" >&2
  exit 2
fi

# --- locate the project root (walk up to project.godot) ---
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
while [[ "$PROJECT_DIR" != "/" && ! -f "$PROJECT_DIR/project.godot" ]]; do
  PROJECT_DIR="$(dirname "$PROJECT_DIR")"
done
if [[ ! -f "$PROJECT_DIR/project.godot" ]]; then
  echo "ERROR: could not locate project.godot above $SCRIPT_DIR" >&2
  exit 2
fi

BOOT="res://addons/Nebula/Testing/NetSync/NetArraySyncBootstrap.tscn"
WORLD="res://addons/Nebula/Testing/NetSync/NetArraySyncTestWorld.tscn"
# Errors that indicate a corrupted/desynced tick stream on the client.
DESYNC_RE="ImportState (ERROR|FAILED)|Unknown delta encoding|Unsupported cache type|Cannot read"

LOG_DIR="$(mktemp -d)"
SERVER_LOG="$LOG_DIR/server.log"
CLIENT_LOG="$LOG_DIR/client.log"
echo "Project: $PROJECT_DIR"
echo "Godot:   $GODOT_BIN"
echo "Logs:    $LOG_DIR"

cleanup() {
  [[ -n "${SERVER_PID:-}" ]] && kill "$SERVER_PID" 2>/dev/null
  [[ -n "${CLIENT_PID:-}" ]] && kill "$CLIENT_PID" 2>/dev/null
}
trap cleanup EXIT

# --- server ---
"$GODOT_BIN" --path "$PROJECT_DIR" --headless "$BOOT" --server "--initialWorldScene=$WORLD" \
  >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
echo "Server PID $SERVER_PID; waiting for boot..."
sleep 5

if ! kill -0 "$SERVER_PID" 2>/dev/null; then
  echo "RESULT: FAIL (server exited during boot)"
  echo "--- server.log ---"; tail -40 "$SERVER_LOG"
  exit 1
fi
if grep -q "Host creation call failed" "$SERVER_LOG"; then
  echo "RESULT: FAIL (server could not bind its port -- is another Nebula server/instance already running?)"
  echo "--- server.log ---"; tail -20 "$SERVER_LOG"
  exit 1
fi

# --- client (self-quits on pass/fail or its own internal timeout) ---
"$GODOT_BIN" --path "$PROJECT_DIR" --headless "$BOOT" >"$CLIENT_LOG" 2>&1 &
CLIENT_PID=$!
echo "Client PID $CLIENT_PID; running..."

for _ in $(seq 1 60); do
  kill -0 "$CLIENT_PID" 2>/dev/null || break
  sleep 1
done
if kill -0 "$CLIENT_PID" 2>/dev/null; then
  echo "Client did not exit within 60s; killing."
  kill "$CLIENT_PID" 2>/dev/null
fi

echo "===== client [NETSYNC] lines ====="
grep -E "\[NETSYNC" "$CLIENT_LOG" || echo "(none)"
echo "===== client desync/import errors ====="
grep -E "$DESYNC_RE" "$CLIENT_LOG" || echo "(none)"

if grep -q "\[NETSYNC PASS\]" "$CLIENT_LOG" && ! grep -qE "$DESYNC_RE" "$CLIENT_LOG"; then
  echo "RESULT: PASS"
  exit 0
fi

echo "RESULT: FAIL"
echo "--- server.log tail ---"; tail -30 "$SERVER_LOG"
echo "--- client.log tail ---"; tail -50 "$CLIENT_LOG"
exit 1
