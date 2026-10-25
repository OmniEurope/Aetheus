# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
<#
.SYNOPSIS
    Local Docker deployment for Aetheus.
.PARAMETER Rebuild
    Force rebuild without cache.
.PARAMETER Down
    Stop containers (volumes and DB data are preserved).
.PARAMETER Wipe
    DESTRUCTIVE: stop containers AND delete named volumes (local DB data).
    Asks for interactive confirmation.
#>
param(
    [switch]$Rebuild,
    [switch]$Down,
    [switch]$Wipe
)

$ErrorActionPreference = "Stop"

$appName   = "aetheus"
$portFront = 10001
$portBack  = 10002
$dbUser    = "dbaetheus"
$dbPass    = "localdev123"
$jwtKey    = "local-dev-jwt-key-minimum-32-characters-long-for-hmac"
$compose   = Join-Path $PSScriptRoot "..\compose\local.compose.yml"
$context   = Join-Path $PSScriptRoot "..\..\.."

$env:APPNAME     = $appName
$env:PORT_FRONT  = $portFront
$env:PORT_BACK   = $portBack
$env:DB_USER     = $dbUser
$env:DB_PASSWORD = $dbPass
$env:JWT_KEY     = $jwtKey
$env:APP_VERSION = "local"

# $ErrorActionPreference does not stop on a native command's failure: every Docker call goes through
# here, which stops the script on a non-zero exit code, so success is reported only once every call
# succeeded (audit DEP-023).
function Invoke-Compose {
    docker compose -f $compose -p "${appName}-local" @args
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if ($Wipe) {
    Write-Host "This DELETES the '${appName}-local' named volumes (local DB data)." -ForegroundColor Red
    $answer = Read-Host "Type 'wipe' to confirm"
    if ($answer -ne 'wipe') {
        Write-Host "Aborted - nothing was removed." -ForegroundColor Yellow
        exit 1
    }
    Invoke-Compose down -v
    Write-Host "Containers and volumes removed." -ForegroundColor Green
    exit 0
}

if ($Down) {
    Write-Host "Stopping containers (volumes preserved)..." -ForegroundColor Yellow
    Invoke-Compose down
    Write-Host "Containers stopped." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Aetheus - Local Docker" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Front: http://localhost:$portFront"
Write-Host "  Back:  http://localhost:$portBack"
Write-Host ""

$env:APPNAME     = $appName
$env:PORT_FRONT  = $portFront
$env:PORT_BACK   = $portBack
$env:DB_USER     = $dbUser
$env:DB_PASSWORD = $dbPass
$env:JWT_KEY     = $jwtKey
$env:APP_VERSION = "local"

# Both Dockerfiles copy a host publish instead of compiling, so it has to exist before any build.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$env:DOTNET = "dotnet"
Push-Location $repoRoot
try {
    & sh "scripts/publish-application.sh" ".pipeline-publish"
    if ($LASTEXITCODE -ne 0) { throw "The host publish failed; the images would contain no application." }
}
finally {
    Pop-Location
}

if ($Rebuild) {
    Invoke-Compose build --no-cache
}
Invoke-Compose up -d --build

Write-Host ""
Write-Host "Containers started." -ForegroundColor Green
Write-Host "  Front: http://localhost:$portFront"
Write-Host "  Back:  http://localhost:$portBack"
