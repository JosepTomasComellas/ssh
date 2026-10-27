# syntax=docker/dockerfile:1
# SDK_IMAGE i RUNTIME_IMAGE s'han de fixar per digest a deploy/application.json abans d'una release.
# Els valors per defecte només serveixen per al desenvolupament local.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14

FROM ${SDK_IMAGE} AS build
ARG REVISION
WORKDIR /src
# El restore s'ha de fer amb les fonts completes: l'SDK afegeix dependències implícites (assets estàtics)
# segons el contingut de wwwroot, i el lockfile s'ha generat així.
COPY global.json Directory.Build.props ./
COPY src src
COPY assets assets
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/WebSshTerminal/WebSshTerminal.csproj --locked-mode
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/WebSshTerminal/WebSshTerminal.csproj -c Release --no-restore -o /out \
    /p:UseAppHost=true /p:SourceRevisionId=${REVISION}

FROM ${RUNTIME_IMAGE} AS final
ARG REVISION
LABEL org.opencontainers.image.source="https://github.com/JosepTomasComellas/ssh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.title="Web SSH Terminal"
WORKDIR /app
COPY --from=build /out .
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["/app/WebSshTerminal"]
