# Copia xterm.js (versions fixades a tools/xterm/package-lock.json) a wwwroot/lib/xterm.
# Així la imatge de Docker no necessita Node. Executar-lo només en actualitzar xterm.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $root 'tools/xterm')
try {
    npm ci --ignore-scripts
    $dest = Join-Path $root 'src/WebSshTerminal/wwwroot/lib/xterm'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item node_modules/@xterm/xterm/lib/xterm.js $dest -Force
    Copy-Item node_modules/@xterm/xterm/css/xterm.css $dest -Force
    Copy-Item node_modules/@xterm/addon-fit/lib/addon-fit.js $dest -Force
    Copy-Item node_modules/@xterm/xterm/LICENSE (Join-Path $dest 'LICENSE-xterm.txt') -Force
    Get-ChildItem $dest | Select-Object Name, Length
} finally {
    Pop-Location
}
