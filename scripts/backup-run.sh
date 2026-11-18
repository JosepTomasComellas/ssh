#!/usr/bin/env bash
# Còpia xifrada programada: còpia (manage.py backup) + retenció + sincronització externa opcional.
#
#   sudo ./scripts/backup-run.sh          (el timer systemd la crida cada nit; vegeu install-backup-timer.sh)
#
# Llegeix el mateix .env que server-deploy.sh. Claus opcionals (valors per defecte):
#   BACKUP_KEEP=30        còpies que es conserven localment (les més noves)
#   BACKUP_REMOTE=        destí rsync (usuari@host:/ruta/), buit = sense còpia externa
#   BACKUP_MAX_HOURS=36   edat màxima acceptada de l'última còpia (només per al control final)
# Atura l'app uns segons mentre es fa la còpia (consistència). Surt amb error si falla qualsevol pas.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }
say() { echo "== $*"; }

main() {
    local src env_file root keep remote max_hours
    src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    env_file="${WEBSSH_ENV:-$src/.env}"
    [[ $EUID -eq 0 ]] || die "Executa'l amb sudo (cal root per als secrets i Docker)."
    [[ -f $env_file ]] || die "No trobo $env_file."
    for c in python3 age tar; do
        command -v "$c" >/dev/null || die "Falta «$c»."
    done

    local cfg_py="$src/scripts/serverconfig.py"
    get() { python3 "$cfg_py" get "$1" --env "$env_file"; }
    root="$(get WEBSSH_ROOT)"
    keep="$(get BACKUP_KEEP)"
    remote="$(get BACKUP_REMOTE)"
    max_hours="$(get BACKUP_MAX_HOURS)"
    [[ $keep =~ ^[0-9]+$ && $keep -ge 1 ]] || die "BACKUP_KEEP ha de ser un enter >= 1 (és «$keep»)."
    [[ $max_hours =~ ^[0-9]+$ ]] || die "BACKUP_MAX_HOURS ha de ser un enter (és «$max_hours»)."

    say "Còpia xifrada (l'app s'atura uns segons)"
    python3 "$src/scripts/manage.py" backup --root "$root"

    say "Retenció: es conserven les $keep més noves"
    python3 "$src/scripts/backup_tools.py" prune --dir "$root/backups" --keep "$keep"

    if [[ -n $remote ]]; then
        command -v rsync >/dev/null || die "BACKUP_REMOTE està definit però falta «rsync»."
        say "Sincronització externa cap a $remote"
        # Sense --delete: el destí conserva l'historial encara que aquí es purgui. Els .partial no s'hi envien.
        rsync -a --partial --chmod=F600 --exclude='*.partial' -e 'ssh -o BatchMode=yes' "$root/backups/" "$remote"
    else
        echo "   BACKUP_REMOTE buit: les còpies només són en aquest servidor."
    fi

    say "Control"
    python3 "$src/scripts/backup_tools.py" check --dir "$root/backups" --max-hours "$max_hours"
}

{ main "$@"; exit $?; }
