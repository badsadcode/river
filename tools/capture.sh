#!/usr/bin/env bash
# Render screenshots without a GPU (Xvfb + Mesa lavapipe). Slow (minutes), but faithful to Forward+.
#   tools/capture.sh "aerial,lake,meander@1" [extra args...]
# Shots: aerial lake meander riffle confluence water_close raft deck  (name@N = time-of-day preset N)
# Output: captures/<shot>.png
set -euo pipefail
cd "$(dirname "$0")/.."
SHOTS=${1:-aerial}; shift || true
RES=${RES:-1280x720}
timeout ${TIMEOUT:-1500} xvfb-run -a -s '-screen 0 1920x1080x24' godot --path . --audio-driver Dummy --resolution "$RES" -- --shots="$SHOTS" "$@" 2>&1 \
  | grep -E "SCREEN|SHADER|ERROR|Captured|DRIFT|error CS" | grep -v "ALSA" || true
