#!/usr/bin/env pwsh
# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
<#
.SYNOPSIS
    Build and launch Aetheus backend + frontend + agent, then open browser.
.DESCRIPTION
    - Kills any existing Aetheus processes (dotnet serve on ports 5301/5401)
    - Builds the entire solution
    - Ensures the local PostgreSQL dev database container is running
    - Optionally resets the local PostgreSQL database (-r) by recreating the docker volume
    - Optionally runs tests with granular control (-t unit, -ti integration, -te E2E, -ta all)
    - Optionally generates an HTML coverage report, then runs the CRAP gate (-c)
    - Mode flags: -mf (Back+Front, default), -ma (Agent), combinable
    - Starts selected services in parallel
    - Opens the browser to https://localhost:5401 (when -mf)
.PARAMETER Reset
    Drop and recreate the local PostgreSQL dev volume so the seed data runs again on a
    clean schema. Reset ONLY happens with an explicit -r - no test flag ever resets the dev DB.
.PARAMETER TestUnit
    Run ALL unit tests: back + front + agent-core + analyzers + observability packages. No database needed.
.PARAMETER TestAll
    Run EVERYTHING: unit + integration + E2E. The dev DB (:15432) is never reset
    (E2E uses its own isolated dedicated DB :15433).
.PARAMETER TestBack
    Run backend unit tests only.
.PARAMETER TestFront
    Run frontend unit tests only.
.PARAMETER TestAgent
    Run the agent-core unit tests only, in a Linux container of the pinned .NET SDK image (as the CI runs them),
    never on the Windows host. Fails when Docker cannot run it; there is no host fallback.
.PARAMETER TestIntegration
    Run integration tests only (Aetheus.Back.IntegrationTests on Testcontainers
    PostgreSQL - its own throwaway containers; the dev DB is never touched).
.PARAMETER TestE2e
    Run E2E tests (starts servers, runs Playwright, stops servers). Uses an ISOLATED,
    dedicated E2E database (aetheus-e2e-database :15433) seeded with demo data - the
    shared dev DB (:15432) is NEVER reset or touched.
    Use alone (-te) to run all categories.
    Use -tec to target specific categories.
.PARAMETER TestE2eFilter
    E2E category filter; implies -te. Examples:
      -tec ?           show interactive category picker
      -tec 1           run category 1
      -tec 1,2,3       run categories by number
      -tec Auth        run named category
      -tec Auth,Acts   run multiple categories by name
    Numbers and names can be mixed: -tec Auth,3,Smoke
.PARAMETER Coverage
    Collect code coverage and open the HTML report. Implies -tb and -tf, and also runs the
    Agent.Core unit suite so the aggregated % covers all three xUnit+bUnit suites (Back+Front+Agent.Core).
    Once the coverage is green, runs the CRAP gate (scripts/crap-gate.ps1) on every report of the run;
    -c fails when the gate fails.
.PARAMETER ModeFront
    Start Backend + Frontend (default mode if neither -mf nor -ma is specified).
.PARAMETER ModeAgent
    Start the Agent Worker Service. Can be combined with -mf to start all three.
.PARAMETER Silent
    Start servers without opening browser. Useful for running servers headless.
.PARAMETER HotReload
    Start servers with dotnet watch for hot reload (always restart on rude edits).
.PARAMETER RemoteAgent
    Open an SSH reverse tunnel so a remote Linux agent can reach the local
    backend via https://localhost:5301.  The SSH target (user@host or
    user@host:port for non-standard SSH port) is read from .remoteagent;
    if the file does not exist, an interactive prompt asks for the target
    and saves it.  Implies -mf.
    The tunnel is torn down on Ctrl+C with the servers.
.PARAMETER DumpDb
    Take a pg_dump snapshot of the local dev database WITHOUT resetting it,
    saved to TestResults\DbSnapshots\. Exits after the dump. Same snapshot the
    -Reset path takes automatically before dropping the volume.
.PARAMETER GitPush
    Stage all changes, commit using .gitmessage as commit message, and push.
    Exits after the push. Fails if .gitmessage does not exist.
