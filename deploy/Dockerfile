# syntax=docker/dockerfile:1
# These arguments MUST be digest-pinned in deploy/application.json before release.
ARG SDK_IMAGE
ARG RUNTIME_IMAGE
FROM ${SDK_IMAGE} AS build
ARG REVISION
WORKDIR /src
COPY . .
RUN dotnet restore src/WebSshTerminal/WebSshTerminal.csproj --locked-mode
RUN dotnet publish src/WebSshTerminal/WebSshTerminal.csproj -c Release --no-restore -o /out /p:UseAppHost=true /p:SourceRevisionId=${REVISION}

FROM ${RUNTIME_IMAGE} AS final
ARG REVISION
LABEL org.opencontainers.image.source="https://github.com/JosepTomasComellas/ssh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
WORKDIR /app
COPY --from=build /out .
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["/app/WebSshTerminal"]
