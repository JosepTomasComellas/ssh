# Desplegament a producció amb Traefik existent (pas a pas)

> **Estat:** el flux no s'ha provat en un servidor Linux real ni amb un *certresolver* ACME real. Les proves locals
> cobreixen el Compose (`tests/compose_check.py`), els scripts (`tests/test_deployment.py`, en Linux) i l'app darrere
> d'un Traefik de proves (`python scripts/dev.py test --proxy traefik`). Fes un primer desplegament en un entorn
> de prova abans d'exposar-lo.

## Via ràpida (recomanada): un script i un `.env`

Al servidor (Linux amb Docker, Git, Python 3 i `age`; Traefik ja en marxa i el DNS apuntant-hi):

```bash
cd /docker && git clone https://github.com/JosepTomasComellas/ssh.git ssh-src && cd ssh-src
sudo ./scripts/server-deploy.sh        # 1a vegada: crea .env i s'atura
sudo nano .env                         # només DOMAIN i BACKUP_RECIPIENT
sudo ./scripts/server-deploy.sh        # desplega l'última versió (o: ./scripts/server-deploy.sh v1.0.1)
```

L'script fa `git pull`, instal·la a `/docker/ssh` (`WEBSSH_ROOT`), genera `config.json` i els secrets que falten,
detecta la IP de Traefik, tria una subxarxa lliure (la IP del servidor dins d'aquesta xarxa és el destí SSH), resol el
commit i el digest de la imatge i executa el desplegament amb còpia i restauració automàtica. Per veure què faria sense
tocar res: `./scripts/server-deploy.sh --check`. Per actualitzar més endavant: la mateixa ordre.

El destí SSH és **aquest mateix servidor**: cal que el firewall permeti la subxarxa de l'app al port 22
(`ufw allow from <backend_subnet> to any port 22 proto tcp`; l'script mostra la subxarxa) i crear una clau per compte
Linux (apartat «Identitats»). La resta d'aquesta guia explica el procés manual i serveix per entendre què fa l'script.
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
sudo python3 scripts/manage.py init      # crea /opt/web-ssh-terminal (mai sobreescriu); no arrenca res
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
| `require_mfa` | `true` (recomanat): tothom ha d'activar TOTP en el primer accés i no el pot desactivar |
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

Si el paquet de GitHub (GHCR) és privat, abans: `echo <PAT amb read:packages> | sudo docker login ghcr.io -u <usuari> --password-stdin`.

```bash
sudo python3 scripts/manage.py deploy --tag vX.Y.Z --commit <commit-sha-40> \
     --image ghcr.io/joseptomascomellas/ssh@sha256:<digest>
```

Fa còpia xifrada, arrenca, espera `/health/ready` local, comprova la **mateixa revisió** per HTTPS i, si falla,
restaura la versió anterior. La primera iteració pot tenir una interrupció breu. Vegeu [OPERATIONS.md](OPERATIONS.md)
per a `status`, `backup`, `rollback` i `recover`.

## 6. Primer accés

1. Llegeix la contrasenya inicial: `sudo cat <arrel>/secrets/bootstrap_password`. Entra com a `admin`; et farà canviar-la.
2. **Admin → Identitats:** crea una identitat per compte Linux remot (clau privada **sense passphrase**, d'ús exclusiu).
3. **Admin → Usuaris:** crea els usuaris i assigna'ls una identitat cadascun.
4. Al servidor remot, limita `sudo` per compte (`/etc/sudoers.d/`); l'app no concedeix privilegis.

## 6bis. Administrador global per a la primera administració (sense OTP temporalment)

Si vols un compte propi en lloc de l'`admin` inicial (p. ex. `admin@escola.cat`) i no voleu fer el MFA el primer dia:

```bash
sudo ./scripts/create-admin.sh admin@escola.cat        # 7 dies sense MFA obligatori (per defecte)
sudo ./scripts/create-admin.sh admin@escola.cat 3      # 3 dies;  0 = MFA obligatori des del primer accés
```

Mostra **una sola vegada** una contrasenya aleatòria de 24 caràcters (no es desa ni es registra) i l'usuari l'ha de canviar en entrar. Amb `require_mfa: true`, aquest compte pot treballar sense OTP **només fins que caduqui l'exempció** (màx. 30 dies); després l'app el redirigeix a activar-lo. És temporal a propòsit: un administrador pot crear identitats i, per tant, donar accés a shells. Recomanacions: activa el MFA tan aviat com puguis, i desactiva (Admin → Usuaris → Desactiva) o protegeix amb MFA l'`admin` inicial. L'ordre no sobreescriu mai un usuari existent.

## 7. Comprovacions després del desplegament

- `https://<domini>/health/ready` retorna `{"status":"ok","revision":"<commit>"}` amb el commit esperat.
- `https://<domini>/metrics` **no** respon (404/redirecció): el port de mètriques no és públic.
- Una sessió de terminal aguanta un refresh i es reconnecta (tmux).
- `docker compose logs app`: només línies d'inici; cap secret.

## 8. MFA (TOTP)

Hi ha TOTP (RFC 6238) amb 10 codis de recuperació d'un sol ús. Amb `require_mfa: true`, cada usuari (admin inclòs) és
redirigit a **MFA** en el primer accés, no pot fer res més fins que l'activa, i no pot desactivar-lo després. Desa els
codis de recuperació de l'admin fora del servidor: si l'admin perd l'aplicació i els codis, caldrà intervenir a la
base de dades (`webssh.db`). Amb `require_mfa: false` el MFA és opcional per usuari (pàgina **MFA** del menú).