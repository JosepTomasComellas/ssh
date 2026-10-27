#!/usr/bin/env bash
# Comprovacions locals prèvies a una release. Falla a la primera que falli; no declara cap criteri
# de deploy/application.json com a complert: això ho ha de fer una persona revisant l'evidència.
# Requisits: .NET SDK 10, Docker (Compose v2), Python 3, git, i l'entorn local aixecable (scripts/dev.py).
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== Format i build (Release, avisos com a errors)"
dotnet build -c Release --nologo -v q   # també fa el restore
dotnet format --verify-no-changes --no-restore

echo "== Dependències vulnerables"
out="$(dotnet list src/WebSshTerminal package --vulnerable --include-transitive 2>&1)"
echo "$out" | grep -q "has no vulnerable packages" || { echo "$out"; exit 1; }
(cd tools/xterm && npm ci --ignore-scripts >/dev/null && npm audit --omit=dev)

echo "== Proves .NET (inclou OpenSSH/tmux reals en Docker)"
dotnet test -c Release --nologo -v q

echo "== Entorn local, e2e de navegador (Nginx i Traefik) i proves d'operació"
python3 scripts/dev.py up --proxy nginx
python3 scripts/dev.py test --proxy nginx
python3 tests/ops_test.py
python3 scripts/dev.py up --proxy traefik
python3 scripts/dev.py test --proxy traefik

echo "== Proves de l'infraestructura de desplegament"
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tests/compose_check.py

echo "OK: totes les comprovacions locals han passat. Revisa l'evidència abans de marcar criteris a deploy/application.json."
