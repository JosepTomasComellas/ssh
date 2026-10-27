# Secrets, còpies i recuperació

## Emmagatzematge

```text
/opt/web-ssh-terminal/       root:root 0700
  config.json               root:root 0600, paràmetres no secrets
  secrets/                  root:root 0700
    app_key                 1654:1654 0400
    bootstrap_password      1654:1654 0400
    ssh_credentials         1654:1654 0400
    known_hosts             1654:1654 0400
  data/                     1654:1654 0700
  releases/<id>/            codi, digest i configuració de cada versió
  repository.git/           cache del repositori oficial
  backups/*.tar.age         còpies xifrades
  state.json                versió actual i anterior
  transaction.json          només durant transició/recuperació pendent
```

No posar `.env` amb secrets a Git, ni claus a Compose, labels, Dockerfile,
logs o arguments de procés. El Compose local munta fitxers: `uid/gid/mode`
d'un secret no remapeja permisos de fitxers bind. Per això es configuren i
validen al host, i el contenidor llegeix els fitxers amb UID 1654.
Root i l'administrador de Docker continuen tenint accés als secrets.

Xifrar el disc i les còpies; limitar comptes amb UID 1654 al host; evitar
logs de depuració a producció. La ignorància de Git és una defensa addicional,
no un detector de secrets. Revisar diffs i habilitar secret scanning/protecció
de push a GitHub quan estigui disponible per al repositori.

Rotació: preparar fitxer nou privat, validar amb l'app i recrear el servei;
un bind mount existent pot continuar veient l'inode antic després d'un rename.
El rollback de binari no torna enrere els secrets. Mantenir compatibilitat
durant la finestra de rollback. No regenerar `app_key` ni Data Protection
sense migració/rexifrat: es perdria accés a dades o sessions protegides.
Una exposició exigeix revocar la credencial a l'origen; eliminar-la de Git
no la invalida.

## Còpia consistent i xifrada

```bash
sudo python3 scripts/manage.py backup
```

Atura l'app, envia directament per pipe dades, secrets, configuració i estat
cap a `age`, i torna a aixecar la versió activa. No crea un tar en clar.
La clau privada de desxifrat queda fora del servidor. Una còpia fallida deixa
un `.partial` xifrat per diagnosticar-la; no es tracta com a còpia vàlida.
Copiar les còpies a un destí extern protegit i definir retenció i monitoratge
d'espai. El projecte no elimina còpies ni imatges de manera automàtica.

La còpia no inclou el servidor remot, les sessions tmux, els fitxers SFTP
remots ni els certificats/configuració de Nginx/Traefik. Aquests elements
necessiten la seva política de còpies. Per defecte tampoc inclou les imatges
Docker; conservar la release i la imatge per digest en un registre/backup
que cobreixi la finestra de recuperació.

## Rollback i interrupcions

```bash
sudo bash scripts/rollback.sh
sudo python3 scripts/manage.py status
```

Rollback a l'anterior release reeixida: reutilitza la imatge local i la
configuració guardada, fa còpia i valida salut. No descarrega una etiqueta
mòbil. Només s'admet el mateix `data_epoch`; tampoc restaura automàticament
una base de dades antiga. Una migració incompatible requereix manteniment,
restauració i acceptar explícitament la possible pèrdua de dades recents.

Hi ha un lock exclusiu per evitar actualitzacions/còpies concurrents. Si es
produeix SIGINT/SIGTERM es prova la recuperació. SIGKILL o apagada del host
pot deixar un journal: qualsevol nou desplegament quedarà bloquejat.

```bash
sudo python3 scripts/manage.py recover
```

Recupera la versió prèvia i comprova HTTPS abans d'esborrar el journal.
Si també falla, conserva el journal i retorna error: resoldre espai, xarxa,
certificats o imatge local i repetir. Una primera instal·lació fallida atura
el candidat i conserva les dades. `status` mostra l'estat **registrat**, no
és un monitor continu de salut del contenidor.

## Restauració d'una còpia

Fer un simulacre en un host aïllat abans de producció. Desxifrar a un
directori temporal privat d'aquest host, mai sobre una app en funcionament:

```bash
umask 077
mkdir restore-review
age --decrypt -i /suport-segur/identity.txt /copies/COPIA.tar.age \
  | tar -xpf - -C restore-review
```

Només restaurar còpies pròpies verificades; inspeccionar l'arxiu i les
metadades abans d'extreure'l amb privilegis. Revisar `state.json` per obtenir
tag, commit i digest; obtenir aquella release exacta i conservar el seu
`data_epoch`. Amb l'app aturada, restaurar `data`, `secrets` i configuració,
reaplicar propietaris i permisos, i reconstruir el desplegament des del tag
i digest amb els scripts revisats. En un host nou, guardar l'antic `state.json`
com a referència fora de l'arrel activa; no copiar punters a `releases/` que
no existeixen. Tornar a provar login, dades, hostkeys i sessions per usuari.

No s'automatitza una restauració destructiva sense conèixer la futura base
de dades. El procediment específic i les proves de restauració de l'app són
un requisit de release, no una propietat demostrada per aquest scaffold.
