# Web SSH Terminal · TuteApps

**Estat: infraestructura preparada; aplicació encara pendent d'implementar.**

La revisió inicial de `main` (`ff94614373ebd28d87964297fa7bfab2d2b3c392`,
27 d'octubre de 2026) només va trobar aquest README i `LICENSE`.
No hi havia codi .NET, imatges de contenidor, tags ni releases.
Aquest canvi prepara el desplegament i conserva les decisions del projecte;
no presenta un terminal fictici com una aplicació funcional.

- [Guia de desplegament Linux](docs/DEPLOYMENT.md): Docker Compose, Nginx o Traefik.
- [Releases i tags](docs/RELEASES.md): imatges per digest, codi corresponent i publicació.
- [Requisits i contracte de l'aplicació](docs/APPLICATION.md): .NET 10, Blazor, MudBlazor, xterm.js.
- [Secrets, còpies i recuperació](docs/OPERATIONS.md): rollback i recuperació després d'una interrupció.
- [Informe de revisió](docs/REVIEW.md).

## Inici

El servidor necessita Linux, Python 3.10+, Git, Docker Engine amb Compose
2.20 o superior, `age` i `tar`. La versió inicial suporta Linux **amd64**.
Traefik o Nginx i el domini/certificat es preparen abans de desplegar.

```bash
git clone https://github.com/JosepTomasComellas/ssh.git
cd ssh
# Revisar el codi abans d'executar-lo com a administrador.
sudo python3 scripts/manage.py init
```

`init` només crea directoris i secrets locals. **No arrenca cap servei.**
El desplegament queda bloquejat mentre `deploy/application.json` indiqui
`scaffold`, falti el projecte o no s'hagin completat les comprovacions de l'app.
No s'ha creat cap tag ni release fictícia.

## Llicència i marca

Es conserva íntegra la llicència [GNU AGPL-3.0](LICENSE); els fitxers nous del
projecte s'ofereixen sota `AGPL-3.0-only`. Les futures versions han d'oferir
accés visible al codi corresponent que s'està executant.

Marca de producte fixa: logo original [TuteApps_Logo.png](assets/branding/TuteApps_Logo.png)
a la cantonada inferior dreta, petit, amb tooltip **Fet per TuteApps**.
La personalització de colors, nom, tema i logo de la instància no l'amaga.
La marca és un requisit de producte; no es modifica el text de l'AGPL ni se li
afegeix una restricció de redistribució.

## Verificació de la infraestructura

```bash
shellcheck scripts/*.sh
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 tests/compose_check.py
python3 tests/proxy_smoke.py
```

Les proves de desplegament simulen Docker i el servei web, i executen Git i
el xifrat amb `age` de veritat. La prova Nginx usa un backend de prova aïllat
per comprovar TLS, redirecció, headers i WebSocket. No valida cap implementació
SSH inexistent. Els contenidors de prova no publiquen ports al host i s'eliminen
en acabar; necessiten accés a les imatges de prova de Docker Hub.
