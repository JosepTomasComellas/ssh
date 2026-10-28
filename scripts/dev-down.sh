#!/usr/bin/env bash
# Embolcall de scripts/dev.py down (Docker Desktop / Linux).
set -euo pipefail
exec python3 "$(dirname "$0")/dev.py" down "$@"

