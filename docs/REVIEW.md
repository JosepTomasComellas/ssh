# Revisió del repositori

Data: 27/10/2026. Font: https://github.com/JosepTomasComellas/ssh
Commit revisat: `ff94614373ebd28d87964297fa7bfab2d2b3c392` (`Initial commit`).
Repositori públic, branca principal `main`.

| Element | Resultat inicial |
|---|---|
| README | `# ssh` / `Consola web SSH` |
| Llicència | AGPL-3.0, text íntegre ja present |
| Codi d'aplicació | Inexistent |
| Dockerfile / Compose / scripts / CI | Inexistents |
| Tags i releases | Cap |
| Secrets al codi existent | No detectats als dos fitxers revisats |
| Logo | Disponible a `D:\Claude\TuteApps_Logo.png`; incorporat sense alterar |

La conversa referenciada s'ha pogut llegir pel que fa a les decisions de
l'usuari. Les respostes anteriors de l'assistent apareixen com a referències
sense contingut recuperable; no se n'han inventat requisits addicionals.

Decisions recuperades: .NET 10, Blazor, MudBlazor, xterm.js, Docker; SSH a un
servidor; multiusuari; autenticació web local; identitat Linux i claus
configurables; ús de sudo al servidor remot; verificació de claus de host;
persistència amb tmux; auditoria de metadades; terminal, sessions,
transferències, porta-retalls i redimensionament; exposició a Internet;
suport de tots dos proxys; branding personalitzable amb crèdit TuteApps fix.

## Abast del canvi

Es lliuren configuracions, scripts, proves, workflow de release bloquejat
fins a disposar d'app i documentació d'operació. No s'han implementat
autenticació, terminal, SFTP, tmux ni servidor SSH. El Dockerfile és el contracte
del futur projecte, no una imatge que avui es pugui construir.

No s'ha accedit a cap servidor Linux de producció ni s'ha escollit un domini.
No es dona per validat TLS/ACME real, l'autenticació de l'app ni una migració de
base de dades. El workflow limita les imatges inicials a amd64; arm64 requereix
publicació i proves pròpies.

## Fonts tècniques

- [Secrets a Docker Compose](https://docs.docker.com/compose/how-tos/use-secrets/): fitxers muntats, no contrasenyes en variables d'entorn.
- [Espera de salut a Compose](https://docs.docker.com/reference/cli/docker/compose/up/).
- [WebSockets a Nginx](https://nginx.org/en/docs/http/websocket.html).
- [Routing Docker a Traefik](https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/).
- [Proxys de confiança a ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

Els scripts guarden metadades de desplegament i eviten imprimir secrets.
Compose local utilitza muntatges de fitxer: no proporciona un magatzem de
secrets xifrat en repòs. Els permisos i el xifrat del disc continuen essent
responsabilitat del servidor.
