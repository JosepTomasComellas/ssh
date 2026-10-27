# Releases i publicació

El repositori públic és la font de codi i dels scripts. `main` rep canvis
revisats; producció només accepta tags explícits anotats `vMAJOR.MINOR.PATCH`
o prereleases `vMAJOR.MINOR.PATCH-rc.N`, amb commit complet i digest.
No moure tags publicats; qualsevol correcció requereix versió nova.

## Activar quan existeixi l'aplicació

1. Implementar el [contracte](APPLICATION.md), els tests d'acceptació i el
   consum de la revisió al projecte. Fixar versions NuGet/npm i
   imatges Microsoft per digest; incloure lockfiles. El Dockerfile assumeix
   que el projecte .NET integra els assets xterm.js; si cal Node, afegir la
   fase de compilació amb `npm ci` i el seu lockfile abans d'activar releases.
2. Validar `deploy/application.json`, incloent `status: ready`, tots els
   criteris i `data_epoch`. No canviar-lo per esquivar un error.
3. Completar `tests/application-release.sh` i provar el contenidor real amb
   tots dos proxys, les credencials muntades i la restauració de dades.
4. Protegir `main`, exigir CI i revisió; protegir `v*` contra esborrat i
   reescriptura. Aquests ajustos GitHub no s'han activat automàticament.
5. Crear preferentment un tag signat des d'un commit revisat:

```bash
git tag -s v1.0.0 COMMIT_REVISAT -m 'Web SSH Terminal v1.0.0'
git push origin v1.0.0
```

Si no hi ha infraestructura de signatura, `git tag -a` és compatible amb
la política inicial; cal contrastar el commit complet per un canal fiable.
Es pot activar `verify_signed_tags: true` al servidor i preparar un keyring
GPG/allowedSignersFile SSH que només contingui signants autoritzats.
El SHA i el digest fixen contingut; per si sols no acrediten l'autor.

## Workflow preparat

`release.yml` rebutja el scaffold, comprova el tag, executa les proves,
construeix la imatge amd64 i la publica a
`ghcr.io/joseptomascomellas/ssh:vX.Y.Z`. Usa `GITHUB_TOKEN` efímer amb els
permisos de continguts/paquets del job, sense PAT ni secrets de producció.
Les accions es fixen per SHA. Els permisos base de CI són de lectura.

Genera una **release draft** amb `release.json` (tag, commit, digest, epoch),
`source.tar.gz` del commit exacte i `SHA256SUMS`. Inclou provenance i SBOM
de BuildKit. Cal revisar i publicar el draft després de l'assaig en staging.
Fer públic el paquet GHCR quan es publiqui per primera vegada; un repositori
públic no garanteix que el paquet ja ho sigui. Els servidors així no necessiten
credencials de registre. No s'ha creat cap imatge/release en aquest canvi.

`gh release create` falla si la release existeix; no sobreescriu assets en
silenci. Si una execució parcial ja ha publicat imatge, revisar el digest
abans de reprendre. Protegir tags i restringir qui pot executar workflows.
La infraestructura inicial no verifica criptogràficament atestacions al
servidor; l'activació d'aquesta política és una millora futura diferenciada
de les comprovacions de digest/commit/label actuals.

## AGPL i font corresponent

Conservar `LICENSE` íntegre i publicar codi de l'app, scripts de construcció,
assets i lockfiles necessaris. Afegir a la UI un accés visible al codi de la
revisió desplegada i a la llicència. Publicar també les modificacions locals
que s'executin; l'enllaç a una versió upstream diferent no cobreix aquest cas.
El paquet de font de la release ha de permetre reconstruir aquella versió.
Les claus SSH, secrets, certificats i dades d'usuaris queden fora del codi públic.

TuteApps es manté segons el disseny de producte. La personalització de marca
no elimina el crèdit fix `Fet per TuteApps`; no s'afegeix una clàusula nova a
l'AGPL ni es canvia la llicència existent del repositori.
