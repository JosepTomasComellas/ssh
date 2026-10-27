#!/bin/sh
# Genera una clau per usuari i la instal·la com a authorized_keys. Les privades es llegeixen
# amb "docker exec" des de la prova i viuen només mentre viu el contenidor.
set -eu
mkdir -p /keys
for u in alice bob; do
  ssh-keygen -q -t ed25519 -N '' -f "/keys/$u" -C "fixture-$u"
  install -d -m 700 -o "$u" -g "$u" "/home/$u/.ssh"
  install -m 600 -o "$u" -g "$u" "/keys/$u.pub" "/home/$u/.ssh/authorized_keys"
done
chmod 644 /keys/*
exec /usr/sbin/sshd -D -e
