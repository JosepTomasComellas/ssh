# Servidor SSH EFÍMER només per a proves: dos comptes sense sudo, tmux i SFTP.
# Les claus es generen en arrencar el contenidor; res d'això s'ha de reutilitzar en producció.
FROM alpine:3.22
RUN apk add --no-cache openssh-server openssh-sftp-server tmux bash \
 && adduser -D -s /bin/bash alice \
 && adduser -D -s /bin/bash bob \
 && echo 'alice:*' | chpasswd -e \
 && echo 'bob:*' | chpasswd -e \
 && ssh-keygen -A
COPY sshd_config /etc/ssh/sshd_config
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 22
ENTRYPOINT ["/entrypoint.sh"]
