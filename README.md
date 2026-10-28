# Web SSH Terminal · TuteApps

Terminal SSH al navegador, multiusuari, cap a **un únic servidor remot** configurat per l'administrador.
Aplicació .NET 10 (Blazor, MudBlazor) amb xterm.js, sessions persistents amb **tmux** al servidor remot,
transferència de fitxers per SFTP i auditoria de metadades. Llicència [GNU AGPL-3.0](LICENSE).

> **Estat: en desenvolupament.** L'aplicació funciona i està provada en local (Docker Desktop), però **no s'ha
> validat en un servidor Linux real, amb DNS ni amb certificats públics**. No es considera llesta per a producció.
> Vegeu [Estat i limitacions](#estat-i-limitacions).

## Per a qui és

Per a qui vol donar accés a una shell d'un servidor concret a persones amb compte propi, sense instal·lar clients
SSH, amb control per usuari de quin compte Linux fa servir cadascú.

## Com funciona

```
Navegador ──HTTPS/WSS──▶ Nginx o Traefik ──▶ App (ASP.NET Core 10)  ──SSH/SFTP──▶ Un únic servidor Linux
                                              ├─ comptes locals (Identity + SQLite)      └─ tmux (sessions persistents)
                                              ├─ identitats SSH xifrades (AES-GCM)
                                              └─ /metrics en un port intern separat
```

- Cada usuari web té **una identitat SSH assignada explícitament** (compte Linux + clau privada, xifrada en repòs).
  Una identitat només es pot assignar a un usuari.
- La clau de host del servidor es valida contra un `known_hosts` aprovat fora de banda; un host desconegut o una
  clau canviada **es rebutgen**. No hi ha cap camp per introduir hosts: el destí és sempre el configurat.
- Les claus privades no arriben mai al navegador, ni als logs, ni a la base de dades en clar.
- El `sudo` es concedeix al servidor remot (per compte); l'aplicació no és root ni usa `sudo`.

## Funcions

| Funció | Estat |
|---|---|
| Login, canvi forçat de contrasenya, bloqueig, límit d'intents, rols admin/usuari, revocació de sessions | Fet |
| Terminal interactiu (xterm.js), resize, reconnexió a tmux, límits de sessions i d'inactivitat | Fet |
| Gestió d'identitats SSH i assignació (admin) | Fet |
| SFTP: llistar, pujar (amb confirmació de sobreescriptura), baixar; camins validats, enllaços simbòlics rebutjats | Fet |
| Porta-retalls (només amb acció explícita de l'usuari) | Fet |
| Auditoria de metadades (actor, hora, sessió, operació, resultat, bytes) | Fet |
| Mètriques Prometheus agregades + dashboard Grafana | Fet (dashboard sense validar amb dades reals) |
| Interfície amb el branding de Salesians de Sarrià (el d'AutoCo): barra slate, targetes, pantalla d'accés amb fons, tema clar/fosc, responsiu; configurable (`WebSsh__Branding__*` i fitxers a `<dades>/branding/`). Crèdit fix **TuteApps** | Fet |
| Interfície en castellà | Pendent |
| MFA (TOTP) amb QR i codis de recuperació; obligatori amb `require_mfa`; ordre `create-admin` amb exempció temporal | Fet |
| Desplegament a Linux provat en un servidor real | Pendent (cal servidor, DNS i certificat) |

## Modes de desplegament

| | **Local (Docker Desktop)** | **Linux + Nginx** | **Linux + Traefik existent** |
|---|---|---|---|
| Ús | Desenvolupar i provar | Producció (Nginx instal·lat al host) | Producció (ja tens Traefik) |
| Què aixeca Compose | app, servidor SSH de proves, Nginx *o* Traefik de proves | només l'app (port a `127.0.0.1`) | només l'app, a la xarxa de Traefik |
| Domini / TLS | `https://localhost:8443`, certificat autosignat | el teu domini, certificat teu (p. ex. Let's Encrypt) | el teu domini, el *certresolver* de Traefik |
| Servidor SSH | contenidor efímer (comptes `alice`, `bob`) | el teu servidor | el teu servidor |
| Guia | [Mode local](#mode-local) | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | [docs/DEPLOY-TRAEFIK.md](docs/DEPLOY-TRAEFIK.md) |

El Compose **no configura** el teu Nginx/Traefik, el DNS, el certificat ni el firewall: això és responsabilitat
de l'administrador (plantilla Nginx a [deploy/nginx](deploy/nginx/webssh.conf.template), etiquetes a
[deploy/compose.traefik.yaml](deploy/compose.traefik.yaml)). **No s'ha provat cap d'aquests dos modes en un servidor real.**

## Mode local

Requisits: Docker Desktop (Compose v2), Python 3 i Git. No cal domini, DNS, GHCR ni cap servidor SSH propi.

```powershell
python scripts/dev.py up                  # construeix i aixeca (Nginx); --proxy traefik per a Traefik; --monitoring per a Prometheus/Grafana
python scripts/dev.py fixture-keys        # copia les claus de proves a .dev\fixture-keys\
```

1. Obre **https://localhost:8443** (`https`, no `http`) i accepta l'avís del certificat autosignat.
2. Usuari `admin`; la contrasenya és a `.dev\secrets\bootstrap_password` (`Get-Content .dev\secrets\bootstrap_password`). Te'n demanarà una de nova.
3. A **Identitats**, crea una identitat (compte Linux `alice`) enganxant la clau de `.dev\fixture-keys\alice`.
4. A **Usuaris**, crea un usuari i assigna-li la identitat. Entra amb ell (canvia la contrasenya temporal) i obre **Terminal → Nova sessió**.

```powershell
python scripts/dev.py test [--proxy traefik]   # proves e2e al navegador (Playwright)
python tests/ops_test.py                       # proves d'operació (persistència, secrets, còpia/restauració)
python scripts/dev.py logs --errors            # només Warning/Error/Critical de l'app
python scripts/dev.py down                     # atura (conserva dades)
python scripts/dev.py reset --yes              # elimina els volums i .dev/ LOCALS
```

Monitoratge opcional (`--monitoring`): Prometheus `http://127.0.0.1:9090` i Grafana `http://127.0.0.1:3000`
(usuari `admin`; contrasenya a `.dev\monitoring\grafana_admin_password`). El port de mètriques de l'app (9091)
**no es publica** i exigeix un token Bearer. Per a un Prometheus compartit, vegeu
[deploy/monitoring/prometheus.shared.example.yml](deploy/monitoring/prometheus.shared.example.yml).
Les mètriques són agregades: cap etiqueta conté usuaris, IP, sessions, ordres ni noms de fitxer.

## Seguretat bàsica

Origen WebSocket validat; proxys de confiança només per IP exacta; cookies `HttpOnly`/`Secure`; antiforgery;
capçaleres CSP i `X-Frame-Options`; contenidor sense privilegis (UID 1654, sistema de fitxers de només lectura,
`cap_drop: ALL`); secrets en fitxers `0400`; logs sense secrets ni contingut de terminal. Els logs són en **hora
local** (variable `TZ`). L'auditoria registra només metadades. **Activa `require_mfa` a producció: sense MFA, no hi ha protecció davant d'una
contrasenya robada.**

## Estat i limitacions

Provat en local: 76 proves .NET (inclou OpenSSH i tmux reals en Docker), 8 proves e2e de navegador amb Nginx i amb
Traefik, 4 proves d'operació, `dotnet format` i escaneig de dependències (NuGet i npm) sense troballes.
**No provat:** servidor Linux real, DNS/ACME, Traefik amb *certresolver* real, càrrega, alta disponibilitat (una sola
rèplica). El pipeline de release (`deploy/application.json`) continua bloquejat en `scaffold`; vegeu
[docs/RELEASES.md](docs/RELEASES.md), [docs/OPERATIONS.md](docs/OPERATIONS.md) i
[docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md). Alguns documents de `docs/` (p. ex. `APPLICATION.md`) encara
descriuen el contracte previ a la implementació.

## Llicència i marca

[GNU AGPL-3.0](LICENSE) íntegra. Cada versió desplegada mostra l'enllaç al codi font i a la llicència (peu de pàgina).
Crèdit fix: logo original de [TuteApps](assets/branding/TuteApps_Logo.png) a baix a la dreta, amb tooltip
**Fet per TuteApps**; la personalització de la instància no l'amaga.
