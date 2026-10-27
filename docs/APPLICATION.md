# Contracte pendent d'implementar

Aquest document defineix el treball que falta perquè la infraestructura
pugui desplegar una aplicació real. No descriu funcionalitats ja existents.

## Arquitectura acordada

Navegador → HTTPS/WSS → Nginx **o** Traefik → ASP.NET Core 10 / Blazor i
MudBlazor → client SSH del backend → únic servidor Linux autoritzat.
xterm.js renderitza el terminal. Les claus privades no arriben al navegador.
tmux resideix al servidor SSH remot, per mantenir sessions en reconnectar.

Autenticació local multiusuari, autorització per sessió i mapatge explícit
entre usuari web, identitat Linux i credencial SSH. Cap accés anònim a SSH.
Una sola rèplica inicial; no es promet alta disponibilitat ni desplegament
sense interrupció. El contenidor web no necessita `sudo`, root, Docker socket,
mode privilegiat, xarxa del host ni muntar el sistema de fitxers del host.
Els permisos sudo són al servidor remot i s'han de restringir per identitat.

## Contracte de contenidor

- Projecte: `src/WebSshTerminal/WebSshTerminal.csproj`, executable
  `/app/WebSshTerminal`; UID/GID `1654:1654`, HTTP `0.0.0.0:8080`.
- `WebSshTerminal --healthcheck`: consultar readiness local, codi 0 només si
  està llest. No ha d'arrencar una segona instància ni modificar dades.
- `GET /health/ready`: HTTP 200 amb exactament
  `{"status":"ok","revision":"<commit de 40 caràcters>"}`. Sense detalls
  de sessions, usuaris o host SSH. Sense redirect ni dependència d'una sessió.
- Injectar la revisió de compilació a l'app i mantenir-la coherent amb
  `org.opencontainers.image.revision`. El Dockerfile passa `SourceRevisionId`
  a la compilació; l'app ha de recuperar aquesta revisió de l'assembly.
- Fitxers de dades, base de dades i claus ASP.NET Data Protection únicament
  sota `/var/lib/web-ssh-terminal`; temporals sota `/tmp`.
- Lectura explícita dels fitxers `WebSsh__*File` definits a Compose. **.NET no
  implementa automàticament aquesta convenció.** Fallar si falta un secret.
- `app_key`: 32 bytes aleatoris codificats en 64 caràcters hexadecimals.
  Clau mestra per protegir material sensible en repòs; mai registrar-la.
- `bootstrap_password`: contrasenya aleatòria d'alta inicial; consumir-la
  només amb una base buida, requerir canvi al primer accés i no recrear comptes
  en reiniciar. Implementar un canal local segur per obtenir-la i ús limitat.
- `ssh_credentials`: JSON amb mapa d'identitats/credencials, esquema pendent
  de concretar amb l'MVP; validar abans d'escoltar. No posar-ne valors reals a Git.
- `known_hosts`: claus públiques de host validades fora de banda. Rebutjar
  host desconegut o canvi de clau; mai `StrictHostKeyChecking=no`.
- `WebSsh__TrustedProxies`: llista d'IP exactes separades per comes; convertir-la
  explícitament en `KnownProxies`. Aplicar `UseForwardedHeaders` abans d'HTTPS,
  autenticació i generació d'URLs. No activar confiança global de headers.
- `AllowedHosts` i `WebSsh__PublicUrl`: restringir host/origen; cookies Secure,
  HttpOnly i política SameSite compatible; protecció CSRF i validació Origin
  de WebSocket. Autoritzar cada connexió i cada accés a sessions/fitxers.
- `WebSsh__SourceUrl`: enllaç visible al codi de la revisió executada.
- SIGTERM: tancar canals web de manera ordenada en menys de 30 s, persistir
  metadades; mantenir tmux remot només segons la política de sessions.

## Criteris abans d'una release exposada a Internet

Provar login/logout, bloqueig per intents, recuperació segura, caducitat,
revocació de sessions i MFA o una política de desplegament explícita per
aquesta exposició. No hi ha una contrasenya predeterminada universal.
Provar aïllament d'usuaris en terminal, tmux, auditoria i SFTP; límits de
sessions, mides de transferència i temps d'inactivitat; rutes de fitxers
autoritzades; restricció de destins SSH per evitar connexions arbitràries.

L'auditoria inclou actor, data UTC, servidor, identificador de sessió, tipus
d'esdeveniment i resultat. No enregistrar ordres, sortida de terminal,
contingut del porta-retalls, contrasenyes, cookies ni claus. Fixar retenció
i accés a l'auditoria abans de producció.

UI: logo TuteApps fix a baix a la dreta, tooltip `Fet per TuteApps`, accessible
amb ratolí i teclat. L'enllaç al codi i a la llicència ha de ser visible.
Colors, logo propi de la instància, nom i tema són configurables.

Completar `tests/application-release.sh` amb compilació, proves d'autenticació,
aïllament, hostkeys, WebSockets, còpies/restauració i smoke del contenidor
construït. No substituir-lo per un script que simplement retorni èxit.
Només llavors passar `status` a `ready`, marcar els criteris a
`deploy/application.json` i fixar imatges base i dependències per versió/digest.
Cada valor `true` representa evidència de proves revisada, no només intenció.

`data_epoch` identifica una família de versions amb dades compatibles en
ambdues direccions. El desplegament ordinari no admet canvis d'epoch ni
migracions destructives. Usar migracions additives provades amb la versió
anterior o preparar un procediment de manteniment i restauració separat.
