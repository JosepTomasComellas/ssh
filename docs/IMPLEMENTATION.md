# Estat d'implementació

Darrera actualització: 27/10/2026. Branca de treball `worktree-ssh-app`, sobre
la base de la PR #1 (`1bf541d`, `codex/linux-deployment-foundation`, integrada
per fast-forward sense canvis locals perduts).

Llegenda: **existent** = revisat i reutilitzat · **adaptat** · **implementat** ·
**pendent** = no fet · **pendent de servei real** = requereix DNS, ACME, host
Linux o servidor SSH reals.

## Fase 0 — Inventari

| Element | Estat |
|---|---|
| `LICENSE` AGPL-3.0 íntegra | existent |
| Scripts `scripts/manage.py`, `deploy.sh`, `rollback.sh` | existent (de la PR #1); pendents de provar amb l'app real |
| `deploy/` (Compose endurit, Nginx/Traefik, Dockerfile contractual) | existent; Dockerfile i Compose s'han d'adaptar a l'app (TZ, tzdata) |
| Workflows CI/release | existent; release bloquejada (`status: scaffold`) |
| Logo `assets/branding/TuteApps_Logo.png` | existent, original, no modificat |
| Proves de desplegament (15) | existent; no re-executades en aquesta fase |
| `.serena/`, `prompt/` | locals de l'usuari, no versionats |

## Fases

| Fase | Estat | Evidència |
|---|---|---|
| 1. Esquelet .NET 10 / Blazor / MudBlazor | **implementada** | `dotnet build -c Release` 0 avisos; 12 tests |
| 2. Comptes i autorització | pendent | — |
| 3. SSH, PTY, tmux | pendent | — |
| 4. Credencials, SFTP, auditoria, porta-retalls | pendent | — |
| 5. UI i marca (xterm.js, i18n) | parcial: crèdit TuteApps i enllaç a codi/llicència | proves visuals pendents |
| 6. Mètriques Prometheus/Grafana | pendent (`prometheus-net.AspNetCore` 8.2.1; l'exporter d'OpenTelemetry només és beta) | — |
| 7. Defensa i qualitat | pendent | — |
| 8. Docker Desktop i e2e | pendent | — |
| 9. Desplegament Linux | existent (PR #1), pendent d'integrar | — |
| 10. Revisió final i README | pendent | — |

## Decisions

- **Biblioteca SSH:** SSH.NET 2026.0.0 (llicència MIT, mantinguda). Pendent de
  verificar PTY/SFTP contra un servidor real a la Fase 3.
- **Base de dades:** SQLite amb EF Core 10 (una rèplica, còpia senzilla).
- **Mètriques:** `prometheus-net.AspNetCore` (estable) en lloc de l'exporter
  OpenTelemetry (només beta).
- **Logs en hora local** amb offset explícit (`yyyy-MM-ddTHH:mm:sszzz`), per
  petició de l'usuari. L'auditoria de seguretat és un canal a part i continua en UTC.
- **Revisió:** l'SDK incrusta el commit de Git a la versió; `BuildInfo.Revision`
  l'extreu (40 hex, zeros si no n'hi ha).
- Lockfiles NuGet activats (`RestorePackagesWithLockFile`); Dockerfile usa `--locked-mode`.

## Límits coneguts ara

Sense autenticació, la pàgina inicial no ofereix cap accés SSH. Res d'això és
«production ready».
