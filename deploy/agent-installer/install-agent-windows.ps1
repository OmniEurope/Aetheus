# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
<#
.SYNOPSIS
    Aetheus Agent - Windows Installation Script

.DESCRIPTION
    Run from inside the extracted agent directory (e.g. C:\Program Files\AetheusAgent).
    Prompts for server URL, registration token and optional agent name,
    writes appsettings.json, registers a Windows service and starts it.
    Self-elevates to Administrator if needed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-agent-windows.ps1
    powershell -ExecutionPolicy Bypass -File .\install-agent-windows.ps1 -ServerUrl "https://example.com" -Token "TOKEN"
#>
param(
    [string]$ServerUrl,
    [string]$Token,
    [string]$Name,
    [string]$InstallDir = "C:\Program Files\AetheusAgent",
    [string]$WorkDir = "C:\ProgramData\AetheusAgent",
    [string]$ServiceName = "AetheusAgent",
    # DEV ONLY: skip TLS certificate validation (backend API + pipeline git clones).
    # Auto-enabled when the server URL is localhost. Never use in production.
    [switch]$AllowInsecureCerts,
    # Skip the pipeline-runner host-tool install (Git via winget/choco).
    # Application SDKs are provided by locked OCI images.
    [switch]$NoPipelineRunner,
    # Suppress acknowledgement prompts for automated/local smoke installations.
    [switch]$NonInteractive,
    # Internal test hook: validate path safety before elevation or filesystem mutation.
    [switch]$ValidatePathsOnly,
    # Internal (self-elevation): path of a transient file holding the registration
    # token, so the token never appears on the elevated process's command line.
    [string]$TokenFile
)

$ErrorActionPreference = "Stop"

