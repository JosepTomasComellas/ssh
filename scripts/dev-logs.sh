#!/usr/bin/env bash
# Embolcall de scripts/dev.py logs (Docker Desktop / Linux).
set -euo pipefail
exec python3 "$(dirname "$0")/dev.py" logs "$@"

