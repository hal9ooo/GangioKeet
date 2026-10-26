#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_PLIST="$SCRIPT_DIR/com.gangio.mlx-dictation.plist"
LABEL="com.gangio.mlx-dictation"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
TARGET_PLIST="$LAUNCH_AGENTS_DIR/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/mlx-dictation"

# The local plist may contain an API key, so keep newly created files private.
umask 077

UV_PATH="$(command -v uv || true)"
if [ -z "$UV_PATH" ]; then
    echo "error: uv was not found in PATH" >&2
    exit 1
fi

ESCAPED_UV_PATH="$(printf '%s' "$UV_PATH" | sed 's/[&|\\]/\\&/g')"

mkdir -p "$LAUNCH_AGENTS_DIR" "$LOG_DIR"
sed "s|__UV_PATH__|$ESCAPED_UV_PATH|g" "$SOURCE_PLIST" > "$TARGET_PLIST"
chmod 0600 "$TARGET_PLIST"

# A shell environment is not inherited by launchd. When the caller exported
# GANGIOKEET_API_KEY, embed it only in the generated local plist. Python reads
# the value from its environment, never from argv, and the versioned template
# stays secret-free. With auth off the field is omitted entirely.
if [ -n "${GANGIOKEET_API_KEY:-}" ]; then
    PYTHON_PATH="$(command -v python3 || true)"
    if [ -z "$PYTHON_PATH" ] && [ -x /usr/bin/python3 ]; then
        PYTHON_PATH=/usr/bin/python3
    fi
    if [ -z "$PYTHON_PATH" ]; then
        echo "error: python3 is required to embed GANGIOKEET_API_KEY" >&2
        exit 1
    fi

    "$PYTHON_PATH" - "$TARGET_PLIST" <<'PY'
import os
import plistlib
import sys

target = sys.argv[1]
with open(target, "rb") as fh:
    plist = plistlib.load(fh)
environment = plist.setdefault("EnvironmentVariables", {})
environment["GANGIOKEET_API_KEY"] = os.environ["GANGIOKEET_API_KEY"]
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

echo "loaded $LABEL from $TARGET_PLIST"
