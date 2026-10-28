#!/usr/bin/env bash
# Crea un administrador global a l'app en marxa (dins el contenidor, amb la seva configuració).
#
#   sudo ./scripts/create-admin.sh admin@escola.cat [dies_sense_MFA_obligatori]   (per defecte 7; 0 = MFA des del primer accés)
#
# Genera una contrasenya aleatòria d'un sol ús i la mostra NOMÉS a la sortida d'aquesta ordre (no es desa ni es registra).
# L'usuari ha de canviar-la en el primer accés. Amb require_mfa actiu, el MFA queda exempt només durant els dies indicats.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }

[[ $# -ge 1 && $# -le 2 ]] || die "Ús: create-admin.sh correu@domini.cat [dies_sense_MFA_obligatori]"
email="$1"
days="${2:-7}"
[[ $EUID -eq 0 ]] || die "Executa'l amb sudo."
[[ "$days" =~ ^[0-9]+$ ]] || die "Els dies han de ser un nombre enter (0-30)."

project="${WEBSSH_COMPOSE_PROJECT:-web-ssh-terminal}"
container="$(docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=app" | head -n1)"
[[ -n $container ]] || die "No trobo el contenidor de l'app del projecte «$project». Està desplegada i en marxa?"

exec docker exec -i "$container" /app/WebSshTerminal --create-admin "$email" --mfa-grace-days "$days"
