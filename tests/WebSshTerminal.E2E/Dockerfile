# Runner d'e2e: imatge de Playwright amb navegadors ja instal·lats (la versió ha de coincidir amb el paquet NuGet).
FROM mcr.microsoft.com/playwright/dotnet:v1.64.1-noble
WORKDIR /e2e
COPY WebSshTerminal.E2E.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet build --no-restore -c Release
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
CMD ["dotnet", "test", "--no-build", "-c", "Release", "--logger", "console;verbosity=normal"]
