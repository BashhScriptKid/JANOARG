#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_PATH="$SCRIPT_DIR"

UNITY_VERSION=""
if [[ -f "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" ]]; then
    UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" | tr -d '[:space:]')"
fi

UNITY_BIN=""
for candidate in \
    "$HOME/Unity/Hub/Editor/${UNITY_VERSION}/Editor/Unity" \
    "/opt/Unity/Editor/Unity" \
    "/usr/lib/unity/Editor/Unity"; do
    if [[ -n "$UNITY_VERSION" && -x "$candidate" ]]; then
        UNITY_BIN="$candidate"
        break
    fi
done

if [[ -z "$UNITY_BIN" ]]; then
    UNITY_BIN="$(find / -path /proc -prune -o -type f -name Unity -path "*/Editor/Unity" -print 2>/dev/null | head -n 1)"
fi

if [[ -z "$UNITY_BIN" || ! -x "$UNITY_BIN" ]]; then
    echo "Unity editor not found" >&2
    exit 1
fi

LOG_FILE=""
UNITY_ARGS=()
PARSE_OPTIONS=1
while [[ $# -gt 0 ]]; do
    if [[ "$PARSE_OPTIONS" -eq 0 ]]; then
        UNITY_ARGS+=("$1")
        shift
        continue
    fi
    case "$1" in
        --)
            PARSE_OPTIONS=0
            shift
            ;;
        --log)
            shift
            if [[ $# -gt 0 && ( "$1" == "-" || "$1" != -* ) ]]; then
                LOG_FILE="$1"
                shift
            else
                LOG_FILE="-"
            fi
            ;;
        *)
            UNITY_ARGS+=("$1")
            shift
            ;;
    esac
done

if [[ -z "$LOG_FILE" ]]; then
    LOG_FILE="-"
fi

if [[ "$LOG_FILE" != "-" ]]; then
    mkdir -p "$(dirname "$LOG_FILE")"
fi

exec "$UNITY_BIN" -projectPath "$PROJECT_PATH" -logFile "$LOG_FILE" "${UNITY_ARGS[@]}"
