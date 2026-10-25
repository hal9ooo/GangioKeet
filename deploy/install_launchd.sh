#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_PLIST="$SCRIPT_DIR/com.gangio.mlx-dictation.plist"
LABEL="com.gangio.mlx-dictation"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"
TARGET_PLIST="$LAUNCH_AGENTS_DIR/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/mlx-dictation"

UV_PATH="$(command -v uv || true)"
if [ -z "$UV_PATH" ]; then
    echo "error: uv was not found in PATH" >&2
    exit 1
fi

mkdir -p "$LAUNCH_AGENTS_DIR" "$LOG_DIR"
sed "s|__UV_PATH__|$UV_PATH|g" "$SOURCE_PLIST" > "$TARGET_PLIST"

# Idempotent reload: remove any previous registration before bootstrapping.
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl unload "$TARGET_PLIST" 2>/dev/null || true

if ! launchctl bootstrap "gui/$(id -u)" "$TARGET_PLIST" 2>/dev/null; then
    launchctl load "$TARGET_PLIST"
fi

echo "loaded $LABEL from $TARGET_PLIST"
