# Desplegament Linux

## Prerequisits

Linux amd64, Python 3.10+, Git, Docker Engine i connector Compose >=2.20,
`age`, `tar`, espai per la imatge nova, l'anterior i còpies. Instal·lar Docker
des de la documentació oficial de la distribució, no amb `curl | sh`.
Operació amb root; l'app s'executa com UID/GID 1654. Només un desplegament
d'aquest projecte per host. Escollir un subnet Docker que no col·lideixi.

Cal DNS, ports 80/443 al proxy i egress cap al servidor SSH autoritzat.
No publicar 8080 ni obrir un SSH nou al contenidor web. Restringir accés
al grup Docker perquè equival a poder administrar el host.

## Inicialització

Després de revisar el codi en una còpia local del repositori:

```bash
sudo python3 scripts/manage.py init
sudoedit /opt/web-ssh-terminal/config.json
```

Crear la identitat `age` en un equip de recuperació segur (`age-keygen`).
Guardar la clau **privada fora del servidor** i posar únicament el destinatari
públic `age1...` a `backup_recipient`. El fitxer d'exemple es rebutja fins
que s'hagi substituït aquest valor.

`init` crea `app_key` i `bootstrap_password` únics, sense mostrar-los, i no
sobreescriu una instal·lació existent. Preparar els altres dos fitxers en una
ubicació segura; no usar arguments de línia d'ordres per passar secrets:

```bash
sudo install -o 1654 -g 1654 -m 0400 /ruta/segura/ssh_credentials /opt/web-ssh-terminal/secrets/ssh_credentials
sudo install -o 1654 -g 1654 -m 0400 /ruta/segura/known_hosts /opt/web-ssh-terminal/secrets/known_hosts
```

L'esquema de credencials es concretarà amb l'app. No emprar `{}` com si fos
una credencial funcional. Contrastar la fingerprint SSH amb l'administrador
per un canal fiable; `ssh-keyscan` sol no acredita la identitat del host.

## Opció Nginx al host

Configurar `proxy: nginx`, domini, port loopback (18080 per defecte) i la IP
de gateway del subnet backend a `trusted_proxies` (172.30.50.1 a l'exemple).
Comprovar la IP real que rep Kestrel al host concret; no confiar en tota una
xarxa per resoldre errors de forwarding.

Obtenir primer un certificat vàlid amb el client ACME del host; pot caldre
DNS-01 o una configuració HTTP temporal abans d'activar el virtual host TLS.
El template espera `/etc/letsencrypt/live/DOMINI/{fullchain,privkey}.pem`.

```bash
sudo python3 scripts/manage.py render-nginx | sudo tee /etc/nginx/conf.d/webssh.conf >/dev/null
sudo nginx -t
sudo systemctl reload nginx
```

Requereix Nginx >=1.25.1 pel `http2 on`. El fitxer s'inclou dins `http {}`.
Conservar el servidor per defecte que rebutgi hosts desconeguts i evitar
duplicats per al mateix domini. Configurar renovació ACME amb `nginx -t` i
reload posterior. El proxy conserva Upgrade/Connection, timeout de 3600 s,
sense cache ni buffering; l'app ha d'enviar heartbeat per detectar talls.
La publicació Docker és exclusivament `127.0.0.1:18080`.

## Opció Traefik existent

Configurar `proxy: traefik`, `proxy_network` amb la xarxa externa existent,
`traefik_resolver` amb el resolver ACME existent i `trusted_proxies` amb les
IP exactes de Traefik. Assignar IP estable al proxy dins aquesta xarxa.
La xarxa només ha de contenir serveis de confiança.

Traefik necessita provider Docker amb `exposedByDefault=false`, entrypoint
`websecure` a 443 i redirecció HTTP→HTTPS a `web`. La resolució ACME i el seu
emmagatzematge privat han d'estar configurats al desplegament del proxy.
El Compose de l'app **no instal·la ni reconfigura** el proxy compartit.

Les labels seleccionen la xarxa i port 8080; no publiquen cap port al host.
Traefik gestiona l'upgrade WebSocket. Configurar logs del proxy per ometre
queries, Authorization, cookies i dades de terminal. No afegir buffering ni
cache als canals de terminal. Protegir l'accés del provider al Docker API;
el contenidor de l'app mai munta `/var/run/docker.sock`.

## Instal·lar una release real

Avui aquest pas **fallarà expressament** perquè no existeix l'aplicació.
Quan hi hagi una release revisada, agafar `tag`, `commit` i `image` del seu
`release.json` i contrastar-los amb GitHub. Executar una versió revisada dels
scripts d'administració i conservar-la fora dels directoris gestionats.

```bash
sudo bash scripts/deploy.sh \
  --tag v1.0.0 \
  --commit COMMIT_COMPLET_DE_40_CARACTERS \
  --image ghcr.io/joseptomascomellas/ssh@sha256:DIGEST_DE_64_CARACTERS
```

Els placeholders es rebutgen. El script obté el tag del repositori públic
per HTTPS, exigeix tag anotat i commit coincident, comprova el contracte de
l'app, descarrega la imatge per digest i verifica la seva label de revisió.
No desplega `main` ni `latest`, no construeix amb secrets al servidor i no
modifica els tags. La comprovació de label evita errors accidentals; no
substitueix signatures/atestacions de la cadena de publicació.

En actualitzar, atura la versió activa, fa una còpia xifrada, inicia la nova
i comprova salut interna i HTTPS públic amb la revisió esperada. Només llavors
desa `current` i `previous`. Això implica una interrupció breu de la web i
reconnexió de les sessions. No declara un desplegament sense interrupcions.

La consulta HTTPS necessita que el host pugui resoldre i accedir al domini
públic. No desactiva la comprovació del certificat. Si aquesta prova falla,
intenta recuperar la versió anterior i retorna error.

```bash
sudo python3 scripts/manage.py status
sudo bash scripts/rollback.sh
```

Els proxys i el domini es fixen per instal·lació: canviar-los en calent es
rebutja. Els canvis de configuració interna s'han de provar igual que una
release. El rollback recupera les configuracions Compose guardades amb cada
versió. Les versions velles d'imatges i codi no es netegen automàticament.