.PARAMETER Worktree
    Give THIS worktree its own ports so it coexists with another worktree's running
    stack. ALWAYS assigns a fresh, non-default triplet (back-https / back-http / front),
    scanned from 5302/5303/5402 upward, skipping any bound port, any port the existing
    .ylaunch.local already used, and never the 5301/5300/5401 defaults. The file is
    written/renewed each run; an existing LABEL is preserved (else it defaults to the
    worktree folder name) and surfaces in the front dev top-bar banner; WORKTREE is always
    (re)written to the worktree folder name (its stable identity, logged on startup). Does
    NOT exit - the build + launch then run on the assigned ports. The dev Postgres (15432)
    stays shared.
.PARAMETER VpsSim
    Build and start the disposable local "VPS simulator" container and exit. By
    DEFAULT this is the BLANK box: a bare Ubuntu (systemd + sshd, nothing else)
    you install onto progressively. Add -full for the fully-provisioned box
    (Docker + apache/certbot/mail/portsentry/rkhunter/teamspeak pre-baked, for
    exercising the agent's collectors). Both flavors share one identity
    (container 'aetheus-vpssim', SSH port 2222) so only ONE runs at a time;
    switching flavors tears the other down. Combine with -r to reset the current
    flavor (drops its volumes).
.PARAMETER Full
    Only meaningful with -vps: select the fully-provisioned VPS-sim flavor
    instead of the default blank box.
.PARAMETER AgentAutoInstall
    Only meaningful with -vps -full: the full VPS-sim mints a registration token
    from the backend API at boot and self-installs the agent, so it appears in
    the dashboard Servers list. Requires the backend to be running (e.g. -s).
    The blank box ships no agent, so enrollment there is a separate step.
.PARAMETER VpsClean
    Only meaningful with -vps: also remove the volumes of the other VPS-sim flavors
    (their provisioned state is lost). Without it, -vps keeps them and only reports
    how many there are.
.PARAMETER Net10
    Only meaningful with -vps: pre-bake the .NET SDK 10.0 into the VPS-sim image
    so the box can `dotnet build/test/run` out of the box. Works with either
    flavor (blank or -full). Pinned to the same channel the agent's pipeline-runner
    installs (10.0). Off by default - the blank box stays bare without it.
.PARAMETER Help
    Show available options and exit.
.PARAMETER HelpLong
    Show detailed help with examples and workflow description.
.EXAMPLE
    .\launch-windows.ps1             Build + start servers + open browser
    .\launch-windows.ps1 -s          Build + start servers (no browser)
    .\launch-windows.ps1 -r          Reset DB + build + start servers
    .\launch-windows.ps1 -tb         Build + run backend tests + exit
    .\launch-windows.ps1 -tf         Build + run frontend tests + exit
    .\launch-windows.ps1 -tg         Run the agent-core unit tests in a Linux container (as CI) + exit
    .\launch-windows.ps1 -ti         Build + run integration tests (Testcontainers) + exit
    .\launch-windows.ps1 -te         Build + run ALL E2E tests on an isolated dedicated DB (dev DB untouched) + exit
    .\launch-windows.ps1 -tec ?      Build + show E2E category picker, run selected + exit
    .\launch-windows.ps1 -tec 1      Build + run E2E category 1 + exit
    .\launch-windows.ps1 -tec 1,2,3  Build + run categories 1 + 2 + 3
    .\launch-windows.ps1 -tb -tf     Build + back + front tests + exit
    .\launch-windows.ps1 -t          Build + ALL unit tests (including observability packages) + exit
    .\launch-windows.ps1 -ta         Build + EVERYTHING (unit + integration + E2E, dev instance left running) + exit
    .\launch-windows.ps1 -c          Build + back + front tests + coverage report + exit
    .\launch-windows.ps1 -hr         Start servers with hot reload (dotnet watch)
    .\launch-windows.ps1 -mf         Start Backend + Frontend (default)
    .\launch-windows.ps1 -ma         Start Agent only
    .\launch-windows.ps1 -mf -ma    Start Backend + Frontend + Agent
    .\launch-windows.ps1 -ra         Start servers + SSH tunnel (target from .remoteagent)
    .\launch-windows.ps1 -db         Snapshot the dev DB (pg_dump) without resetting + exit
    .\launch-windows.ps1 -gpush      Stage all + commit .gitmessage + push
    .\launch-windows.ps1 -w          Assign fresh free ports to this worktree (renews .ylaunch.local) + launch
    .\launch-windows.ps1 -vps        Build + start the BLANK VPS-sim (bare Ubuntu) + exit
    .\launch-windows.ps1 -vps -full  Build + start the fully-provisioned VPS-sim + exit
    .\launch-windows.ps1 -vps -net10 Build + start the BLANK VPS-sim with .NET SDK 10.0 pre-installed
    .\launch-windows.ps1 -vps -r     Reset the current VPS-sim flavor (drops its volumes)
    .\launch-windows.ps1 -vps -vpsclean  Also remove the other flavors' VPS-sim volumes
    .\launch-windows.ps1 -vps -full -aa  Full VPS-sim + self-install the agent at boot
    .\launch-windows.ps1 -h          Show available options
    .\launch-windows.ps1 -hl         Show detailed help with examples
#>
param(
    [Alias("r")]  [switch]$Reset,
    [Alias("t")]  [switch]$TestUnit,
    [Alias("ta")] [switch]$TestAll,
    [Alias("tb")] [switch]$TestBack,
    [Alias("tf")] [switch]$TestFront,
    [Alias("tg")] [switch]$TestAgent,
    [Alias("ti")] [switch]$TestIntegration,
    [Alias("te")] [switch]$TestE2e,
    [Alias("tec")] [string[]]$TestE2eFilter,
    [Alias("c")]  [switch]$Coverage,
    [Alias("mf")] [switch]$ModeFront,
    [Alias("ma")] [switch]$ModeAgent,
    [Alias("hr")] [switch]$HotReload,
    [Alias("s")]  [switch]$Silent,
    [Alias("ra")] [switch]$RemoteAgent,
    [Alias("db")] [switch]$DumpDb,
    [Alias("gpush")] [switch]$GitPush,
    [Alias("w")]  [switch]$Worktree,
    [Alias("vps")] [switch]$VpsSim,
    [switch]$Full,
    [Alias("aa")] [switch]$AgentAutoInstall,
    [switch]$Net10,
    [switch]$VpsClean,
    [Alias("h")]  [switch]$Help,
    [Alias("hl")] [switch]$HelpLong
)

# PowerShell 5.1 trampoline: relaunch via pwsh (PS 7+).
# PS 5.1 parses the entire file before execution, so the core logic lives in
# scripts\launch-core.ps1 (PS7 syntax) which is dot-sourced only after this gate.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwshPath = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $pwshPath) {
        Write-Host "ERROR: PowerShell 7+ required but pwsh not found. Install from https://aka.ms/powershell" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Relaunching in PowerShell 7..." -ForegroundColor DarkGray
    $boundArgs = @()
    foreach ($key in $PSBoundParameters.Keys) {
        $val = $PSBoundParameters[$key]
        if ($val -is [switch]) { if ($val) { $boundArgs += "-$key" } }
        else {
            $boundArgs += "-$key"
            $boundArgs += if ($val -is [array]) { [string]::Join(',', $val) } else { "$val" }
        }
    }
    & $pwshPath -NoLogo -File $PSCommandPath @boundArgs
    exit $LASTEXITCODE
}

# PS 7+ continues here - dot-source the core implementation (relocated to scripts/).
# Pass the repo root explicitly: this entry point lives at the root, but the core
# now sits under scripts/, so its own $PSScriptRoot is no longer the repo root.
$root = $PSScriptRoot
# PowerShell treats an unquoted comma as an array separator. Keep the documented
# `-tec Auth,Smoke` form working by passing one comma-separated filter to the core.
if ($TestE2eFilter) { $TestE2eFilter = [string]::Join(',', $TestE2eFilter) }
# STD-CRAP: the kit's CRAP gate, a verbatim copy run by the core after a green -c coverage run.
$crapGate = Join-Path $PSScriptRoot "scripts\crap-gate.ps1"
$script:ylaunchExitCode = 0
. "$PSScriptRoot\scripts\launch-core.ps1"
exit $script:ylaunchExitCode
