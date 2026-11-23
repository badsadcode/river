#!/usr/bin/env bash
# Headless raft drift test: drops the raft at the start, lets it drift for N simulated seconds with nobody
# steering, and prints its track (every 10 s) and a summary: distance, max speed, max tilt, time aground.
#   tools/drift_test.sh 600
set -euo pipefail
cd "$(dirname "$0")/.."
SECS=${1:-300}
timeout ${TIMEOUT:-1800} godot --headless --path . --audio-driver Dummy -- --drift-test="$SECS" 2>&1 | grep -E "DRIFT|ERROR|error" || true
