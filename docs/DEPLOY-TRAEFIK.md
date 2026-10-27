# Desplegament a producció amb Traefik existent (pas a pas)

> **Estat:** el flux no s'ha provat en un servidor Linux real ni amb un *certresolver* ACME real. Les proves locals
> cobreixen el Compose (`tests/compose_check.py`), els scripts (`tests/test_deployment.py`, en Linux) i l'app darrere
> d'un Traefik de proves (`python scripts/dev.py test --proxy traefik`). Fes un primer desplegament en un entorn
> de prova abans d'exposar-lo.

## 0. Bloqueig actual de la release

`deploy/application.json` és en `status: scaffold` i el workflow `release.yml` **no publicarà imatge** fins que
una persona revisi l'evidència, fixi les imatges base per digest i marqui els criteris (vegeu
[RELEASES.md](RELEASES.md)). Sense una imatge `ghcr.io/…@sha256:…` publicada, `manage.py deploy` no té què desplegar.

## 1. Requisits del servidor

- Linux amd64, Docker Engine amb Compose ≥ 2.20, Python 3.10+, Git, `age`, `tar`.
- Traefik **ja en marxa**, amb: una xarxa Docker externa (p. ex. `web`), un *entrypoint* `websecure` (443) i un
  *certresolver* ACME (p. ex. `letsencrypt`). El Compose **no aixeca ni modifica Traefik**.
- DNS: el domini apunta al servidor. Port 443 obert (el que ja usa Traefik).
- Un servidor SSH remot únic, accessible des del servidor Docker, i la seva clau de host verificada **fora de banda**.
- Una clau pública `age` (la privada es guarda fora del servidor) per a les còpies.

## 2. Inicialitzar

```bash
sudo python3 scripts/manage.py init      # crea /opt/web-ssh-terminal… (mai sobreescriu); no arrenca res
```

Genera `app_key`, `bootstrap_password` i `metrics_token` (propietari UID 1654, mode 0400) i un `config.json` d'exemple.

## 3. Configurar `config.json`

| Camp | Valor |
|---|---|
| `domain` | el teu domini (per a aquesta instal·lació: `ssh.sdslab.cat`) |
| `proxy` | `traefik` |
| `proxy_network` | nom de la xarxa externa de Traefik (`docker network ls`) |
| `traefik_resolver` | nom del *certresolver* configurat a Traefik |
| `trusted_proxies` | **IP exactes** del contenidor Traefik a la xarxa del backend (no rangs). Vegeu 3.1 |
| `backend_subnet` | subxarxa lliure per a la xarxa privada de l'app (p. ex. `172.30.50.0/24`) |
| `ssh_host`, `ssh_port` | el servidor SSH únic de destí (nom o IP, sense esquema ni usuari) |
| `timezone` | zona dels logs (p. ex. `Europe/Madrid`) |
| `backup_recipient` | clau pública `age1…` |
| `http_port` | no s'usa amb Traefik (només Nginx) |

### 3.1 `trusted_proxies`

L'app només fa cas de `X-Forwarded-*` si la petició ve d'aquestes IP. Si no coincideix, perdràs la IP/protocol
real i el login pot redirigir malament. Comprova la IP que veu l'app: la de Traefik a la xarxa que comparteixen.
Com que Traefik és a la xarxa externa (`web`) i l'app s'hi connecta, `trusted_proxies` ha de ser la **IP de
Traefik a la xarxa `web`**. Fixa-la (`ipv4_address`) a la definició de Traefik o revisa-la després de cada
recreació del contenidor.

## 4. Secrets (`<arrel>/secrets/`, propietari 1654, mode 0400)

- `known_hosts`: línia(es) `host ssh-ed25519 AAAA…` del servidor SSH, obtingudes i **verificades per un altre canal**
  (no facis `ssh-keyscan` a cegues). Si el port no és el 22, usa `[host]:port`. No s'admeten hosts hashejats.
- `ssh_credentials`: no s'usa en aquesta versió; ha d'existir i no ser buit (`{}`).
- `metrics_token`: token Bearer del port de mètriques (obligatori, perquè l'app comparteix xarxa amb Traefik).

## 5. Desplegar

```bash
sudo python3 scripts/manage.py deploy vX.Y.Z <commit-sha-40> ghcr.io/joseptomascomellas/ssh@sha256:<digest>
```

Fa còpia xifrada, arrenca, espera `/health/ready` local, comprova la **mateixa revisió** per HTTPS i, si falla,
restaura la versió anterior. La primera iteració pot tenir una interrupció breu. Vegeu [OPERATIONS.md](OPERATIONS.md)
per a `status`, `backup`, `rollback` i `recover`.

## 6. Primer accés

1. Llegeix la contrasenya inicial: `sudo cat <arrel>/secrets/bootstrap_password`. Entra com a `admin`; et farà canviar-la.
2. **Admin → Identitats:** crea una identitat per compte Linux remot (clau privada **sense passphrase**, d'ús exclusiu).
3. **Admin → Usuaris:** crea els usuaris i assigna'ls una identitat cadascun.
4. Al servidor remot, limita `sudo` per compte (`/etc/sudoers.d/`); l'app no concedeix privilegis.

## 7. Comprovacions després del desplegament

- `https://<domini>/health/ready` retorna `{"status":"ok","revision":"<commit>"}` amb el commit esperat.
- `https://<domini>/metrics` **no** respon (404/redirecció): el port de mètriques no és públic.
- Una sessió de terminal aguanta un refresh i es reconnecta (tmux).
- `docker compose logs app`: només línies d'inici; cap secret.

## 8. Recomanació de seguretat abans d'exposar a Internet

El MFA (TOTP) **encara no està implementat**. Amb només contrasenya, una contrasenya robada dona accés a una shell.
Considera restringir l'accés a nivell de Traefik (llista d'IP o autenticació prèvia) fins que hi hagi un segon factor.
