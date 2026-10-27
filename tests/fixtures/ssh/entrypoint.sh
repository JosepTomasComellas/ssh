#!/bin/sh
# Genera (una sola vegada) una clau per usuari i la instal·la com a authorized_keys. Les privades viuen
# a /keys (un volum de desenvolupament) i es llegeixen amb "docker exec"/volum només lectura a les proves.
set -eu
mkdir -p /keys
for u in alice bob; do
  [ -f "/keys/$u" ] || ssh-keygen -q -t ed25519 -N '' -f "/keys/$u" -C "fixture-$u"
  install -d -m 700 -o "$u" -g "$u" "/home/$u/.ssh"
  install -m 600 -o "$u" -g "$u" "/keys/$u.pub" "/home/$u/.ssh/authorized_keys"
done
chmod 644 /keys/*
exec /usr/sbin/sshd -D -e
