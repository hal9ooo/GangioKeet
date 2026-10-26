#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_PLIST="$SCRIPT_DIR/com.gangio.mlx-dictation.plist"
LABEL="com.gangio.mlx-dictation"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
TARGET_PLIST="$LAUNCH_AGENTS_DIR/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/mlx-dictation"

# The local plist may contain an API key, so keep newly created files private.
umask 077

RECORDING_ENABLED=0
RECORDING_VALUE="$(printf '%s' "${GANGIOKEET_RECORDING:-}" | tr '[:upper:]' '[:lower:]')"
case "$RECORDING_VALUE" in
    ""|0|false|no|off)
        ;;
    1|true|yes|on)
        RECORDING_ENABLED=1
        ;;
    *)
        echo "error: GANGIOKEET_RECORDING must be 1/true/yes/on or 0/false/no/off" >&2
        exit 1
        ;;
esac

UV_PATH="$(command -v uv || true)"
if [ -z "$UV_PATH" ]; then
    echo "error: uv was not found in PATH" >&2
    exit 1
fi

ESCAPED_UV_PATH="$(printf '%s' "$UV_PATH" | sed 's/[&|\\]/\\&/g')"
ESCAPED_HOME="$(printf '%s' "$HOME" | sed 's/[&|\\]/\\&/g')"
ESCAPED_PROJECT_DIR="$(printf '%s' "$PROJECT_DIR" | sed 's/[&|\\]/\\&/g')"

mkdir -p "$LAUNCH_AGENTS_DIR" "$LOG_DIR"
sed \
    -e "s|__UV_PATH__|$ESCAPED_UV_PATH|g" \
    -e "s|__HOME__|$ESCAPED_HOME|g" \
    -e "s|__PROJECT_DIR__|$ESCAPED_PROJECT_DIR|g" \
    "$SOURCE_PLIST" > "$TARGET_PLIST"
chmod 0600 "$TARGET_PLIST"

# A shell environment is not inherited by launchd. When the caller exported
# GANGIOKEET_API_KEY, embed it only in the generated local plist. When
# GANGIOKEET_RECORDING is enabled, append --recording to the program arguments.
# Python reads the key from its environment, never from argv, and the versioned
# template stays secret-free. With both options off the block is skipped.
if [ -n "${GANGIOKEET_API_KEY:-}" ] || [ "$RECORDING_ENABLED" = "1" ]; then
    PYTHON_PATH="$(command -v python3 || true)"
    if [ -z "$PYTHON_PATH" ] && [ -x /usr/bin/python3 ]; then
        PYTHON_PATH=/usr/bin/python3
    fi
    if [ -z "$PYTHON_PATH" ]; then
        echo "error: python3 is required to update the launch agent" >&2
        exit 1
    fi

    "$PYTHON_PATH" - "$TARGET_PLIST" "$RECORDING_ENABLED" <<'PY'
import os
import plistlib
import sys

target = sys.argv[1]
recording_enabled = sys.argv[2] == "1"

with open(target, "rb") as fh:
    plist = plistlib.load(fh)

if recording_enabled:
    arguments = plist["ProgramArguments"]
    if "--recording" not in arguments:
        arguments.append("--recording")

api_key = os.environ.get("GANGIOKEET_API_KEY", "")
if api_key:
    environment = plist.setdefault("EnvironmentVariables", {})
    environment["GANGIOKEET_API_KEY"] = api_key

with open(target, "wb") as fh:
    plistlib.dump(plist, fh)
PY

    chmod 0600 "$TARGET_PLIST"
fi

# Idempotent reload: remove any previous registration before bootstrapping.
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl unload "$TARGET_PLIST" 2>/dev/null || true

if ! bootstrap_error="$(launchctl bootstrap "gui/$(id -u)" "$TARGET_PLIST" 2>&1)"; then
    echo "warning: launchctl bootstrap failed: $bootstrap_error" >&2
    launchctl load "$TARGET_PLIST"
fi

if [ "$RECORDING_ENABLED" = "1" ]; then
    echo "recording mode enabled in $TARGET_PLIST"
fi
echo "loaded $LABEL from $TARGET_PLIST"
