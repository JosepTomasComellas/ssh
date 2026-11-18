#!/usr/bin/env bash
# Instal·la (o actualitza) el timer systemd que fa la còpia xifrada cada nit.
#
#   cd /docker/ssh-src && sudo ./scripts/install-backup-timer.sh [--remove]
#
# L'hora ve de BACKUP_TIME al .env (HH:MM, per defecte 03:30, hora local del servidor).
# Idempotent. --remove desactiva i esborra les unitats.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }

main() {
    local src env_file when unit_dir=/etc/systemd/system
    src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    env_file="${WEBSSH_ENV:-$src/.env}"
    [[ $EUID -eq 0 ]] || die "Executa'l amb sudo."
    command -v systemctl >/dev/null || die "Cal systemd."

    if [[ ${1:-} == "--remove" ]]; then
        systemctl disable --now webssh-backup.timer 2>/dev/null || true
        rm -f "$unit_dir/webssh-backup.service" "$unit_dir/webssh-backup.timer"
        systemctl daemon-reload
        echo "Timer eliminat."
        return 0
    fi
    [[ -z ${1:-} ]] || die "Argument desconegut: $1 (ús: install-backup-timer.sh [--remove])"

    [[ -f $env_file ]] || die "No trobo $env_file."
    when="$(python3 "$src/scripts/serverconfig.py" get BACKUP_TIME --env "$env_file")"
    [[ $when =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] || die "BACKUP_TIME ha de ser HH:MM (és «$when»)."
    [[ $src != *[[:space:]]* ]] || die "La ruta del clon no pot contenir espais: $src"

    sed "s|@SRC@|$src|g" "$src/deploy/systemd/webssh-backup.service" >"$unit_dir/webssh-backup.service"
    sed "s|@WHEN@|$when|g" "$src/deploy/systemd/webssh-backup.timer" >"$unit_dir/webssh-backup.timer"
    chmod 644 "$unit_dir/webssh-backup.service" "$unit_dir/webssh-backup.timer"
    systemctl daemon-reload
    systemctl enable --now webssh-backup.timer
    systemctl list-timers webssh-backup.timer --no-pager
    echo "Prova ara una còpia: sudo systemctl start webssh-backup.service && journalctl -u webssh-backup.service -n 30 --no-pager"
}

main "$@"
