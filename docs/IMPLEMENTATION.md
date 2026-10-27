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
| 1. Esquelet .NET 10 / Blazor / MudBlazor | **implementada** | build Release 0 avisos |
| 2. Comptes i autorització | **implementada** (TOTP només documentat/preparat) | tests d'Auth: login, lockout, rate limit, rols, revocació, reinici de contrasenya |
| 3. SSH, PTY, tmux | **implementada** | tests contra OpenSSH+tmux real (Docker): PTY, resize, reattach, host key, aïllament, origen |
| 4. Credencials, SFTP, auditoria, porta-retalls | **implementada** | tests SFTP (traversal, symlinks, límits, overwrite) i «cap secret a BD/logs» |
| 5. UI i marca | **implementada** (català; castellà pendent) | e2e Playwright ample/estret, teclat, tooltip TuteApps |
| 6. Mètriques Prometheus/Grafana | **implementada**; dashboard no validat amb dades reals | tests de scrape, token, cardinalitat, absència de PII |
| 7. Defensa i qualitat | **parcial**: `dotnet format` net, escaneig NuGet/npm sense troballes; pendent llista formal de riscos | `dotnet list package --vulnerable`, `npm audit` |
| 8. Docker Desktop i e2e | **implementada** amb Nginx i Traefik; pendents: rollback del mateix `data_epoch`, host key alterada a l'e2e, perfil monitoring no verificat | e2e 7/7 (nginx i traefik); `tests/ops_test.py` 4/4 |
| 9. Desplegament Linux | **parcial**: Compose alineat amb l'app (validat amb `compose config`); pendents: `application.json` en `scaffold`, `tests/application-release.sh`, CI, `test_deployment.py` a Linux | — |
| 10. README i revisió final | **parcial**: README reescrit; pendents alinear `docs/APPLICATION.md` i guies d'operació | — |
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
