#!/usr/bin/env bash
# Desplega Web SSH Terminal al servidor (Traefik existent) amb UNA ordre.
#
#   cd /docker/ssh-src && sudo ./scripts/server-deploy.sh [vX.Y.Z] [--check]
#
# Sense argument desplega l'última versió (tag vX.Y.Z). Fa git pull de main (scripts). Llegeix .env (mínim: DOMAIN i BACKUP_RECIPIENT; vegeu
# deploy/server.env.example) i fa: init (només la 1a vegada), genera config.json i
# els secrets que falten, resol commit i digest de la imatge i executa `manage.py deploy` (còpia, arrencada,
# comprovació HTTPS i restauració automàtica si falla). És idempotent: es pot repetir per actualitzar.
# --check: mostra què faria (config generada, sense secrets) sense desplegar.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }
say() { echo "== $*"; }

main() {
    local src env_file tag check=0 arg
    src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    env_file="${WEBSSH_ENV:-$src/.env}"

    for arg in "$@"; do
        case "$arg" in
            --check) check=1 ;;
            v*) tag="$arg" ;;
            *) die "Argument desconegut: $arg (ús: server-deploy.sh [vX.Y.Z] [--check])" ;;
        esac
    done

    [[ $EUID -eq 0 ]] || die "Executa'l amb sudo (cal root per als secrets i Docker)."
    for c in docker git python3 age tar; do
        command -v "$c" >/dev/null || die "Falta «$c». A Ubuntu: apt install -y age git python3 tar (Docker: docs.docker.com)."
    done
    docker compose version >/dev/null 2>&1 || die "Cal Docker Compose v2."

    if [[ ! -f $env_file ]]; then
        install -m 600 "$src/deploy/server.env.example" "$env_file"
        die "He creat $env_file. Edita'l (DOMAIN i BACKUP_RECIPIENT) i torna a executar l'script."
    fi
    [[ "$(stat -c '%a' "$env_file")" == "600" ]] || die "$env_file ha de tenir mode 600 (chmod 600 $env_file)."

    local cfg_py="$src/scripts/serverconfig.py"
    get() { python3 "$cfg_py" get "$1" --env "$env_file"; }
    local root image
    root="$(get WEBSSH_ROOT)"
    image="$(get GHCR_IMAGE)"
    if [[ "$(realpath -m "$root")" == "$(realpath -m "$src")" ]]; then
        die "El clon del codi ($src) coincideix amb l'arrel d'instal·lació ($root). Mou el clon (p. ex. mv $src ${src}-src) i executa l'script des d'allà, o defineix WEBSSH_ROOT al .env."
    fi

    say "Codi font: git pull (scripts) i versió a desplegar"
    if ! git -C "$src" diff --quiet || ! git -C "$src" diff --cached --quiet; then
        die "El clon $src té canvis locals; desa'ls o descarta'ls."
    fi
    # Els scripts vénen sempre de main; el codi de l'aplicació el baixa manage.py del tag, dins releases/.
    git -C "$src" checkout --quiet main
    git -C "$src" pull --ff-only --quiet origin main
    git -C "$src" fetch --tags --force --quiet origin
    # El pull pot haver actualitzat aquest mateix script: es reexecuta una sola vegada amb la versió nova.
    if [[ -z "${WEBSSH_REEXEC:-}" ]]; then
        WEBSSH_REEXEC=1 exec bash "$src/scripts/server-deploy.sh" "$@"
    fi
    if [[ -z "${tag:-}" ]]; then
        tag="$(git -C "$src" tag --list 'v[0-9]*' --sort=-v:refname | head -n1)"
        [[ -n $tag ]] || die "No hi ha cap tag de versió (vX.Y.Z)."
    fi
    [[ "$(git -C "$src" cat-file -t "refs/tags/$tag" 2>/dev/null)" == "tag" ]] || die "«$tag» no és un tag anotat existent."
    local commit
    commit="$(git -C "$src" rev-list -n1 "$tag")"
    echo "   versió $tag, commit $commit"
    if [[ ! -d $root ]]; then
        say "Primera instal·lació: init a $root"
        [[ $check -eq 1 ]] || python3 "$src/scripts/manage.py" init --root "$root"
    fi

    say "Generant config.json des de $env_file"
    local tmp
    tmp="$(mktemp)"
    trap 'rm -f "${tmp:-}"' EXIT
    python3 "$cfg_py" render --env "$env_file" --existing "$root/config.json" --out "$tmp"
    echo "   $(python3 -c 'import json,sys; c=json.load(open(sys.argv[1])); c["backup_recipient"]="(ocult)"; print(json.dumps(c))' "$tmp")"
    if [[ $check -eq 1 ]]; then
        echo "--check: no s'ha desplegat res."
        exit 0
    fi
    install -m 600 -o root -g root "$tmp" "$root/config.json"

    say "Secrets que falten"
    local ssh_host_key=/etc/ssh/ssh_host_ed25519_key.pub
    if [[ ! -s "$root/secrets/ssh_credentials" ]]; then
        printf '{}\n' >"$root/secrets/ssh_credentials"
    fi
    # known_hosts: la clau de host d'aquest servidor, amb el nom exacte de ssh_host. Es regenera sempre (és pública).
    [[ -r $ssh_host_key ]] || die "No trobo $ssh_host_key (el destí SSH és aquest servidor). Defineix-lo manualment a $root/secrets/known_hosts."
    rm -f "$root/secrets/known_hosts"
    python3 "$cfg_py" known-hosts --config "$root/config.json" --pubkey "$ssh_host_key" >"$root/secrets/known_hosts"
    chown 1654:1654 "$root/secrets/ssh_credentials" "$root/secrets/known_hosts"
    chmod 0400 "$root/secrets/ssh_credentials" "$root/secrets/known_hosts"

    say "Imatge de la release"
    docker pull --quiet "$image:$tag" >/dev/null ||
        die "No puc baixar $image:$tag. Si el paquet és privat: docker login ghcr.io (token amb read:packages)."
    local digest_ref
    digest_ref="$(docker image inspect "$image:$tag" --format '{{range .RepoDigests}}{{println .}}{{end}}' | grep "^$image@sha256:" | head -n1)"
    [[ -n $digest_ref ]] || die "No s'ha pogut obtenir el digest de $image:$tag."
    echo "   $digest_ref"

    say "Desplegant (còpia, arrencada, comprovació per HTTPS)"
    python3 "$src/scripts/manage.py" deploy --root "$root" --tag "$tag" --commit "$commit" --image "$digest_ref"

    say "Fet"
    python3 "$src/scripts/manage.py" status --root "$root"
    local domain
    domain="$(python3 "$cfg_py" field domain --config "$root/config.json")"
    echo "Obre https://$domain  (usuari: admin; contrasenya inicial: sudo cat $root/secrets/bootstrap_password)"
}

{ main "$@"; exit $?; }