function Resolve-AgentDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$AllowProgramFiles
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Label must be an absolute path."
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ([string]::Equals($fullPath, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must not be a volume root."
    }

    $forbiddenRoots = @($env:WINDIR)
    if (-not $AllowProgramFiles) {
        $forbiddenRoots += @($env:ProgramFiles, ${env:ProgramFiles(x86)})
    }
    foreach ($forbidden in $forbiddenRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        $canonicalForbidden = [System.IO.Path]::GetFullPath($forbidden).TrimEnd('\', '/')
        if ([string]::Equals($fullPath, $canonicalForbidden, [System.StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith($canonicalForbidden + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label must not be inside the protected directory '$canonicalForbidden'."
        }
    }

    $cursor = $fullPath
    while (-not (Test-Path -LiteralPath $cursor)) {
        $parent = [System.IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    while (Test-Path -LiteralPath $cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label must not traverse a symbolic link, junction or reparse point ('$cursor')."
        }
        $parent = [System.IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }

    return $fullPath
}

function Test-AgentPathContains {
    param([string]$Parent, [string]$Child)
    return [string]::Equals($Parent, $Child, [System.StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent.TrimEnd('\', '/') + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

$InstallDir = Resolve-AgentDirectory -Path $InstallDir -Label "InstallDir" -AllowProgramFiles
$WorkDir = Resolve-AgentDirectory -Path $WorkDir -Label "WorkDir"
if ((Test-AgentPathContains -Parent $InstallDir -Child $WorkDir) -or
    (Test-AgentPathContains -Parent $WorkDir -Child $InstallDir)) {
    throw "InstallDir and WorkDir must not overlap."
}
if ($ValidatePathsOnly) {
    Write-Output "PATHS_VALID"
    exit 0
}

# --- Token handover from the unelevated instance (kept off the command line) ---
if (-not $Token -and $TokenFile -and (Test-Path $TokenFile)) {
    $Token = (Get-Content -Path $TokenFile -Raw).Trim()
    Remove-Item -Path $TokenFile -Force -ErrorAction SilentlyContinue
}

# --- Self-elevation ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[INFO]  Not running as Administrator. Requesting elevation..." -ForegroundColor Yellow
    $argList = "-ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($ServerUrl) { $argList += " -ServerUrl `"$ServerUrl`"" }
    $handoverFile = $null
    if ($Token) {
        # Never put the token on the elevated argv (process command lines are
        # visible to other local users): hand it over via a transient file.
        $handoverFile = Join-Path $env:TEMP ("aetheus-agent-token-" + [guid]::NewGuid().ToString("N") + ".tmp")
        Set-Content -Path $handoverFile -Value $Token -NoNewline -Encoding UTF8
        $argList += " -TokenFile `"$handoverFile`""
    }
    if ($Name)      { $argList += " -Name `"$Name`"" }
    if ($InstallDir) { $argList += " -InstallDir `"$InstallDir`"" }
    if ($WorkDir)    { $argList += " -WorkDir `"$WorkDir`"" }
    if ($ServiceName) { $argList += " -ServiceName `"$ServiceName`"" }
    if ($AllowInsecureCerts) { $argList += " -AllowInsecureCerts" }
    if ($NoPipelineRunner)   { $argList += " -NoPipelineRunner" }
    if ($NonInteractive)     { $argList += " -NonInteractive" }
    try {
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
        # Start-Process does not populate $LASTEXITCODE: without -PassThru a
        # failed elevated install would be reported as success (exit 0).
        exit $proc.ExitCode
    } finally {
        if ($handoverFile) { Remove-Item -Path $handoverFile -Force -ErrorAction SilentlyContinue }
    }
}

function Write-Info  { param([string]$m) Write-Host "[INFO]  $m"  -ForegroundColor Green }
function Write-Warn  { param([string]$m) Write-Host "[WARN]  $m"  -ForegroundColor Yellow }
function Write-Err   { param([string]$m) Write-Host "[ERROR] $m"  -ForegroundColor Red }

# --- TLS 1.2 (PS5 needs this explicitly) ---
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- Prompts ---
if (-not $ServerUrl) { $ServerUrl = Read-Host "Aetheus server URL (e.g. https://aetheus.example.com)" }
if (-not $ServerUrl) { Write-Err "Server URL is required."; exit 1 }
$ServerUrl = $ServerUrl.TrimEnd('/')

# --- Skip certificate validation for localhost or explicit dev opt-in (self-signed dev certs) ---
$uri = [System.Uri]::new($ServerUrl)
if ($AllowInsecureCerts -or $uri.Host -in @("localhost", "127.0.0.1", "::1")) {
    try {
        Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class SSLValidator {
    public static void Ignore() {
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
    }
}
"@
        [SSLValidator]::Ignore()
        Write-Info "TLS certificate validation bypassed (localhost detected)"
    } catch {
        Write-Warn "Could not disable TLS validation ($($_.Exception.Message)); continuing with validation active - downloads from a self-signed backend may fail."
    }
}

if (-not $Token) {
    $secureToken = Read-Host "Registration token (input hidden)" -AsSecureString
    $Token = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken))
}
if (-not $Token) { Write-Err "Registration token is required."; exit 1 }

# Show masked token for confirmation
if ($Token.Length -gt 10) {
    $first5 = $Token.Substring(0, 5)
    $last5  = $Token.Substring($Token.Length - 5)
    Write-Info "Token received: $first5...$last5 ($($Token.Length) chars)"
} else {
    Write-Info "Token received: $($Token.Length) chars"
}

# Default agent name = hostname (no prompt)
if (-not $Name) { $Name = [System.Net.Dns]::GetHostName() }
Write-Info "Agent name: $Name"

# --- Locate binaries ---
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exeSource = Join-Path $scriptDir "Aetheus.Agent.Windows.exe"
if (-not (Test-Path $exeSource)) {
    Write-Err "Aetheus.Agent.Windows.exe not found in $scriptDir. Extract the agent archive first."
    exit 1
}

# --- Stop and remove existing service BEFORE copying files (exe is locked while running) ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service $ServiceName already exists. Stopping and removing..."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# --- Clean stale enrollment state so the agent re-enrolls with the new token ---
foreach ($staleFile in @(".credentials", ".enrollment-state")) {
    $stalePath = Join-Path $workDir $staleFile
    if (Test-Path $stalePath) {
        Write-Info "Removing stale $staleFile from previous installation..."
        Remove-Item -Path $stalePath -Force
    }
}

# --- Copy to InstallDir ---
if ($scriptDir -ne $InstallDir) {
    Write-Info "Copying agent files to $InstallDir..."
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $scriptDir "*") -Destination $InstallDir -Recurse -Force
}

$exePath = Join-Path $InstallDir "Aetheus.Agent.Windows.exe"

# --- Configuration ---
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

# --- Install timestamp marker ---
$installedAtPath = Join-Path $workDir ".installed-at"
[System.IO.File]::WriteAllText($installedAtPath, [DateTime]::UtcNow.ToString("o"))
Write-Info "Recorded install timestamp at $installedAtPath"

$configPath = Join-Path $InstallDir "appsettings.json"
# PS5-compatible ordered hashtable
$aetheus = New-Object System.Collections.Specialized.OrderedDictionary
$aetheus.Add("ServerUrl", $ServerUrl)
$aetheus.Add("RegistrationToken", $Token)
$aetheus.Add("Name", $Name)
$aetheus.Add("PollingIntervalSeconds", 10)
$aetheus.Add("HeartbeatIntervalSeconds", 30)
$aetheus.Add("MaxConcurrentTasks", 2)
$aetheus.Add("WorkDirectory", $workDir)
$aetheus.Add("LogRetentionDays", 30)
$aetheus.Add("DockerStorageMaintenance", [ordered]@{
    PolicyVersion = 3
    Enabled = $true
    DryRun = $false
    DeploymentOnly = $false
    AllowBuildsOnDeploymentTarget = $false
    MaintenanceIntervalMinutes = 60
    MaxCacheAgeHours = 168
    PressureCacheAgeHours = 24
    ReservedSpaceGiB = 5
    MaxCacheGiB = 15
    MinFreeSpaceGiB = 20
    PressureUsedPercent = 80
    NuGetCacheRetentionDays = 30
})

# Auto-enable insecure certs for localhost (self-signed dev cert) or explicit -AllowInsecureCerts.
# Also covers pipeline git clones (the agent sets GIT_SSL_NO_VERIFY when this is on).
$uri = [System.Uri]::new($ServerUrl)
if ($AllowInsecureCerts -or $uri.Host -in @("localhost", "127.0.0.1", "::1")) {
    $aetheus.Add("AllowInsecureCerts", $true)
    Write-Warn "AllowInsecureCerts enabled (DEV mode): TLS validation bypassed for backend API and pipeline git clones"
}

$config = New-Object System.Collections.Specialized.OrderedDictionary
$config.Add("Aetheus", $aetheus)
$config.Add("Logging", @{ LogLevel = @{ Default = "Information" } })

Write-Info "Writing configuration to $configPath..."
$config | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding UTF8

# --- Pipeline-runner module: install the host Git dependency ---
# winget first (built into Windows 10/11), choco as fallback. Best-effort: a failure
# is a warning, not fatal.
function Install-WithPackageManager {
    param([string]$Command, [string]$WingetId, [string]$ChocoId, [string]$Label)
    if (Get-Command $Command -ErrorAction SilentlyContinue) {
        Write-Info "  $Label already present."
        return
    }
    Write-Info "  Installing $Label (pipeline-runner module)..."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id $WingetId --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>$null
        if (Get-Command $Command -ErrorAction SilentlyContinue) { Write-Info "  $Label installed via winget."; return }
    }
    if (Get-Command choco -ErrorAction SilentlyContinue) {
        choco install $ChocoId -y 2>$null
        if (Get-Command $Command -ErrorAction SilentlyContinue) { Write-Info "  $Label installed via choco."; return }
    }
    Write-Warn "  Could not install $Label automatically - install it manually if your pipelines need it."
}

if (-not $NoPipelineRunner) {
    Write-Info "Pipeline-runner module: ensuring build/clone toolchain..."
    Install-WithPackageManager -Command "git"    -WingetId "Git.Git"               -ChocoId "git"        -Label "git"
    # Refresh PATH so a freshly-installed Git is visible to this session.
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path", "User")
}

# --- Faithful backend probe (parity with install-agent-linux.sh) ---
# Runs the agent binary in --probe-backend mode: one-shot HTTP GET /health/live using
# the same .NET HTTP stack the service will use, so TLS/DNS/proxy issues invisible to
# Invoke-WebRequest surface NOW instead of inside a service start-timeout. The probe
# runs from the install dir so it reads the exact appsettings.json the service will.
Write-Info "Probing backend with the agent runtime (TLS/DNS faithfulness check)..."
Push-Location $InstallDir
try {
    & $exePath --probe-backend 2>&1 | ForEach-Object { Write-Host "  $_" }
    $probeRc = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($probeRc -ne 0) {
    Write-Err "The agent runtime cannot reach $ServerUrl (probe exit $probeRc). Fix connectivity/TLS and re-run the installer."
    if (-not $NonInteractive) { Read-Host "Press Enter to exit" }
    exit 1
}

# --- Register service ---
Write-Info "Registering Windows service $ServiceName..."
$serviceIdentity = "NT SERVICE\$ServiceName"
& sc.exe create $ServiceName "binPath= $exePath" "start= auto" "obj= $serviceIdentity" "password= " "DisplayName= Aetheus Agent" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Err "Could not create $ServiceName with the restricted virtual service account $serviceIdentity."
    exit 1
}
# The detached self-updater runs as the same restricted virtual service account and must
# atomically replace and, on failure, restore files in this one directory. Modify is scoped
# to the agent installation only; Program Files inheritance and SYSTEM/Administrators ACLs remain.
& icacls.exe $InstallDir /grant:r "${serviceIdentity}:(OI)(CI)M" /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "Could not grant self-update access to $serviceIdentity on $InstallDir."; exit 1 }
& icacls.exe $workDir /grant:r "${serviceIdentity}:(OI)(CI)M" /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "Could not grant modify access to $serviceIdentity on $workDir."; exit 1 }
sc.exe description $ServiceName "Aetheus Infrastructure Agent" | Out-Null
sc.exe failure $ServiceName reset= 60 actions= restart/10000/restart/10000/restart/10000 | Out-Null

Write-Info "Starting service..."
Start-Service -Name $ServiceName

Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
if ($svc.Status -ne 'Running') {
    Write-Err "Service failed to start. Check Windows Event Log."
    if (-not $NonInteractive) { Read-Host "Press Enter to exit" }
    exit 1
}

Write-Info "Aetheus Agent is running!"
Write-Info ""

# --- Verify connection to backend ---
Write-Info "Verifying connection to $ServerUrl..."
$connected = $false
for ($i = 1; $i -le 10; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "$ServerUrl/health/live" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($r.StatusCode -eq 200) {
            $connected = $true
            break
        }
    } catch { }
    Write-Info "  Attempt $i/10 - waiting..."
    Start-Sleep -Seconds 3
}

if ($connected) {
    Write-Info "Connection to backend verified!"
} else {
    Write-Err "Could not reach $ServerUrl/health/live. Installation cannot be validated."
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    exit 1
}

# --- Verify agent registration ---
Write-Info "Waiting for agent registration..."
$registered = $false
for ($i = 1; $i -le 15; $i++) {
    try {
        $logPath = Join-Path $workDir "logs"
        if (Test-Path $logPath) {
            $recentLogs = Get-ChildItem $logPath -Filter "*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($recentLogs) {
                $content = Get-Content $recentLogs.FullName -Tail 20 -ErrorAction SilentlyContinue
                $logText = $content -join "`n"
                if ($logText -match "registered|Heartbeat sent|Connected to server") {
                    $registered = $true
                    break
                }
                if ($logText -match "Enrollment.*failed|token.*invalid|token.*expired") {
                    Write-Err "Enrollment failed - check that the registration token is valid and not expired."
                    break
                }
            }
        }
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Running') {
            Write-Err "Service stopped unexpectedly."
            break
        }
    } catch { }
    Write-Info "  Waiting for registration... ($i/15)"
    Start-Sleep -Seconds 2
}

if ($registered) {
    Write-Info ""
    Write-Info "==========================================="
    Write-Info "  Agent '$Name' registered successfully!"
    Write-Info "==========================================="
} else {
    Write-Err "Agent enrollment could not be proven from logs. Installation failed closed."
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    exit 1
}

Write-Info ""
Write-Info "  Status : Get-Service $ServiceName"
Write-Info "  Stop   : Stop-Service $ServiceName"
Write-Info "  Logs   : $workDir\logs"
Write-Info ""
if (-not $NonInteractive) { Read-Host "Press Enter to exit" }
