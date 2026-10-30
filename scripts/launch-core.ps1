# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
$ErrorActionPreference = "Stop"

# ---------- implications ----------

# NOTE: check the bound VALUE, not $PSBoundParameters - this core is dot-sourced from launch-windows.ps1,
# and $PSBoundParameters is scoped to the parent's invocation (always empty here), so
# ContainsKey('TestE2eFilter') was always false and `-tec <cat>` silently never ran E2E.
if (-not [string]::IsNullOrEmpty($TestE2eFilter)) { $TestE2e = $true }
# -ta runs everything; -t runs every unit suite. Integration (Testcontainers) and E2E
# (isolated :15433) bring their own databases, so NO test flag ever resets the dev DB -
# only an explicit -r does (handled in the post-test section, which the test path exits before).
if ($TestAll)  { $TestUnit = $true; $TestIntegration = $true; $TestE2e = $true }
if ($TestUnit) { $TestBack = $true; $TestFront = $true; $TestAgent = $true; $TestAnalyzers = $true }
if ($Coverage) { $TestBack = $true; $TestFront = $true }
$buildConfiguration = if ($Coverage) { "Release" } else { "Debug" }
# Every build output read after the build (runtime snapshots, the front publish; the browser image builds its own Release)
# follows this value: `-c -te` builds Release, and E2E used to copy and run stale Debug outputs.
$anyTest = $TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration -or $TestE2e
$e2eOnly = $TestE2e -and -not ($TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration -or $Coverage)
# The agent suite is built and run in its Linux container (see "Agent suite container"), so a run of that suite
# alone needs no host build.
$agentOnly = $TestAgent -and -not ($TestBack -or $TestFront -or $TestAnalyzers -or $TestIntegration -or $TestE2e -or $Coverage)
$tunnelOnly = $RemoteAgent -and -not $ModeFront -and -not $ModeAgent -and -not $anyTest -and -not $Reset
if ($RemoteAgent -and -not $tunnelOnly) { $ModeFront = $true }
if (-not $ModeFront -and -not $ModeAgent -and -not $tunnelOnly) { $ModeFront = $true }

# ---------- help ----------

function Show-Options {
    Write-Host ""
    Write-Host "launch-windows.ps1 - Build and launch Aetheus" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "USAGE:" -ForegroundColor Yellow
    Write-Host "  .\launch-windows.ps1 [OPTIONS]"
    Write-Host ""
    Write-Host "OPTIONS:" -ForegroundColor Yellow
    Write-Host "  -r,  -Reset            Drop & recreate local Postgres docker volume (fresh seed)"
    Write-Host "  -tb, -TestBack         Run backend unit tests"
    Write-Host "  -tf, -TestFront        Run frontend unit tests"
    Write-Host "  -tg, -TestAgent        Run agent-core unit tests in a Linux container of the pinned .NET SDK image (as CI), never on the host"
    Write-Host "  -t,  -TestUnit         Run ALL unit tests (back + front + agent-core in a Linux container + analyzers + observability + JavaScript + the locked ESLint from a clean npm ci in the pipeline's Node container)"
    Write-Host "  -ti, -TestIntegration  Run integration tests (Aetheus.Back.IntegrationTests, Testcontainers)"
    Write-Host "  -te                    Run ALL E2E tests (isolated dedicated DB :15433, dev DB untouched) in the QA browser image (Dockerfile.e2e)"
    Write-Host "  -tec <filter>          Run specific E2E categories (implies -te)"
    Write-Host "    -tec ?               Show interactive category picker"
    Write-Host "    -tec 1               Category by number"
    Write-Host "    -tec 1,2,3           Categories by number"
    Write-Host "    -tec Auth            Category by name"
    Write-Host "    -tec Auth,Acts,3     Mix of names and numbers"
    Write-Host "  -ta, -TestAll          Run EVERYTHING: unit + integration + E2E (dev DB never reset, dev instance left running)"
    Write-Host "  -c,  -Coverage         Collect coverage + open HTML report (Back+Front+Agent.Core; integration coverage kept beside it, implies -tb -tf), then the CRAP gate"
    Write-Host "  -mf, -ModeFront        Start Backend + Frontend (default)"
    Write-Host "  -ma, -ModeAgent        Start Agent Worker Service"
    Write-Host "  -hr, -HotReload        Start servers with dotnet watch (hot reload)"
    Write-Host "  -s,  -Silent           Start servers without opening browser"
    Write-Host "  -ra, -RemoteAgent      SSH tunnel for remote agent (fwd port 5301)"
    Write-Host "                         Target: user@host or user@host:sshport"
    Write-Host "                         Read from .remoteagent or prompted"
    Write-Host "  -db, -DumpDb           pg_dump snapshot of the dev DB (no reset) + exit"
    Write-Host "  -w,  -Worktree         Assign FRESH free ports to THIS worktree (writes/renews"
    Write-Host "                         .ylaunch.local, never the defaults), then build + launch"
    Write-Host "  -gpush, -GitPush       Stage all + commit .gitmessage + git push"
    Write-Host "  -vps, -VpsSim          Build + start the disposable local VPS-sim + exit"
    Write-Host "                         (default = BLANK bare box; one box at a time;"
    Write-Host "                          add -r to reset the current flavor)"
    Write-Host "  -full, -Full           With -vps: use the fully-provisioned box instead"
    Write-Host "                         (Docker + apache/certbot/mail/.../teamspeak baked)"
    Write-Host "  -aa, -AgentAutoInstall With -vps -full: self-install the agent at boot"
    Write-Host "  -net10, -Net10         With -vps: pre-bake .NET SDK 10.0 into the box"
    Write-Host "  -h,  -Help             Show available options"
    Write-Host "  -hl, -HelpLong         Show detailed help with examples"
    Write-Host ""
}

if ($Help) {
    Show-Options
    exit 0
}

if ($HelpLong) {
    Show-Options
    Write-Host "EXAMPLES:" -ForegroundColor Yellow
    Write-Host "  .\launch-windows.ps1             Build + start servers + open browser"
    Write-Host "  .\launch-windows.ps1 -s          Build + start servers (no browser)"
    Write-Host "  .\launch-windows.ps1 -r          Reset DB + build + start servers"
    Write-Host "  .\launch-windows.ps1 -tb         Build + backend tests only + exit"
    Write-Host "  .\launch-windows.ps1 -tf         Build + frontend tests only + exit"
    Write-Host "  .\launch-windows.ps1 -ti         Build + integration tests only (Testcontainers) + exit"
    Write-Host "  .\launch-windows.ps1 -te         Build + ALL E2E tests on isolated dedicated DB (dev DB untouched) + exit"
    Write-Host "  .\launch-windows.ps1 -tec ?      Build + E2E category picker (interactive)"
    Write-Host "  .\launch-windows.ps1 -tec 1      Build + E2E category 1 only"
    Write-Host "  .\launch-windows.ps1 -tec 1,2,3  Build + categories 1 + 2 + 3"
    Write-Host "  .\launch-windows.ps1 -tec Auth   Build + Auth category only"
    Write-Host "  .\launch-windows.ps1 -tb -tf     Build + back + front tests + exit"
    Write-Host "  .\launch-windows.ps1 -tb -tf -ti Build + back + front + integration + exit"
    Write-Host "  .\launch-windows.ps1 -t          Build + ALL unit tests (back/front/agent-core/analyzers) + exit"
    Write-Host "  .\launch-windows.ps1 -ta         Build + EVERYTHING (unit + integration + E2E) + exit"
    Write-Host "  .\launch-windows.ps1 -c          Build + back + front tests + coverage report + exit"
    Write-Host "  .\launch-windows.ps1 -hr         Start servers with hot reload (dotnet watch)"
    Write-Host "  .\launch-windows.ps1 -s          Build + start servers without opening browser"
    Write-Host "  .\launch-windows.ps1 -ra         Start servers + SSH tunnel (reads .remoteagent)"
    Write-Host "  .\launch-windows.ps1 -gpush      Stage all + commit .gitmessage + git push"
    Write-Host "  .\launch-windows.ps1 -w          Assign fresh free ports to this worktree + launch"
    Write-Host "  .\launch-windows.ps1 -vps        Build + start the BLANK VPS-sim (bare Ubuntu)"
    Write-Host "  .\launch-windows.ps1 -vps -full  Build + start the fully-provisioned VPS-sim"
    Write-Host "  .\launch-windows.ps1 -vps -net10 Build + start the BLANK VPS-sim with .NET SDK 10.0 baked in"
    Write-Host "  .\launch-windows.ps1 -vps -r     Reset the current VPS-sim flavor (drops volumes)"
    Write-Host "  .\launch-windows.ps1 -vps -full -aa  Full VPS-sim + self-install the agent at boot"
    Write-Host "  .\launch-windows.ps1 -ma         Build + start Agent only"
    Write-Host "  .\launch-windows.ps1 -mf -ma     Build + start Backend + Frontend + Agent"
    Write-Host ""
    Write-Host "WORKFLOW:" -ForegroundColor Yellow
    Write-Host "  1. Kills existing Aetheus processes (back/front ports)"
    Write-Host "  2. Builds the solution (skipped with -hr)"
    Write-Host "  3. Runs requested tests (if any test flag is set)"
    Write-Host "  4. Starts services based on mode flags (-mf: Back+Front, -ma: Agent)"
    Write-Host "  5. Opens browser to the frontend (when -mf)"
    Write-Host "  6. Streams server output until Ctrl+C"
    Write-Host ""
    Write-Host "  Coexistence: run -w to assign a FRESH free BACK_HTTPS_PORT / BACK_HTTP_PORT"
    Write-Host "  / FRONT_PORT triplet (gitignored .ylaunch.local, never the defaults), so THIS"
    Write-Host "  worktree's stack coexists with another worktree's (DB stays shared). Re-running"
    Write-Host "  -w renews the ports. Or hand-write those keys yourself. LABEL=<text> tags this"
    Write-Host "  worktree in the front dev top-bar (-w keeps an existing one, else folder name)."
    Write-Host "  WORKTREE=<id> records the worktree identity (-w sets it to the folder name)."
    Write-Host ""
    Write-Host "  Test runs: the unit, package, JavaScript and integration suites of one run start together (each"
    Write-Host "  prints its block when it ends); with -c they stay one after the other. A full E2E run leaves out the"
    Write-Host "  sweep of every page (PageRules, owner decision of 2026-10-27: run it by hand, -tec PageRules)."
    Write-Host "  YLAUNCH_SEQUENTIAL_SUITES=1 runs the suites one after the other; YLAUNCH_E2E_SHARDS=2 runs the"
    Write-Host "  E2E suite with the sweep, in two shards on their own ports and databases (the second 1000 ports up)."
    Write-Host ""
    exit 0
}

# ---------- git push ----------

if ($GitPush) {
    # $root is the repo root resolved by the dot-sourcing launcher; $PSScriptRoot is scripts/, so
    # .gitmessage (which lives at the repo root) must be resolved from $root - matches launch-linux.sh.
    $gmFile = Join-Path $root ".gitmessage"
    if (-not (Test-Path $gmFile)) {
        Write-Host "  .gitmessage not found at $gmFile" -ForegroundColor Red
        exit 1
    }
    $msg = Get-Content $gmFile -Raw
    if ([string]::IsNullOrWhiteSpace($msg)) {
        Write-Host "  .gitmessage is empty" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Git: stage + commit + push" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    Write-Host "  Staging all changes..." -ForegroundColor DarkGray
    git add -A
    if ($LASTEXITCODE -ne 0) { Write-Host "  git add failed." -ForegroundColor Red; exit 1 }

    Write-Host "  Committing with .gitmessage..." -ForegroundColor DarkGray
    git commit -F $gmFile
    if ($LASTEXITCODE -ne 0) { Write-Host "  git commit failed." -ForegroundColor Red; exit 1 }

    Write-Host "  Pushing..." -ForegroundColor DarkGray
    git push
    if ($LASTEXITCODE -ne 0) { Write-Host "  git push failed." -ForegroundColor Red; exit 1 }

    Write-Host "  Done." -ForegroundColor Green
    exit 0
}

# ---------- paths ----------

# Repo root. launch-windows.ps1 (the entry point, which sits at the repo root) sets $root
# before dot-sourcing us. This file now lives under scripts/, so its own
# $PSScriptRoot is NOT the repo root - fall back to the parent dir if unset.
if (-not $root) { $root = Split-Path $PSScriptRoot -Parent }
$solution    = Join-Path $root "Aetheus.slnx"
$backDir     = Join-Path $root "src\Aetheus.Back"
$frontDir    = Join-Path $root "src\Aetheus.Front"

# ---------- worktree git-repos junction ----------
# A worktree backend resolves GitLight:RepositoriesPath ('./data/git-repos') against ITS OWN content
# root, which starts empty (data/ is gitignored, never copied into a worktree). Result: pipeline runs
# still work (trigger reads git-first then falls back to the DB YAML), but pipeline EDITS 409 with
# "repository not initialized on disk" - even though the shared DB references repos that physically live
# on the MAIN checkout's disk. Bridge them with a junction so a worktree stack transparently reads AND
# writes the real repos. Idempotent; a no-op in the main checkout (its own data/git-repos is authoritative).
$wtRepos = Join-Path $backDir "data\git-repos"
if (-not (Test-Path $wtRepos)) {
    $commonDir = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($commonDir) {
        $mainRoot  = Split-Path $commonDir -Parent
        $mainRepos = Join-Path $mainRoot "src\Aetheus.Back\Data\git-repos"
        if ((Test-Path $mainRepos) -and ((Resolve-Path $mainRoot).Path -ne (Resolve-Path $root).Path)) {
            New-Item -ItemType Directory -Path (Split-Path $wtRepos -Parent) -Force | Out-Null
            New-Item -ItemType Junction -Path $wtRepos -Target $mainRepos -ErrorAction SilentlyContinue | Out-Null
            if (Test-Path $wtRepos) { Write-Host "  Worktree git-repos junction -> $mainRepos" -ForegroundColor Magenta }
        }
    }
}

# ---------- per-worktree overrides (.ylaunch.local) ----------
# A gitignored KEY=VALUE file at the repo root lets a second worktree run the
# whole stack on its OWN ports, so it coexists with another worktree's instance
# instead of fighting over 5301/5401 (the DB stays shared - one Postgres serves
# both). Absent file => default ports, behaviour identical to before.
# It also carries LABEL, a free-text tag surfaced in the front dev top-bar banner, and
# WORKTREE, the worktree's stable identity (its folder name) for tooling that tells instances apart.
#   Keys: BACK_HTTPS_PORT, BACK_HTTP_PORT, FRONT_PORT, LABEL, WORKTREE
$backHttpsPort = 5301
$backHttpPort  = 5300
$frontPort     = 5401
$bannerLabel   = ""
$worktreeName  = ""
$script:portsOverridden = $false
$portsFile = Join-Path $root ".ylaunch.local"

# -w / -Worktree: give THIS worktree its OWN ports without hand-writing the file. -w ALWAYS
# assigns a fresh, non-default triplet (back-https / back-http / front), scanned +2/+2/+1 from
# the 5302/5303/5402 base upward, and NEVER the 5301/5300/5401 defaults. It also skips any port
# currently bound AND any port the existing .ylaunch.local already used, so a re-run genuinely
# renews the ports instead of handing back the same set. The file is (over)written; an existing
# LABEL is preserved, otherwise it defaults to the worktree folder name (feeds the dev banner),
# and WORKTREE is always (re)written to the worktree folder name (its stable identity).
if ($Worktree) {
    function Test-TcpPortFree([int]$candidate) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $candidate)
            $listener.Start(); return $true
        }
        catch { return $false }
        finally { if ($listener) { $listener.Stop() } }
    }
    # Carry over an existing LABEL; reserve the file's current ports so we hand back a new set.
    $reservedPorts = @()
    $existingLabel = $null
    $hadFile = Test-Path $portsFile
    if ($hadFile) {
        foreach ($line in Get-Content $portsFile) {
            $t = $line.Trim()
            if (-not $t -or $t.StartsWith("#")) { continue }
            $i = $t.IndexOf("="); if ($i -lt 1) { continue }
            $k = $t.Substring(0, $i).Trim(); $v = $t.Substring($i + 1).Trim()
            switch ($k) {
                "BACK_HTTPS_PORT" { $reservedPorts += [int]$v }
                "BACK_HTTP_PORT"  { $reservedPorts += [int]$v }
                "FRONT_PORT"      { $reservedPorts += [int]$v }
                "LABEL"           { $existingLabel = $v }
            }
        }
    }
    $picked = $null
    for ($slot = 1; $slot -le 50 -and -not $picked; $slot++) {
        $hs = 5302 + ($slot - 1) * 2
        $hp = 5303 + ($slot - 1) * 2
        $fr = 5402 + ($slot - 1)
        if ($reservedPorts -contains $hs -or $reservedPorts -contains $hp -or $reservedPorts -contains $fr) { continue }
        if ((Test-TcpPortFree $hs) -and (Test-TcpPortFree $hp) -and (Test-TcpPortFree $fr)) {
            $picked = @{ Https = $hs; Http = $hp; Front = $fr }
        }
    }
    if (-not $picked) {
        Write-Host "  -w: no free port triplet found (scanned 50 slots from 5302/5303/5402)." -ForegroundColor Red
        exit 1
    }
    $wtFolder = Split-Path $root -Leaf
    $wtLabel  = if ($existingLabel) { $existingLabel } else { $wtFolder }
    $action   = if ($hadFile) { "renewed" } else { "created" }
    @(
        "# Per-worktree ylaunch overrides (gitignored). Auto-generated by 'launch-windows.ps1 -w'.",
        "# Keys: BACK_HTTPS_PORT, BACK_HTTP_PORT, FRONT_PORT (port coexistence), LABEL (dev banner tag), WORKTREE (worktree id).",
        "BACK_HTTPS_PORT=$($picked.Https)",
        "BACK_HTTP_PORT=$($picked.Http)",
        "FRONT_PORT=$($picked.Front)",
        "LABEL=$wtLabel",
        "WORKTREE=$wtFolder"
    ) -join "`n" | Set-Content -Path $portsFile -Encoding utf8
    Write-Host "  -w: $action .ylaunch.local - back $($picked.Https)/$($picked.Http), front $($picked.Front), label '$wtLabel', worktree '$wtFolder'." -ForegroundColor Green
}

if (Test-Path $portsFile) {
    foreach ($line in Get-Content $portsFile) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith("#")) { continue }
        $i = $t.IndexOf("=")
        if ($i -lt 1) { continue }
        $k = $t.Substring(0, $i).Trim()
        $v = $t.Substring($i + 1).Trim()
        switch ($k) {
            "BACK_HTTPS_PORT" { $backHttpsPort = [int]$v; $script:portsOverridden = $true }
            "BACK_HTTP_PORT"  { $backHttpPort  = [int]$v; $script:portsOverridden = $true }
            "FRONT_PORT"      { $frontPort     = [int]$v; $script:portsOverridden = $true }
            "LABEL"           { $bannerLabel   = $v }
            "WORKTREE"        { $worktreeName  = $v }
        }
    }
    if ($script:portsOverridden) {
        Write-Host "  Port override (.ylaunch.local): back $backHttpsPort/$backHttpPort, front $frontPort" -ForegroundColor Magenta
    }
    if ($bannerLabel) {
        Write-Host "  Dev banner label (.ylaunch.local): $bannerLabel" -ForegroundColor Magenta
    }
    if ($worktreeName) {
        Write-Host "  Worktree (.ylaunch.local): $worktreeName" -ForegroundColor Magenta
    }
}
# A test run (any -t flag) never touches the development instance, so the user can keep using it while
# the suites run: its E2E stack listens 1000 ports above this checkout's own (6301/6300/6401 by default),
# runs from its own snapshots (TestResults\YLaunchE2e*), and its sweeps only stop what a test run started
# (see Get-AetheusOwnProcessPattern). Unit and integration suites open no port.
$script:testRun = $anyTest
if ($script:testRun) {
    $backHttpsPort += 1000
    $backHttpPort  += 1000
    $frontPort     += 1000
    $script:portsOverridden = $true
    Write-Host "  Test run: E2E stack on back $backHttpsPort/$backHttpPort, front $frontPort (the development instance is left running)." -ForegroundColor Magenta
}
$script:killPorts = @($backHttpsPort, $frontPort)
# A full E2E run splits the suite in shards, each on a stack of its own (New-E2eStack): the second stack
# listens this many ports above the first, so a sweep of leftovers has to look there too.
$e2eStackPortOffset = 1000
$e2eShardCount = 1
# Two shards only on request (YLAUNCH_E2E_SHARDS=2): shard 1 is then the manual sweep of every page (PageRules).
if ($script:testRun -and $TestE2e -and -not $HotReload -and [string]::IsNullOrEmpty($TestE2eFilter) -and $env:YLAUNCH_E2E_SHARDS -eq '2') {
    $e2eShardCount = 2
    $script:killPorts += @(($backHttpsPort + $e2eStackPortOffset), ($frontPort + $e2eStackPortOffset))
}
$frontUrl     ="https://localhost:$frontPort"
$backUrl      = "https://localhost:$backHttpsPort/health/live"
$backAspUrls  = "https://localhost:$backHttpsPort;http://localhost:$backHttpPort"
# Agent-facing API URL baked into the Add-Agent wizard's install command, tracking the
# per-worktree port override (so the wizard points agents at THIS worktree's backend, not
# the :5300 default). HTTPS - not the HTTP default - because the agent's ValidateServerUrl
# rejects plain HTTP for non-loopback hosts, and the wizard's host.docker.internal dev
# variant (for the VPS-sim) is non-loopback, so it must be https.
$backPublicApiUrl = if ($env:YLAUNCH_PUBLIC_API_BASE_URL) {
    $candidatePublicApiUrl = $env:YLAUNCH_PUBLIC_API_BASE_URL.TrimEnd('/')
    $candidatePublicApiUri = $null
    if (-not [Uri]::TryCreate(
            $candidatePublicApiUrl,
            [UriKind]::Absolute,
            [ref]$candidatePublicApiUri) -or $candidatePublicApiUri.Scheme -ne 'https') {
        throw "YLAUNCH_PUBLIC_API_BASE_URL must be an absolute HTTPS URL."
    }
    $candidatePublicApiUrl
} else {
    "https://localhost:$backHttpsPort"
}
$frontAspUrls = "https://localhost:$frontPort"
$corsFrontUrl = "https://localhost:$frontPort"

# The Blazor WASM front reads its config in the BROWSER from wwwroot/appsettings*.json
# (auto-merged in Development), so a process env var can't redirect it. Write a gitignored
# wwwroot/appsettings.Development.json that carries:
#   - ApiBaseUrl  (only when ports are overridden) pointing the front at THIS worktree's back;
#   - DevBanner   (always) feeding the dev-only top-bar banner: the current git branch and the
#     free-text LABEL from .ylaunch.local. Absent in prod (the file only merges in Development),
#     so the banner never shows there.
# Written BEFORE the build so `dotnet run --no-build` serves it.
$frontDevSettings = Join-Path $frontDir "wwwroot\appsettings.Development.json"
$gitBranch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
if (-not $gitBranch) { $gitBranch = "" }
$devConfig = [ordered]@{}
if ($script:portsOverridden) { $devConfig["ApiBaseUrl"] = "https://localhost:$backHttpsPort" }
$devConfig["DevBanner"] = [ordered]@{ Branch = "$gitBranch".Trim(); Label = $bannerLabel }
($devConfig | ConvertTo-Json -Depth 5) | Set-Content -Path $frontDevSettings -Encoding utf8
$agentDir    = Join-Path $root "src\Aetheus.Agent.Windows"
$coverageDir = Join-Path $root "TestResults\Coverage"
$reportDir   = Join-Path $root "TestResults\CoverageReport"
$devDbCompose = Join-Path $root "deploy\compose\dev-db.compose.yml"
$e2eDbCompose = Join-Path $root "deploy\compose\e2e-db.compose.yml"
# Connection string the E2E backend uses instead of the dev DB, so the destructive E2E reset only
# ever touches the ephemeral aetheus-e2e-database (:15433), never the shared dev DB (:15432).
# The DB NAME is "aetheus_e2e" (not "aetheus"): the backend's reset-db endpoint refuses to drop
# any database whose name doesn't end with "_e2e", so a fallback to the dev connection string can
# never wipe the dev DB (fail-safe added after the 2026-06-26 dev-DB wipe incident).
$e2eConnString = "Host=127.0.0.1;Port=15433;Database=aetheus_e2e;Username=postgres;Password=postgres"
$vpssimCompose = Join-Path $root "deploy\compose\vpssim.compose.yml"
$vpssimBlankCompose = Join-Path $root "deploy\compose\vpssim-blank.compose.yml"
$dbSnapshotDir = Join-Path $root "TestResults\DbSnapshots"

# ---------- VPS simulator (-vps) ----------
# One disposable box, two flavors sharing a single identity (container
# 'aetheus-vpssim', SSH port 2222): blank by default, -full for the
# fully-provisioned box. Only one runs at a time - switching tears the other
# down. -r resets the chosen flavor; -aa (full only) self-installs the agent.

if ($VpsSim) {
    $flavor     = if ($Full) { "full" } else { "blank" }
    $vpssimFile = if ($Full) { $vpssimCompose } else { $vpssimBlankCompose }
    $proj       = "aetheus-vpssim-$flavor"
    $sshPort    = if ($env:VPSSIM_SSH_PORT) { $env:VPSSIM_SSH_PORT } else { "2222" }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Aetheus VPS-sim ($flavor)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    if (-not (Test-Path $vpssimFile)) {
        Write-Host "  Compose file not found at $vpssimFile" -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }

    # Single identity: remove whatever container currently holds the shared name
    # (the other flavor, if any) so this flavor can claim it. One box at a time.
    & docker rm -f aetheus-vpssim 2>$null | Out-Null

    # Disposable by construction. Removing the container above leaves its named volumes
    # behind with nothing attached, and no other code path in this repo ever reclaims
    # them: measured 2026-08-19, two abandoned project sets held 33 GB of orphaned
    # volumes (the nested daemon's /var/lib/docker and /var/lib/containerd stores),
    # one of them from a worktree that no longer exists. The volumes of EVERY Aetheus vpssim
    # compose project except the one about to start are those of another flavor: their state
    # is not this run's to drop, so they are only counted, and removed with -vpsclean alone
    # (SCRIPTS-001, decision of 2026-10-08).
    # Matched by name on purpose: `docker volume prune` is forbidden by
    # docs/runbooks/storage-maintenance.md because it would reach other projects.
    # Only this repository's own compose projects (aetheus-vpssim-<flavor>, volumes named
    # "<project>_<volume>"): a name that merely contains "vpssim" can belong to another project,
    # and its volumes and containers are not ours to destroy (SCRIPTS-001).
    function Select-AetheusVpsSimStaleVolumes([string[]]$Names, [string]$KeepProject) {
        @($Names | Where-Object {
            $_ -and $_ -cmatch '^aetheus-vpssim-[a-z0-9-]+_' -and
            -not $_.StartsWith("${KeepProject}_", [StringComparison]::Ordinal)
        })
    }
    $staleVolumes = @(Select-AetheusVpsSimStaleVolumes @(& docker volume ls -q --filter name=vpssim 2>$null) $proj)
    if ($staleVolumes.Count -gt 0 -and -not $VpsClean) {
        Write-Host "  $($staleVolumes.Count) VPS-sim volume(s) of another flavor kept; -vpsclean removes them." -ForegroundColor DarkGray
    }
    elseif ($staleVolumes.Count -gt 0) {
        Write-Host "  Reclaiming $($staleVolumes.Count) VPS-sim volume(s) of other flavors (-vpsclean)..." -ForegroundColor DarkGray
        foreach ($vol in $staleVolumes) {
            # A stopped container from a vanished worktree's project can still hold the
            # volume; drop those holders first, otherwise `volume rm` refuses.
            $holders = @(& docker ps -aq --filter "volume=$vol" 2>$null | Where-Object { $_ })
            if ($holders.Count -gt 0) { & docker rm -f @holders 2>$null | Out-Null }
            & docker volume rm $vol 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "    Could not remove volume '$vol' (still in use?)." -ForegroundColor Yellow
            }
        }
    }

    if ($Reset) {
        Write-Host "  Reset: tearing down the $flavor VPS-sim (drops its volumes)..." -ForegroundColor DarkGray
        & docker compose -p $proj -f $vpssimFile down -v | Out-Host
    }

    # Agent auto-install is a full-box boot feature (the blank box ships no agent).
    if ($AgentAutoInstall) {
        if ($Full) {
            $env:VPSSIM_AGENT = "1"
            Write-Host "  Agent auto-install: ON (mints a token + self-installs at boot)" -ForegroundColor Yellow
        } else {
            Write-Host "  -aa ignored: the blank box ships no agent (enrollment is a separate step)." -ForegroundColor Yellow
            Write-Host "  For boot auto-install use: .\launch-windows.ps1 -vps -full -aa" -ForegroundColor DarkGray
        }
    } elseif ($Full) {
        $env:VPSSIM_AGENT = "0"
    }

    # Optional .NET SDK 10.0 pre-bake (-net10): a build-time install into the image so the
    # box can `dotnet build/test/run` out of the box. Passed to the Dockerfile via the
    # INSTALL_DOTNET build arg (compose reads $env:VPSSIM_DOTNET). Works for either flavor.
    $env:VPSSIM_DOTNET = if ($Net10) { "1" } else { "0" }
    if ($Net10) {
        Write-Host "  .NET SDK 10.0: pre-baking into the $flavor box (-net10)" -ForegroundColor Yellow
    }

    # Honor the per-worktree port override (.ylaunch.local): the VPS-sim agent must
    # enroll against THIS worktree's backend, not the hardcoded default 5301 - same
    # class of bug as PublicApiBaseUrl. Export it before compose resolves the
    # VPSSIM_BACKEND_URL interpolation. host.docker.internal reaches the host's port.
    $backendUrl = if ($env:VPSSIM_BACKEND_URL) { $env:VPSSIM_BACKEND_URL } else { "https://host.docker.internal:$backHttpsPort" }
    $env:VPSSIM_BACKEND_URL = $backendUrl

    # Keep the admin credential on the host: -aa mints a one-use registration token and passes only
    # that token to the disposable container. The explicit environment override supports developers
    # who changed the local admin password; otherwise use the same canonical Development seed config
    # as DbInitializer instead of duplicating a credential that can drift.
    if ($Full -and $AgentAutoInstall -and -not $env:VPSSIM_AGENT_TOKEN) {
        $localBackendUrl = "https://localhost:$backHttpsPort"
        $developmentAuth = (Get-Content (Join-Path $root "src/Aetheus.Back/appsettings.Development.json") -Raw | ConvertFrom-Json).Auth
        $adminEmail = if ($env:VPSSIM_ADMIN_EMAIL) { $env:VPSSIM_ADMIN_EMAIL } else { [string]$developmentAuth.AdminEmail }
        $adminPassword = if ($env:VPSSIM_ADMIN_PASSWORD) { $env:VPSSIM_ADMIN_PASSWORD } else { [string]$developmentAuth.AdminPassword }
        if ([string]::IsNullOrWhiteSpace($adminEmail) -or [string]::IsNullOrWhiteSpace($adminPassword)) {
            throw "Development admin credentials are missing from appsettings.Development.json."
        }
        try {
            $loginBody = @{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json -Compress
            $login = Invoke-RestMethod -Method Post -Uri "$localBackendUrl/api/auth/login" `
                -ContentType "application/json" -Body $loginBody -SkipCertificateCheck
            if (-not $login.token) { throw "login returned no token" }
            $registration = Invoke-RestMethod -Method Post `
                -Uri "$localBackendUrl/api/auth/registration-tokens" `
                -Headers @{ Authorization = "Bearer $($login.token)" } `
                -ContentType "application/json" `
                -Body '{"expirationHours":1}' -SkipCertificateCheck
            if (-not $registration.token) { throw "registration-token creation returned no token" }
            $env:VPSSIM_AGENT_TOKEN = [string]$registration.token
            Write-Host "  One-use agent registration token minted by the local backend." -ForegroundColor DarkGray
        } catch {
            Write-Host "  Agent token mint failed: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  Set VPSSIM_ADMIN_PASSWORD to the current local admin password and retry." -ForegroundColor Yellow
            $script:ylaunchExitCode = 1
            return
        } finally {
            $adminPassword = $null
            $developmentAuth = $null
            $loginBody = $null
            $login = $null
        }
    }

    Write-Host "  Building + starting the $flavor VPS-sim container..." -ForegroundColor DarkGray
    & docker compose -p $proj -f $vpssimFile up -d --build | Out-Host
    Remove-Item Env:VPSSIM_AGENT_TOKEN -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  docker compose up failed." -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }

    # Configure the disposable SSH credential only after the container exists. Keeping it out of
    # Docker build args prevents the password from being persisted in image metadata or layers.
    $rootPassword = if ($env:VPSSIM_ROOT_PASSWORD) { [string]$env:VPSSIM_ROOT_PASSWORD } else { "aetheus" }
    try {
        $passwordConfigured = $false
        for ($attempt = 1; $attempt -le 15 -and -not $passwordConfigured; $attempt++) {
            # Windows PowerShell writes CRLF to native-process pipelines. Strip CR so Linux
            # chpasswd does not silently include it in the configured password.
            "root:$rootPassword" | & docker exec -i aetheus-vpssim sh -c 'tr -d "\r" | chpasswd' 2>$null
            $passwordConfigured = $LASTEXITCODE -eq 0
            if (-not $passwordConfigured) { Start-Sleep -Seconds 1 }
        }
        if (-not $passwordConfigured) {
            Write-Host "  Failed to configure the disposable VPS-sim SSH credential." -ForegroundColor Red
            $script:ylaunchExitCode = 1
            return
        }
    } finally {
        $rootPassword = $null
    }

    $switchCmd  = if ($Full) { ".\launch-windows.ps1 -vps" } else { ".\launch-windows.ps1 -vps -full" }
    $resetCmd   = if ($Full) { ".\launch-windows.ps1 -vps -full -r" } else { ".\launch-windows.ps1 -vps -r" }
    $otherFlav  = if ($Full) { "blank" } else { "full" }
    Write-Host ""
    Write-Host "  VPS-sim ($flavor) is up." -ForegroundColor Green
    Write-Host "    Shell : docker exec -it aetheus-vpssim bash" -ForegroundColor DarkGray
    if ($Net10) {
        Write-Host "    dotnet: .NET SDK 10.0 baked in (docker exec -it aetheus-vpssim dotnet --version)" -ForegroundColor DarkGray
    }
    Write-Host "    SSH   : ssh root@localhost -p $sshPort" -ForegroundColor DarkGray
    Write-Host "    Pass  : aetheus  (root, fixed, local-only/disposable)" -ForegroundColor DarkGray
    if ($Full -and $AgentAutoInstall) {
        Write-Host "    Agent : enrolling against $backendUrl - watch the dashboard Servers list" -ForegroundColor DarkGray
        Write-Host "            (the backend must be running: .\launch-windows.ps1 -s)" -ForegroundColor DarkGray
    }
    Write-Host "    Switch: $switchCmd   (-> $otherFlav box)" -ForegroundColor DarkGray
    Write-Host "    Reset : $resetCmd" -ForegroundColor DarkGray
    Write-Host "    Stop  : docker compose -p $proj -f $vpssimFile down" -ForegroundColor DarkGray
    $script:ylaunchExitCode = 0
    return
}

# ---------- helpers ----------

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
}

function Ensure-DevDb {
    Write-Step "Ensuring local PostgreSQL (aetheus-local-database) is running..."
    $running = & docker ps --filter "name=^/aetheus-local-database$" --format "{{.Names}}" 2>$null
    if ($running -eq "aetheus-local-database") {
        Write-Host "  Already up." -ForegroundColor DarkGray
        return
    }
    & docker compose -f $devDbCompose up -d | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Failed to start dev database. Is Docker Desktop running?" -ForegroundColor Red
        exit 1
    }
    # Wait for healthy
    for ($i = 0; $i -lt 30; $i++) {
        $health = & docker inspect --format '{{.State.Health.Status}}' aetheus-local-database 2>$null
        if ($health -eq "healthy") { Write-Host "  Database healthy." -ForegroundColor Green; return }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  Database not healthy after 15s -continuing anyway." -ForegroundColor Yellow
}

function Backup-DevDb {
    # Safety snapshot taken before any destructive DB reset. pg_dump runs INSIDE the
    # container (UTF-8, no BOM) then we copy the file out, sidestepping PowerShell
    # redirection encoding issues. Best-effort but reset is aborted if the dump fails.
    $running = & docker ps --filter "name=^/aetheus-local-database$" --format "{{.Names}}" 2>$null
    if ($running -ne "aetheus-local-database") {
        Write-Host "  No running database to snapshot - skipping backup." -ForegroundColor DarkGray
        return
    }
    New-Item -ItemType Directory -Force $dbSnapshotDir | Out-Null
    $stamp    = Get-Date -Format "yyyyMMdd-HHmmss"
    $snapshot = Join-Path $dbSnapshotDir "aetheus-$stamp.sql"
    $inner    = "/tmp/aetheus-$stamp.sql"
    Write-Host "  Running pg_dump..." -ForegroundColor DarkGray
    & docker exec aetheus-local-database sh -c "pg_dump -U postgres -d aetheus --no-owner --no-acl > $inner" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  pg_dump failed - aborting reset to protect your data." -ForegroundColor Red
        & docker exec aetheus-local-database rm -f $inner 2>$null
        exit 1
    }
    & docker cp "aetheus-local-database:$inner" $snapshot 2>$null
    & docker exec aetheus-local-database rm -f $inner 2>$null
    if (-not (Test-Path $snapshot) -or (Get-Item $snapshot).Length -eq 0) {
        Write-Host "  Snapshot file is empty/missing - aborting reset to protect your data." -ForegroundColor Red
        if (Test-Path $snapshot) { Remove-Item $snapshot -Force }
        exit 1
    }
    $kb = [math]::Round((Get-Item $snapshot).Length / 1KB, 1)
    Write-Host "  Snapshot saved: $snapshot ($kb KB)" -ForegroundColor Green
    # Retention: keep only the 20 most recent snapshots.
    Get-ChildItem $dbSnapshotDir -Filter "aetheus-*.sql" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Reset-DevDb {
    Write-Step "Resetting local PostgreSQL (drop volume + recreate)..."
    Backup-DevDb
    & docker compose -f $devDbCompose down -v | Out-Host
    Ensure-DevDb
}

function Ensure-E2eDb {
    Write-Step "Ensuring dedicated E2E PostgreSQL (aetheus-e2e-database :15433) is running..."
    $running = & docker ps --filter "name=^/aetheus-e2e-database$" --format "{{.Names}}" 2>$null
    if ($running -ne "aetheus-e2e-database") {
        & docker compose -f $e2eDbCompose up -d | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Host "  docker compose up (E2E DB) failed." -ForegroundColor Red; exit 1 }
    }

    # Do not start the backend against a database that is still initialising. Docker Desktop can
    # take longer than the historical 15 seconds after a volume recreation, especially while a
    # VPS-sim image is also running. A failed backend boot makes every following E2E error noisy.
    for ($i = 0; $i -lt 120; $i++) {
        $health = & docker inspect --format '{{.State.Health.Status}}' aetheus-e2e-database 2>$null
        if ($health -eq "healthy") { Write-Host "  E2E database healthy." -ForegroundColor Green; return }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  E2E database not healthy after 60s - aborting before backend startup." -ForegroundColor Red
    & docker logs --tail 80 aetheus-e2e-database 2>$null | Out-Host
    exit 1
}

function Write-TestSummary([string]$summaryLine, [double]$elapsedSec) {
    $duration = [math]::Round($elapsedSec, 1)
    if ($summaryLine -match "total:\s*(\d+).*?failed:\s*(\d+).*?succeeded:\s*(\d+)") {
        $total   = [int]$Matches[1]
        $failed  = [int]$Matches[2]
        $passed  = [int]$Matches[3]
        $skipped = 0
        if ($summaryLine -match "skipped:\s*(\d+)") { $skipped = [int]$Matches[1] }
    } elseif ($summaryLine -match "Failed:\s*(\d+).*?Passed:\s*(\d+).*?Total:\s*(\d+)") {
        $failed  = [int]$Matches[1]
        $passed  = [int]$Matches[2]
        $total   = [int]$Matches[3]
        $skipped = 0
        if ($summaryLine -match "Skipped:\s*(\d+)") { $skipped = [int]$Matches[1] }
    } else {
        Write-Host "  Done in ${duration}s" -ForegroundColor DarkGray
        return
    }
    Write-Host ""
    Write-Host "  ----------------------------------------" -ForegroundColor DarkGray
    Write-Host "  Test summary  (${duration}s)" -ForegroundColor White
    Write-Host "  ----------------------------------------" -ForegroundColor DarkGray
    Write-Host "  Passed : $passed" -ForegroundColor Green
    if ($failed -gt 0) {
        Write-Host "  Failed : $failed" -ForegroundColor Red
    } else {
        Write-Host "  Failed : $failed" -ForegroundColor DarkGray
    }
    if ($skipped -gt 0) { Write-Host "  Skipped: $skipped" -ForegroundColor Yellow }
    Write-Host "  Total  : $total" -ForegroundColor White
    Write-Host "  ----------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
}

# One `dotnet test` run is split in three steps so that several of them can be in flight at once:
# Start-TestRun launches the process, Read-TestRunStream prints the per-test lines of a streamed run
# while it works, Complete-TestRun reads TRX, writes the failure log and prints the summary block once
# the process has exited. Wait-TestRuns drives them from the one launcher thread, so every block is
# printed whole, in the order the runs finish.
function Start-TestRun {
    param(
        [string[]]$ExtraArgs = @(),
        [string]$Label = "Running tests...",
        [string]$Target = "",
        [switch]$StreamOutput,
        # Where TRX and Cobertura land instead of the default (PLAN-015: the integration coverage goes
        # beside the product report, which merges exactly the three unit suites).
        [string]$ResultDirectory = "",
        # Prefixes the per-test lines of a streamed run when several runs share the console.
        [string]$Tag = ""
    )
    if (-not $Target) { $Target = $solution }

    $tmpOut = Join-Path $env:TEMP "aetheus_test_$(Get-Random).txt"
    $tmpErr = "$tmpOut.err"
    # `dotnet test` runs in Microsoft.Testing.Platform mode (global.json "test.runner"): the target is
    # named by --project or --solution, TRX comes from the TrxReport extension and the per-test lines
    # only appear with --output Detailed. Put both TRX and Cobertura outputs in the coverage directory
    # for -c, and pass --results-directory exactly once.
    $resultDirectory = if ($ResultDirectory) { $ResultDirectory } elseif ($Coverage) { $coverageDir } else { Join-Path $root "TestResults\Launcher" }
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $safeLabel = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $trxName = "{0}-{1:yyyyMMdd-HHmmssfff}.trx" -f $safeLabel, (Get-Date)
    $targetSwitch = if ($Target -match '\.slnx?$') { "--solution" } else { "--project" }
    $argList = @($targetSwitch, $Target, "--no-build", "--configuration", $buildConfiguration, "--no-progress",
                 "--report-trx", "--report-trx-filename", $trxName,
                 "--results-directory", $resultDirectory)
    if ($StreamOutput) { $argList += @("--output", "Detailed") }
    $argList += $ExtraArgs
    $fullArgs = @("test") + $argList
    # From the repository root: `dotnet test` reads the runner from the global.json of its working
    # directory, and anywhere outside the repository it would fall back to VSTest and refuse these arguments.
    $proc = Start-Process -FilePath "dotnet" -ArgumentList $fullArgs -WorkingDirectory $root -NoNewWindow -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    return @{
        Kind = "dotnet"; Label = $Label; Tag = $Tag; Process = $proc; Stream = $StreamOutput.IsPresent
        OutputPath = $tmpOut; ErrorPath = $tmpErr; ResultDirectory = $resultDirectory; TrxName = $trxName
        Stopwatch = [System.Diagnostics.Stopwatch]::StartNew(); Reader = $null
        StreamIndex = 0; StreamPassed = 0; StreamFailed = 0; StreamSkipped = 0; Result = $null
    }
}

function Read-TestRunStream([hashtable]$Run) {
    # Keep one shared reader at its current byte position. Re-running Get-Content against
    # the entire growing log every 500 ms made streaming I/O quadratic for long suites.
    if (-not $Run.Reader) {
        if (-not (Test-Path -LiteralPath $Run.OutputPath)) { return }
        $outputFile = [System.IO.File]::Open(
            $Run.OutputPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite)
        $Run.Reader = [System.IO.StreamReader]::new($outputFile)
    }
    $prefix = if ($Run.Tag) { "[$($Run.Tag)] " } else { "" }
    while ($null -ne ($line = $Run.Reader.ReadLine())) {
        # Microsoft.Testing.Platform, --output Detailed: "passed <test> (209ms)", then a "from <dll>" line.
        # VSTest console logger at normal verbosity (the E2E browser container): "  Passed <test> [209 ms]".
        if ($line -match '^(passed|failed|skipped)\s+(.+?)\s+\(([^()]+)\)\s*$') {
            $status = $Matches[1]; $testName = $Matches[2]; $duration = $Matches[3].Trim()
        } elseif ($line -cmatch '^\s+(Passed|Failed|Skipped)\s+(.+?)(?:\s+\[([^\[\]]+)\])?\s*$') {
            $status = $Matches[1].ToLowerInvariant(); $testName = $Matches[2]; $duration = "$($Matches[3])".Trim()
        } else { continue }
        $Run.StreamIndex++
        $idx = "$($Run.StreamIndex)".PadLeft(2, '0')
        if ($status -eq 'passed') {
            $Run.StreamPassed++
            Write-Host "  ${prefix}[${idx}] PASS " -NoNewline -ForegroundColor Green
            Write-Host "$testName " -NoNewline
            Write-Host "($duration)" -ForegroundColor DarkGray
        } elseif ($status -eq 'skipped') {
            $Run.StreamSkipped++
            Write-Host "  ${prefix}[${idx}] SKIP " -NoNewline -ForegroundColor Yellow
            Write-Host "$testName " -NoNewline -ForegroundColor Yellow
            Write-Host "($duration)" -ForegroundColor DarkGray
        } else {
            $Run.StreamFailed++
            Write-Host "  ${prefix}[${idx}] FAIL " -NoNewline -ForegroundColor Red
            Write-Host "$testName " -NoNewline -ForegroundColor Red
            Write-Host "($duration)" -ForegroundColor DarkGray
        }
    }
}

function Complete-TestRun([hashtable]$Run) {
    $Run.Process.WaitForExit()
    if ($Run.Container) { Copy-E2eContainerResults $Run }
    try {
        if ($Run.Stream) { Read-TestRunStream $Run }
    } finally {
        if ($Run.Reader) { $Run.Reader.Dispose(); $Run.Reader = $null }
    }
    Write-Host ""
    $Run.Stopwatch.Stop()
    $exitCode = $Run.Process.ExitCode
    $resultDirectory = $Run.ResultDirectory
    $tmpOut = $Run.OutputPath
    $tmpErr = $Run.ErrorPath
    $trxPath = Join-Path $resultDirectory $Run.TrxName
    $trxCounters = $null
    # A container run's verdict is the exit code of `dotnet test` in it plus the TRX copied out: without the TRX
    # nothing proves the tests ran, so a "successful" container that left none is a failure, not a pass.
    if ($Run.Container -and -not (Test-Path $trxPath) -and $exitCode -eq 0) {
        Write-Host "  The container exited 0 but left no TRX ($($Run.ContainerTrx)): the run is not accepted." -ForegroundColor Red
        $exitCode = 1
    }
    if (Test-Path $trxPath) {
        Write-Host "  TRX: $trxPath" -ForegroundColor DarkGray
        try {
            [xml]$trxDocument = Get-Content -Raw $trxPath
            $trxCounters = $trxDocument.TestRun.ResultSummary.Counters
        } catch {
            Write-Warning "Could not read authoritative TRX counters from '$trxPath': $($_.Exception.Message)"
        }
    }

    $lines = @(Get-Content $tmpOut -ErrorAction SilentlyContinue) + @(Get-Content $tmpErr -ErrorAction SilentlyContinue)
    if ($exitCode -ne 0) {
        $failureDirectory = Join-Path $root "TestResults\LauncherFailures"
        New-Item -ItemType Directory -Path $failureDirectory -Force | Out-Null
        $failureLog = Join-Path $failureDirectory ("dotnet-test-{0:yyyyMMdd-HHmmssfff}.log" -f (Get-Date))
        $lines | Set-Content -Path $failureLog -Encoding utf8
        Write-Host "  dotnet test exited with code $exitCode. Full log: $failureLog" -ForegroundColor Red
        $lines | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    Remove-Item $tmpOut, $tmpErr -Force -ErrorAction SilentlyContinue

    # Without a TRX, fall back on the console summary Microsoft.Testing.Platform prints last: one
    # counter per line ("total: N", "failed: N", "succeeded: N", "skipped: N").
    $summaryLine = $null
    $consoleCounters = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*(total|failed|succeeded|skipped):\s*(\d+)\s*$') { $consoleCounters[$Matches[1]] = [int]$Matches[2] }
    }
    if ($consoleCounters.ContainsKey('total') -and $consoleCounters.ContainsKey('failed') -and $consoleCounters.ContainsKey('succeeded')) {
        $consoleSkipped = if ($consoleCounters.ContainsKey('skipped')) { $consoleCounters['skipped'] } else { 0 }
        $summaryLine = "Test summary: total: $($consoleCounters['total']); failed: $($consoleCounters['failed']); succeeded: $($consoleCounters['succeeded']); skipped: $consoleSkipped"
    }
    $expectedTotal = 0
    if ($trxCounters) {
        # The live console stream is diagnostic only: redirected output can expose its final line
        # after the process-exit edge and previously made a fully green run look incomplete. TRX is
        # finalized once by the test run and is therefore the authoritative completion record.
        $trxTotal = [int]$trxCounters.total
        $trxFailed = [int]$trxCounters.failed
        $trxPassed = [int]$trxCounters.passed
        $trxSkipped = $trxTotal - $trxFailed - $trxPassed
        $summaryLine = "Test summary: total: ${trxTotal}; failed: ${trxFailed}; succeeded: ${trxPassed}; skipped: ${trxSkipped}"
        $expectedTotal = $trxTotal
    } elseif (-not $summaryLine -and $Run.Stream -and ($Run.StreamPassed + $Run.StreamFailed + $Run.StreamSkipped) -gt 0) {
        $completedTotal = $Run.StreamPassed + $Run.StreamFailed + $Run.StreamSkipped
        $summaryLine = "Test summary: total: ${completedTotal}; failed: $($Run.StreamFailed); succeeded: $($Run.StreamPassed); skipped: $($Run.StreamSkipped)"
    }
    Write-TestSummary -summaryLine "$summaryLine" -elapsedSec $Run.Stopwatch.Elapsed.TotalSeconds

    $passed = 0; $failed = 0; $total = 0; $skipped = 0
    if ($summaryLine -match "total:\s*(\d+).*?failed:\s*(\d+).*?succeeded:\s*(\d+)") {
        $total  = [int]$Matches[1]; $failed = [int]$Matches[2]; $passed = [int]$Matches[3]
        if ($summaryLine -match "skipped:\s*(\d+)") { $skipped = [int]$Matches[1] }
    } elseif ($Run.Stream -and ($Run.StreamPassed + $Run.StreamFailed) -gt 0) {
        $passed = $Run.StreamPassed; $failed = $Run.StreamFailed; $skipped = $Run.StreamSkipped
        $total = $Run.StreamPassed + $Run.StreamFailed + $Run.StreamSkipped
    }
    return @{
        Label = $Run.Label
        ExitCode = $exitCode
        Passed = $passed
        Failed = $failed
        Total = $total
        ExpectedTotal = $expectedTotal
        Skipped = $skipped
        ElapsedSec = $Run.Stopwatch.Elapsed.TotalSeconds
    }
}

function Start-NodeTestRun {
    # PLAN-002 lot 9.T: the JavaScript suites (tests/js/deploy/*.test.mjs, tests/js/*) run in the CI and
    # quality pipelines but never ran locally. Runs `node --test` with the TAP reporter over the given
    # files; Complete-NodeTestRun reads its final counters. A skipped test fails the run, like the quality
    # stage that demands `# skipped 0`: the PowerShell-driven suites skip themselves when pwsh is missing.
    param(
        [string[]]$Files,
        [string]$Label
    )
    $unlaunched = {
        param([string]$why)
        Write-Host "  $why" -ForegroundColor Red
        @{ Kind = "node"; Label = $Label; Process = $null; Stream = $false
           Result = @{ Label = $Label; ExitCode = 1; Passed = 0; Failed = 0; Total = 0; ExpectedTotal = 0; Skipped = 0; ElapsedSec = 0 } }
    }
    $nodeExe = Get-Command node -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if (-not $nodeExe) { return & $unlaunched "node not found on PATH: the JavaScript suites cannot run." }
    if (-not $Files -or $Files.Count -eq 0) { return & $unlaunched "No JavaScript test file found." }
    # The deploy-script suites drive POSIX shells (`sh -c`, `bash`), as on the Linux CI agent. A Windows
    # PowerShell PATH has no `sh` and its `bash` is WSL's: put Git for Windows' usr\bin first for the
    # node process only, the same tools a Git Bash prompt resolves.
    $savedPath = $env:PATH
    $posixBin = $null
    $gitExe = Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if ($gitExe) {
        $candidate = Join-Path (Split-Path (Split-Path $gitExe)) "usr\bin"
        if (Test-Path (Join-Path $candidate "sh.exe")) { $posixBin = $candidate }
    }
    if (-not $posixBin -and -not (Get-Command sh -ErrorAction SilentlyContinue)) {
        return & $unlaunched "No POSIX sh found (Git for Windows usr\bin): the deploy-script suites cannot run."
    }
    $tmpOut = Join-Path $env:TEMP "aetheus_node_test_$(Get-Random).txt"
    $tmpErr = "$tmpOut.err"
    try {
        if ($posixBin) { $env:PATH = "$posixBin;$savedPath" }
        $proc = Start-Process -FilePath $nodeExe -ArgumentList (@("--test", "--test-reporter=tap") + $Files) -WorkingDirectory $root -NoNewWindow -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    } finally {
        $env:PATH = $savedPath
    }
    return @{
        Kind = "node"; Label = $Label; Tag = ""; Process = $proc; Stream = $false; Files = $Files
        OutputPath = $tmpOut; ErrorPath = $tmpErr; Stopwatch = [System.Diagnostics.Stopwatch]::StartNew(); Result = $null
    }
}

function Complete-NodeTestRun([hashtable]$Run) {
    $Run.Process.WaitForExit()
    $Run.Stopwatch.Stop()
    $Files = $Run.Files
    $exitCode = $Run.Process.ExitCode
    $lines = @(@(Get-Content $Run.OutputPath -ErrorAction SilentlyContinue) + @(Get-Content $Run.ErrorPath -ErrorAction SilentlyContinue) | ForEach-Object { "$_" })
    Remove-Item $Run.OutputPath, $Run.ErrorPath -Force -ErrorAction SilentlyContinue
    $counters = @{}
    foreach ($line in $lines) {
        if ($line -match '^# (tests|pass|fail|cancelled|skipped|todo) (\d+)\s*$') { $counters[$Matches[1]] = [int]$Matches[2] }
    }
    $total = if ($counters.ContainsKey('tests')) { $counters['tests'] } else { 0 }
    $passed = if ($counters.ContainsKey('pass')) { $counters['pass'] } else { 0 }
    $failed = 0
    foreach ($key in 'fail', 'cancelled') { if ($counters.ContainsKey($key)) { $failed += $counters[$key] } }
    $skipped = 0
    foreach ($key in 'skipped', 'todo') { if ($counters.ContainsKey($key)) { $skipped += $counters[$key] } }
    # Tests that need POSIX file modes, symbolic links and FIFOs skip themselves on NTFS: they are run
    # for real in a Linux container instead, and only their passing there accounts for the skips.
    $posixSkips = @($lines | Where-Object { $_ -match '# SKIP needs POSIX file modes' })
    if ($exitCode -eq 0 -and $skipped -gt 0 -and $posixSkips.Count -eq $skipped -and $IsWindows) {
        $posixFiles = @($Files | Where-Object { Select-String -Path (Join-Path $root $_) -SimpleMatch 'needs POSIX file modes' -Quiet })
        Write-Host "  $skipped POSIX-only JavaScript test(s) skipped on Windows: running $($posixFiles.Count) file(s) in a Linux container..." -ForegroundColor Cyan
        $relative = @($posixFiles | ForEach-Object { $_.Replace('\', '/') })
        $posixLines = @(& docker run --rm --user 1000 -v "${root}:/w" -w /w node:22 node --test --test-reporter=tap @relative 2>&1 | ForEach-Object { "$_" })
        $posixExit = $LASTEXITCODE
        $posixRan = @($posixLines | Where-Object { $_ -match '^# skipped 0\s*$' }).Count -eq 1
        if ($posixExit -eq 0 -and $posixRan) {
            Write-Host "  POSIX-only tests passed in the Linux container." -ForegroundColor Green
            $skipped = 0
        } else {
            $lines += $posixLines
            Write-Host "  POSIX-only tests failed or did not all run in the Linux container (docker exit $posixExit)." -ForegroundColor Red
        }
    }
    if ($exitCode -eq 0 -and $skipped -gt 0) {
        Write-Host "  $skipped JavaScript test(s) skipped: every suite must run (is pwsh on PATH?)." -ForegroundColor Red
        $exitCode = 1
    }
    if ($exitCode -ne 0) {
        $failureDirectory = Join-Path $root "TestResults\LauncherFailures"
        New-Item -ItemType Directory -Path $failureDirectory -Force | Out-Null
        $failureLog = Join-Path $failureDirectory ("node-test-{0:yyyyMMdd-HHmmssfff}.log" -f (Get-Date))
        $lines | Set-Content -Path $failureLog -Encoding utf8
        $tool = if ($Run.Tool) { $Run.Tool } else { "node --test" }
        Write-Host "  $tool exited with code $exitCode. Full log: $failureLog" -ForegroundColor Red
        $lines | Where-Object { $_ -match '^\s*not ok ' } | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    Write-TestSummary -summaryLine "Test summary: total: ${total}; failed: ${failed}; succeeded: ${passed}; skipped: ${skipped}" -elapsedSec $Run.Stopwatch.Elapsed.TotalSeconds
    return @{
        Label = $Run.Label
        ExitCode = $exitCode
        Passed = $passed
        Failed = $failed
        Total = $total
        ExpectedTotal = $total
        Skipped = $skipped
        ElapsedSec = $Run.Stopwatch.Elapsed.TotalSeconds
    }
}

# Waits for every run, printing the block of each (header, TRX, summary) as soon as that run ends. The
# blocks come one at a time from this thread, so output never interleaves; a streamed run (E2E) prints its
# per-test lines as they arrive, each tagged with its shard. Returns the results in the order given.
function Wait-TestRuns {
    param(
        [hashtable[]]$Runs,
        # Parallel runs have no header printed before they start: print it with the block.
        [switch]$HeaderWithBlock
    )
    $pending = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($run in $Runs) {
        if ($run.Result) {
            # Never launched (node or POSIX shell missing): the reason is already printed.
            if ($HeaderWithBlock) { Write-Step $run.Label }
        } else { $pending.Add($run) }
    }
    $wall = [System.Diagnostics.Stopwatch]::StartNew()
    $lastStatusAt = 0
    while ($pending.Count -gt 0) {
        foreach ($run in @($pending)) {
            if ($run.Stream) { Read-TestRunStream $run }
            if (-not $run.Process.HasExited) { continue }
            if ($HeaderWithBlock) { Write-Step $run.Label }
            $run.Result = if ($run.Kind -eq "node") { Complete-NodeTestRun $run } else { Complete-TestRun $run }
            [void]$pending.Remove($run)
        }
        if ($pending.Count -eq 0) { break }
        if ($Runs.Count -eq 1) {
            if (-not $Runs[0].Stream) {
                Write-Host "`r  Elapsed: $([int]$wall.Elapsed.TotalSeconds)s   " -NoNewline -ForegroundColor DarkGray
            }
        } elseif ($wall.Elapsed.TotalSeconds - $lastStatusAt -ge 30) {
            $lastStatusAt = $wall.Elapsed.TotalSeconds
            $names = ($pending | ForEach-Object { $_.Label -replace '^Running ', '' -replace '\.\.\.$', '' }) -join '; '
            Write-Host "  [parallel $([int]$wall.Elapsed.TotalSeconds)s] still running: $names" -ForegroundColor DarkGray
        }
        Start-Sleep -Milliseconds 500
    }
    return @($Runs | ForEach-Object { $_.Result })
}

function Start-SuiteRun([hashtable]$Suite) {
    if ($Suite.Kind -eq "node") { return Start-NodeTestRun -Files $Suite.Files -Label $Suite.Title }
    if ($Suite.Kind -eq "agent-container") { return Start-AgentSuiteContainerRun -Label $Suite.Title -ExtraArgs $Suite.ExtraArgs }
    if ($Suite.Kind -eq "js-lint-container") { return Start-JsLintContainerRun -Label $Suite.Title }
    $parameters = @{ Label = $Suite.Title; Target = $Suite.Target; ExtraArgs = $Suite.ExtraArgs }
    if ($Suite.ResultDirectory) { $parameters.ResultDirectory = $Suite.ResultDirectory }
    return Start-TestRun @parameters
}
# Runs the unit-level suites and returns @{ Results = ...; Failed = ... } (results in the order given, each
# named by its suite). All the suites start together and each prints its whole block when it ends, so a failed
# suite never hides the others: every suite finishes and every failure is named. The suites that start the real
# back end in Development (Back, Integration) run beside the rest because the back end stages the Windows agent
# package in the temp directory, not under src/Aetheus.Back/wwwroot/downloads that the source-scanning guards
# walk (they met a folder that vanished mid-scan, seen: Front.Tests NoSourceFile_NamesTheRemovedLibrary).
# After a failure the caller skips the stages that follow. With -Sequential (coverage, where coverlet
# instruments one suite at a time) every suite is its own batch.
function Invoke-TestSuites {
    param(
        [hashtable[]]$Suites,
        [switch]$Sequential
    )
    $results = [System.Collections.Generic.List[hashtable]]::new()
    $failed = [System.Collections.Generic.List[hashtable]]::new()
    $wall = [System.Diagnostics.Stopwatch]::StartNew()

    $batches = [System.Collections.Generic.List[object[]]]::new()
    if ($Sequential) {
        foreach ($suite in $Suites) { $batches.Add(@($suite)) }
    } else {
        $batches.Add(@($Suites))
    }

    foreach ($batch in $batches) {
        if ($batch.Count -eq 1) {
            Write-Step $batch[0].Title
        } else {
            Write-Step "Running $($batch.Count) test suites in parallel: $(($batch | ForEach-Object { $_.Name }) -join ', ')"
        }
        $runs = @($batch | ForEach-Object { Start-SuiteRun $_ })
        $outcomes = @(Wait-TestRuns -Runs $runs -HeaderWithBlock:($batch.Count -gt 1))
        $batchFailed = $false
        for ($i = 0; $i -lt $batch.Count; $i++) {
            $outcomes[$i].Name = $batch[$i].Name
            $results.Add($outcomes[$i])
            if ($outcomes[$i].ExitCode -ne 0) {
                $batchFailed = $true
                $failed.Add($batch[$i])
                Write-Host "$($batch[$i].Failure)." -ForegroundColor Red
            }
        }
        if ($batchFailed) { break }
    }
    $wall.Stop()
    if ($results.Count -gt 1) {
        $sum = ($results | ForEach-Object { $_.ElapsedSec } | Measure-Object -Sum).Sum
        Write-Host ""
        Write-Host "  Suites finished in $([math]::Round($wall.Elapsed.TotalSeconds, 1))s ($([math]::Round($sum, 1))s if run one after the other):" -ForegroundColor DarkGray
        foreach ($result in $results) {
            Write-Host ("    {0,-18} {1,7:N1}s" -f $result.Name, $result.ElapsedSec) -ForegroundColor DarkGray
        }
    }
    return @{ Results = $results.ToArray(); Failed = $failed.ToArray() }
}

function Invoke-CoverageReport {
    Write-Step "Generating HTML coverage report..."
    # coverlet.MTP writes coverage.cobertura.<timestamp>.xml at the top of the results directory. Give
    # each report the <id>/coverage.cobertura.xml layout the CI readers expect (as
    # scripts/normalize-coverage-report.sh does there), then merge exactly the three suites.
    Get-ChildItem -Path $coverageDir -Filter "coverage.cobertura.*.xml" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $stampDirectory = Join-Path $coverageDir ($_.BaseName -replace '^coverage\.cobertura\.', '')
            New-Item -ItemType Directory -Path $stampDirectory -Force | Out-Null
            Move-Item -Path $_.FullName -Destination (Join-Path $stampDirectory "coverage.cobertura.xml")
        }
    $xmlFiles = @(Get-ChildItem -Path $coverageDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Parent.FullName -eq $coverageDir })
    if ($xmlFiles.Count -ne 3) {
        throw "Expected exactly 3 product coverage reports in $coverageDir, found $($xmlFiles.Count)."
    }
    Write-Host "  Found $($xmlFiles.Count) coverage file(s)" -ForegroundColor DarkGray
    if (Test-Path $reportDir) { Remove-Item $reportDir -Recurse -Force }
    $reportFiles = ($xmlFiles | ForEach-Object { $_.FullName }) -join ";"

    Push-Location $root
    try {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }
        & dotnet tool run reportgenerator -- "-reports:$reportFiles" "-targetdir:$reportDir" "-reporttypes:Html;Cobertura" "-assemblyfilters:+Aetheus.Back;+Aetheus.Front;+Aetheus.Agent.Core" "-verbosity:Warning"
        if ($LASTEXITCODE -ne 0) { throw "ReportGenerator failed." }
    }
    finally {
        Pop-Location
    }

    $mergedReport = Join-Path $reportDir "Cobertura.xml"
    if (-not (Test-Path $mergedReport)) { throw "Merged Cobertura report was not produced." }
    [xml]$coverageXml = Get-Content -Raw $mergedReport
    $linesCovered = [long]$coverageXml.coverage.'lines-covered'
    $linesValid = [long]$coverageXml.coverage.'lines-valid'
    if ($linesValid -le 0 -or $linesCovered -lt 0 -or $linesCovered -gt $linesValid) {
        throw "Merged Cobertura report contains invalid line totals."
    }
    $lineRate = 100.0 * $linesCovered / $linesValid
    Write-Host ("  Product line coverage: {0:N2}% ({1:N0}/{2:N0})" -f $lineRate, $linesCovered, $linesValid) -ForegroundColor Cyan
    # 80.1% is the floor the 2026-08-19 campaign reached and that this project commits to holding.
    # The margin above it is thin (measured 80,14%), so a commit that deletes tests without replacing
    # their coverage fails here rather than in CI an hour later.
    if ($lineRate -lt 80.1) {
        throw ("Product line coverage is below the required 80.1%: {0:N2}%" -f $lineRate)
    }

    $indexHtml = Join-Path $reportDir "index.html"
    if (Test-Path $indexHtml) {
        Write-Host "  Coverage report: $indexHtml" -ForegroundColor Green
        Start-Process $indexHtml
    }
}

# PLAN-002 lot 2.final (STD-CRAP): the kit's CRAP gate, run once the coverage is green. It scores every
# Cobertura report of this run (the three unit suites and the integration suite, under a coverage folder
# emptied at the start of -c) against .config/crap-exceptions.json. Returns $false when it fails.
function Invoke-CrapGate([string]$GatePath, [string]$CoverageRoot, [string]$ScoresPath) {
    Write-Step "Running the CRAP gate..."
    try {
        & $GatePath -CoverageRoot $CoverageRoot -ReportPath $ScoresPath | Out-Null
        Write-Host "  Method scores: $ScoresPath" -ForegroundColor DarkGray
        return $true
    }
    catch {
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Method scores: $ScoresPath" -ForegroundColor DarkGray
        return $false
    }
}

# ---------- E2E browser container ----------

# The E2E suite runs in the browser image the candidate's QA runs it in (deploy/docker/Dockerfile.e2e: the pinned
# Playwright noble image, Chromium only, user pwuser), never in a browser of the Windows host. The host resolves
# OmniEurope's system-ui font stack to Segoe UI, the QA image to its own Linux fonts, so a layout measured on the
# host was not the layout QA measured (candidate 2956). There is no fallback to a host browser: a local result has
# to be the result the CI gets. The stacks stay on the host; the container reaches them through Docker Desktop's
# host.docker.internal, which forwards to the host's loopback, where the backend and the front listen.
$e2eContainerHost = "host.docker.internal"
# CI builds the suite in Release (aetheus-ci.yaml) and the image refuses an output that is not under bin/Release.
$e2eImageConfiguration = "Release"
$e2eCheckoutId = (Split-Path $root -Leaf).ToLowerInvariant() -replace '[^a-z0-9_.-]', '-'
$e2eImageTag = "aetheus-browser-smoke-local:$e2eCheckoutId"
$e2eImageContext = Join-Path $root "TestResults\YLaunchE2eImageContext"
$e2eContainerResults = "/tmp/aetheus-e2e-results"
# The run-qa-e2e-suite function's runner script (E2eRun.RunnerScript), with two local differences: the console
# logger is at normal verbosity so the launcher can stream one line per test, and the script exits with the
# code of `dotnet test` (QA records it in raw-exit-code and grades the TRX itself; this script records it too).
$e2eRunnerScript = @'
set -u
RESULTS=/tmp/aetheus-e2e-results
rm -rf "$RESULTS"
mkdir -p "$RESULTS"
RAW_EXIT=0
if [ -n "$E2E_TEST_FILTER" ]; then set -- --filter "$E2E_TEST_FILTER"; else set --; fi
dotnet test "$E2E_TEST_PROJECT" -c Release --no-build --no-restore "$@" \
  --logger "console;verbosity=normal" --logger "trx;LogFileName=e2e.trx" \
  --results-directory "$RESULTS" --nologo \
  -- NUnit.NumberOfTestWorkers="$E2E_WORKERS" || RAW_EXIT=$?
printf "%s\n" "$RAW_EXIT" > "$RESULTS/raw-exit-code"
exit "$RAW_EXIT"
'@ -replace "`r`n", "`n"

# Builds the browser image from deploy/docker/Dockerfile.e2e exactly as CI's "Build the browser smoke image" does:
# the suite compiled in Release, then a context holding only the root build files, the project, its bin output
# (the Playwright driver kept for linux-x64 only) and its restore outputs. Docker's layer cache reuses the image
# while that context is unchanged.
function Build-E2eRunnerImage {
    $e2eProject = Join-Path $root "tests\Aetheus.E2E\Aetheus.E2E.csproj"
    # Always, even after a Release solution build: a build on Windows copies only the Windows Playwright driver,
    # and the image runs the linux-x64 one (CI builds on Linux, where that is the default).
    Write-Step "Building the E2E suite in $e2eImageConfiguration with the linux-x64 Playwright driver (as CI does)..."
    dotnet build $e2eProject --configuration $e2eImageConfiguration -p:PlaywrightPlatform=linux-x64 --maxcpucount:1 -p:UseSharedCompilation=false -nodeReuse:false | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "  E2E suite build failed." -ForegroundColor Red; return $false }
    Write-Step "Building the QA browser image $e2eImageTag (deploy\docker\Dockerfile.e2e)..."
    if (Test-Path -LiteralPath $e2eImageContext) { Remove-Item -LiteralPath $e2eImageContext -Recurse -Force }
    $contextProject = Join-Path $e2eImageContext "tests\Aetheus.E2E"
    $contextBin = Join-Path $contextProject "bin\$e2eImageConfiguration"
    $contextObj = Join-Path $contextProject "obj"
    New-Item -ItemType Directory -Path $contextBin, $contextObj -Force | Out-Null
    foreach ($file in @("Directory.Build.props", "Directory.Packages.props", "NuGet.config")) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $e2eImageContext
    }
    Copy-Item -LiteralPath $e2eProject -Destination $contextProject
    $suiteOutput = Join-Path $root "tests\Aetheus.E2E\bin\$e2eImageConfiguration\net10.0"
    if (-not (Test-Path -LiteralPath (Join-Path $suiteOutput "Aetheus.E2E.dll"))) {
        Write-Host "  The E2E suite output is missing: $suiteOutput" -ForegroundColor Red
        return $false
    }
    Copy-Item -LiteralPath $suiteOutput -Destination $contextBin -Recurse
    Get-ChildItem -LiteralPath (Join-Path $contextBin "net10.0\.playwright\node") -Directory |
        Where-Object Name -ne "linux-x64" | Remove-Item -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $contextBin "net10.0\.playwright\node\linux-x64\node"))) {
        Write-Host "  The Playwright driver for linux-x64 is missing from the E2E suite output." -ForegroundColor Red
        return $false
    }
    $suiteObj = Join-Path $root "tests\Aetheus.E2E\obj"
    Copy-Item -LiteralPath (Join-Path $suiteObj "project.assets.json") -Destination $contextObj
    Get-ChildItem -LiteralPath $suiteObj -File | Where-Object { $_.Name -like "*.nuget.g.props" -or $_.Name -like "*.nuget.g.targets" } |
        Copy-Item -Destination $contextObj
    $revision = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
    $buildOutput = & docker build --label "org.opencontainers.image.revision=$revision" `
        -f (Join-Path $root "deploy\docker\Dockerfile.e2e") -t $e2eImageTag $e2eImageContext 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "  docker build of the QA browser image failed (exit $LASTEXITCODE)." -ForegroundColor Red
        return $false
    }
    Write-Host "  Browser image ready: $e2eImageTag ($(& docker image inspect --format '{{.Id}}' $e2eImageTag))." -ForegroundColor Green
    return $true
}

# Starts one E2E run in a container of the browser image, against one host stack: the container is created with
# the environment QA's suite container gets (the URLs as the container reaches the stack, the admin account,
# the workers, the filter, the project and results paths), the runner script is copied in, and `docker start -a`
# streams its output to the launcher like a host `dotnet test` would. Complete-TestRun copies the results out of
# the stopped container (as QA does with docker cp) and removes it.
function Start-E2eContainerRun {
    param([hashtable]$Stack, [string[]]$ExtraArgs = @(), [string]$Label, [string]$Tag = "")
    $resultDirectory = Join-Path $root "TestResults\Launcher"
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $safeLabel = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $stamp = "{0:yyyyMMdd-HHmmssfff}" -f (Get-Date)
    $trxName = "$safeLabel-$stamp.trx"
    $container = "aetheus-ylaunch-e2e-$e2eCheckoutId-$($Stack.Index)"
    & docker rm -f $container 2>$null | Out-Null
    $filter = if ($ExtraArgs.Count -ge 2 -and $ExtraArgs[0] -eq "--filter") { $ExtraArgs[1] } else { "" }
    $workers = if ($env:AETHEUS_E2E_WORKERS) { $env:AETHEUS_E2E_WORKERS } else { "1" }
    $environment = [ordered]@{
        E2E_FRONTEND_URL = $Stack.ContainerFrontUrl
        E2E_BACKEND_URL = $Stack.ContainerApiBaseUrl
        E2E_WORKERS = $workers
        E2E_TEST_FILTER = $filter
        E2E_TEST_PROJECT = "tests/Aetheus.E2E/Aetheus.E2E.csproj"
        AETHEUS_E2E_RESULTS_DIR = $e2eContainerResults
        E2E_REQUIRE_FRONTEND_SECURITY_HEADERS = $env:E2E_REQUIRE_FRONTEND_SECURITY_HEADERS
        AETHEUS_PAGE_RULES_SWEEP = $env:AETHEUS_PAGE_RULES_SWEEP
        # The manual sweep's report and proposed baseline land with the results, which are copied out.
        AETHEUS_PAGE_RULES_OUTPUT = "$e2eContainerResults/page-rules"
        DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        DOTNET_NOLOGO = "1"
    }
    # Values a developer may set on the host for a run; QA sets its own.
    foreach ($name in @("E2E_ADMIN_USER", "E2E_ADMIN_EMAIL", "E2E_ADMIN_PASSWORD", "E2E_PRESERVE_DATABASE", "E2E_PREVIOUS_PAYLOAD_ONLY", "E2E_APP_READY_TIMEOUT_MS")) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $environment[$name] = $value }
    }
    $createArgs = @("create", "--name", $container)
    foreach ($key in $environment.Keys) { $createArgs += @("-e", "$key=$($environment[$key])") }
    $createArgs += @($e2eImageTag, "sh", "/tmp/ylaunch-e2e-run.sh")
    $shown = ($createArgs | ForEach-Object { $_ -replace '^(E2E_ADMIN_PASSWORD=).+$', '$1***' }) -join ' '
    Write-Host "  Container: docker $shown" -ForegroundColor DarkGray
    & docker @createArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker create of the E2E container $container failed (exit $LASTEXITCODE)." }
    $scriptFile = Join-Path $env:TEMP "ylaunch-e2e-run-$(Get-Random).sh"
    [System.IO.File]::WriteAllText($scriptFile, $e2eRunnerScript)
    & docker cp $scriptFile "${container}:/tmp/ylaunch-e2e-run.sh" | Out-Null
    $copied = $LASTEXITCODE
    Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    if ($copied -ne 0) { throw "docker cp of the E2E runner script into $container failed (exit $copied)." }

    $tmpOut = Join-Path $env:TEMP "aetheus_test_$(Get-Random).txt"
    $tmpErr = "$tmpOut.err"
    $proc = Start-Process -FilePath "docker" -ArgumentList @("start", "-a", $container) -WorkingDirectory $root `
        -NoNewWindow -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    return @{
        Kind = "dotnet"; Label = $Label; Tag = $Tag; Process = $proc; Stream = $true
        OutputPath = $tmpOut; ErrorPath = $tmpErr; ResultDirectory = $resultDirectory; TrxName = $trxName
        Container = $container; ContainerEvidence = Join-Path $resultDirectory "$safeLabel-$stamp"
        ContainerResultsPath = $e2eContainerResults; ContainerTrx = "e2e.trx"
        Stopwatch = [System.Diagnostics.Stopwatch]::StartNew(); Reader = $null
        StreamIndex = 0; StreamPassed = 0; StreamFailed = 0; StreamSkipped = 0; Result = $null
    }
}

# The stopped container's results (TRX, raw exit code, accessibility SARIF), copied out as QA copies them; the TRX
# is then put where Complete-TestRun reads it, and the container removed. Shared by the E2E and the agent-suite
# containers: the run names its results folder inside the container and the TRX it holds.
function Copy-E2eContainerResults([hashtable]$Run) {
    New-Item -ItemType Directory -Path $Run.ContainerEvidence -Force | Out-Null
    & docker cp "$($Run.Container):$($Run.ContainerResultsPath)/." $Run.ContainerEvidence 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  docker cp of the results out of $($Run.Container) failed (exit $LASTEXITCODE)." -ForegroundColor Red
    }
    $trx = Join-Path $Run.ContainerEvidence $Run.ContainerTrx
    if (Test-Path -LiteralPath $trx) { Copy-Item -LiteralPath $trx -Destination (Join-Path $Run.ResultDirectory $Run.TrxName) }
    if ($Run.CopyCoverage) {
        # coverlet.MTP names the sources as the container saw them (/work/...): rewrite that prefix to this checkout so
        # the merged report and the CRAP gate read the same files whichever OS ran the suite.
        $hostRoot = ($root.TrimEnd('\', '/') -replace '\\', '/') + "/"
        foreach ($report in @(Get-ChildItem -LiteralPath $Run.ContainerEvidence -Filter "coverage.cobertura.*.xml" -File -ErrorAction SilentlyContinue)) {
            $text = [System.IO.File]::ReadAllText($report.FullName).Replace("$agentSuiteWorkDir/", $hostRoot)
            [System.IO.File]::WriteAllText((Join-Path $Run.ResultDirectory $report.Name), $text, [System.Text.UTF8Encoding]::new($false))
        }
    }
    if ($Run.PlatformFile) {
        $platform = Join-Path $Run.ContainerEvidence $Run.PlatformFile
        if (Test-Path -LiteralPath $platform) { Write-Host "  Ran in: $((Get-Content -LiteralPath $platform -Raw).Trim())" -ForegroundColor Cyan }
    }
    Write-Host "  Container results: $($Run.ContainerEvidence)" -ForegroundColor DarkGray
    & docker rm -f $Run.Container 2>$null | Out-Null
}

# ---------- Agent suite container ----------

# The agent unit suite (tests/Aetheus.Agent.Core.Tests) runs on Linux in the CI, and several of its tests read the
# machine they run on (the real /etc/apache2, file modes, POSIX shells): on the Windows host it passed while the
# candidate 2961 CI failed it. The launcher therefore runs it in a Linux container of the SDK image the release builds
# use (deploy/docker/Dockerfile.back, the build-source stage), in Release like the CI, and never on the host. There is
# no fallback: without a usable Docker the suite does not run and the launcher fails.
#
# The checkout is mounted read-only and copied into the container's own filesystem (bin/obj/.git and the heavy
# folders left out), so the build of the container never touches the host's Windows bin/obj or restore outputs.
# The NuGet cache is a named volume, so only the first run downloads the packages.
$agentSuiteWorkDir = "/work"
$agentSuiteResults = "/tmp/aetheus-agent-results"
$agentSuiteNuGetVolume = "aetheus-ylaunch-nuget"
$agentSuiteRunnerScript = @'
set -u
RESULTS=/tmp/aetheus-agent-results
rm -rf "$RESULTS" /work /tmp/src.tar
mkdir -p "$RESULTS" /work
{ uname -sr; dotnet --version; } | tr '\n' ' ' > "$RESULTS/platform.txt"
echo "Copying the checkout into the container..."
# wwwroot/downloads holds the agent packages -ta publishes while this copy runs (git-ignored, not read by the suite):
# a package still being written cannot be opened and would fail the copy.
tar -C /src -cf /tmp/src.tar --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules \
  --exclude=./TestResults --exclude=./.claude/worktrees --exclude=./.worktrees --exclude=./src/Aetheus.Back/wwwroot/downloads . || { echo "Could not read the checkout." >&2; exit 1; }
tar -C /work -xf /tmp/src.tar || { echo "Could not copy the checkout." >&2; exit 1; }
rm -f /tmp/src.tar
# A Linux checkout (the CI's) holds LF where this Windows checkout holds CRLF (.gitattributes: text=auto), so the
# copy gets the Linux line endings; the files .gitattributes forces to CRLF (*.ps1, *.cmd, *.bat) keep them. Without
# it the C# raw-string scripts of the tests carry a CR and the shell refuses them.
grep -rIlZ -e "$(printf '\r')" /work --exclude='*.ps1' --exclude='*.cmd' --exclude='*.bat' | xargs -0 -r sed -i 's/\r$//'
cd /work
RAW_EXIT=0
dotnet test --project tests/Aetheus.Agent.Core.Tests --configuration Release --no-progress \
  --report-trx --report-trx-filename agent.trx --results-directory "$RESULTS" $AGENT_TEST_ARGS || RAW_EXIT=$?
printf "%s\n" "$RAW_EXIT" > "$RESULTS/raw-exit-code"
exit "$RAW_EXIT"
'@ -replace "`r`n", "`n"

# The SDK image reference (tag and digest) is read from the release Dockerfile, so the suite follows the pin the
# release builds use and cannot drift from it.
function Get-AgentSuiteImage {
    $dockerfile = Join-Path $root "deploy\docker\Dockerfile.back"
    $match = Select-String -LiteralPath $dockerfile -Pattern '^FROM\s+(mcr\.microsoft\.com/dotnet/sdk:\S+)\s+AS\s+build-source\s*$' | Select-Object -First 1
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value
}

# A run that could not start: reported as a failed suite (exit 1, no test), never replaced by a host run.
function New-UnlaunchedAgentSuiteRun([string]$Label, [string]$Why) {
    Write-Host "  $Why" -ForegroundColor Red
    Write-Host "  The agent unit tests run only in a Linux container, as the CI runs them: they are NOT run on the Windows host." -ForegroundColor Red
    return @{ Kind = "dotnet"; Label = $Label; Process = $null; Stream = $false
              Result = @{ Label = $Label; ExitCode = 1; Passed = 0; Failed = 0; Total = 0; ExpectedTotal = 0; Skipped = 0; ElapsedSec = 0 } }
}

function Start-AgentSuiteContainerRun {
    param([string]$Label, [string[]]$ExtraArgs = @())
    $dockerOs = $null
    try { $dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null | Select-Object -First 1) } catch { $dockerOs = $null }
    if ("$dockerOs".Trim() -ne "linux") {
        return New-UnlaunchedAgentSuiteRun $Label "Docker did not report a running Linux engine (docker version --format '{{.Server.Os}}' returned '$dockerOs'): is Docker running in Linux containers mode?"
    }
    $image = Get-AgentSuiteImage
    if (-not $image) {
        return New-UnlaunchedAgentSuiteRun $Label "The SDK image of the agent suite was not found in deploy\docker\Dockerfile.back (the build-source stage)."
    }
    $resultDirectory = if ($Coverage) { $coverageDir } else { Join-Path $root "TestResults\Launcher" }
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $safeLabel = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $stamp = "{0:yyyyMMdd-HHmmssfff}" -f (Get-Date)
    $trxName = "$safeLabel-$stamp.trx"
    $container = "aetheus-ylaunch-agent-$e2eCheckoutId"
    & docker rm -f $container 2>$null | Out-Null
    $createArgs = @("create", "--name", $container,
                    "--mount", "type=bind,src=$root,dst=/src,readonly",
                    "-v", "${agentSuiteNuGetVolume}:/nuget",
                    "-e", "NUGET_PACKAGES=/nuget",
                    "-e", "AGENT_TEST_ARGS=$($ExtraArgs -join ' ')",
                    "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
                    $image, "sh", "/tmp/ylaunch-agent-run.sh")
    Write-Host "  Container: docker $($createArgs -join ' ')" -ForegroundColor DarkGray
    & docker @createArgs | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return New-UnlaunchedAgentSuiteRun $Label "docker create of the agent suite container failed (exit $LASTEXITCODE; is the image $image pullable?)."
    }
    $scriptFile = Join-Path $env:TEMP "ylaunch-agent-run-$(Get-Random).sh"
    [System.IO.File]::WriteAllText($scriptFile, $agentSuiteRunnerScript)
    & docker cp $scriptFile "${container}:/tmp/ylaunch-agent-run.sh" | Out-Null
    $copied = $LASTEXITCODE
    Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    if ($copied -ne 0) {
        & docker rm -f $container 2>$null | Out-Null
        return New-UnlaunchedAgentSuiteRun $Label "docker cp of the runner script into $container failed (exit $copied)."
    }
    $tmpOut = Join-Path $env:TEMP "aetheus_test_$(Get-Random).txt"
    $tmpErr = "$tmpOut.err"
    $proc = Start-Process -FilePath "docker" -ArgumentList @("start", "-a", $container) -WorkingDirectory $root `
        -NoNewWindow -PassThru -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    return @{
        Kind = "dotnet"; Label = $Label; Tag = ""; Process = $proc; Stream = $false
        OutputPath = $tmpOut; ErrorPath = $tmpErr; ResultDirectory = $resultDirectory; TrxName = $trxName
        Container = $container; ContainerEvidence = Join-Path $resultDirectory "$safeLabel-$stamp"
        ContainerResultsPath = $agentSuiteResults; ContainerTrx = "agent.trx"
        CopyCoverage = ($ExtraArgs -contains "--coverlet"); PlatformFile = "platform.txt"
        Stopwatch = [System.Diagnostics.Stopwatch]::StartNew(); Reader = $null
        StreamIndex = 0; StreamPassed = 0; StreamFailed = 0; StreamSkipped = 0; Result = $null
    }
}

# ---------- JavaScript lint container ----------

# The quality pipeline lints the repository with the project's locked ESLint (scanner-manifest.json, entry
# "eslint"), installed by `npm ci` from the committed package-lock.json and run in the pinned Node image
# (candidate 2967, R-48: ESLint 10.13.0 crashed on load in the pipeline while the workstation's own, older,
# node_modules linted fine, so no local run ever exercised the locked tool). This suite does the same thing:
# a clean `npm ci` of the committed lock in the manifest's image, then `eslint .` with the same config and the
# same JSON report as the scanner. A crash (a tool that does not load) and any finding both fail it. The
# checkout is mounted read-only; only the JavaScript sources and the npm manifests are copied into the
# container, so the host's node_modules never take part. The npm cache is a named volume. There is no host
# fallback: without a usable Docker the suite fails.
$jsLintNpmVolume = "aetheus-ylaunch-npm"
$jsLintRunnerScript = @'
set -u
rm -rf /work /tmp/js-files.lst
mkdir -p /work
cd /src
find . \( -name node_modules -o -name .git -o -name bin -o -name obj -o -name TestResults -o -name .worktrees -o -path ./.claude/worktrees \) -prune -o \
  -type f \( -name '*.js' -o -name '*.jsx' -o -name '*.mjs' -o -name '*.cjs' -o -name '*.ts' -o -name '*.tsx' \
  -o -name package.json -o -name package-lock.json \) -print > /tmp/js-files.lst
tar -cf - -T /tmp/js-files.lst | tar -C /work -xf - || { echo "Could not copy the JavaScript sources." >&2; exit 1; }
cd /work
node --version
echo "npm ci from the committed package-lock.json..."
npm ci --ignore-scripts --no-audit --no-fund --prefer-offline > /tmp/npm-ci.log 2>&1 || {
  tail -n 30 /tmp/npm-ci.log; echo "not ok - npm ci of the committed lock failed"; printf '# tests 1\n# pass 0\n# fail 1\n# skipped 0\n'; exit 1; }
node -e "for (const p of ['eslint','@eslint/plugin-kit','@eslint/core']) console.log(p, require('/work/node_modules/'+p+'/package.json').version)"
RAW=0
node /work/node_modules/eslint/bin/eslint.js . --format json --output-file /tmp/eslint-report.json || RAW=$?
if [ "$RAW" -eq 0 ]; then
  echo "ok - eslint: no finding"; printf '# tests 1\n# pass 1\n# fail 0\n# skipped 0\n'; exit 0
fi
if [ "$RAW" -eq 1 ] && [ -s /tmp/eslint-report.json ]; then
  node -e "const r=require('/tmp/eslint-report.json');let e=0,w=0;for(const f of r)for(const m of f.messages){m.severity>1?e++:w++;console.log(f.filePath.replace('/work/','')+':'+m.line+' '+m.ruleId+' '+m.message)}console.log('errors '+e+', warnings '+w)" | head -n 40
  echo "not ok - eslint reported findings"
else
  echo "not ok - eslint crashed or did not run (exit $RAW): the locked tool does not load"
fi
printf '# tests 1\n# pass 0\n# fail 1\n# skipped 0\n'
exit 1
'@ -replace "`r`n", "`n"

# The image the quality pipeline's ESLint step runs in, read from the scanner manifest so the suite follows its pin.
function Get-JsLintImage {
    $manifest = Join-Path $root "scanner-manifest.json"
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    $entry = @((Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).scanners | Where-Object { $_.key -eq "eslint" }) | Select-Object -First 1
    if (-not $entry -or [string]::IsNullOrWhiteSpace($entry.image)) { return $null }
    return [string]$entry.image
}

# A run that could not start: reported as a failed suite (exit 1, no test), never replaced by a host run.
function New-UnlaunchedJsLintRun([string]$Label, [string]$Why) {
    Write-Host "  $Why" -ForegroundColor Red
    Write-Host "  The locked ESLint runs only in the pipeline's Node container: it is NOT run on the Windows host." -ForegroundColor Red
    return @{ Kind = "node"; Label = $Label; Process = $null; Stream = $false
              Result = @{ Label = $Label; ExitCode = 1; Passed = 0; Failed = 0; Total = 0; ExpectedTotal = 0; Skipped = 0; ElapsedSec = 0 } }
}

function Start-JsLintContainerRun {
    param([string]$Label)
    $dockerOs = $null
    try { $dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null | Select-Object -First 1) } catch { $dockerOs = $null }
    if ("$dockerOs".Trim() -ne "linux") {
        return New-UnlaunchedJsLintRun $Label "Docker did not report a running Linux engine (docker version --format '{{.Server.Os}}' returned '$dockerOs'): is Docker running in Linux containers mode?"
    }
    $image = Get-JsLintImage
    if (-not $image) { return New-UnlaunchedJsLintRun $Label "The ESLint image was not found in scanner-manifest.json (entry 'eslint')." }
    $scriptFile = Join-Path $env:TEMP "ylaunch-jslint-run-$(Get-Random).sh"
    [System.IO.File]::WriteAllText($scriptFile, $jsLintRunnerScript)
    $tmpOut = Join-Path $env:TEMP "aetheus_node_test_$(Get-Random).txt"
    $tmpErr = "$tmpOut.err"
    $container = "aetheus-ylaunch-jslint-$e2eCheckoutId"
    & docker rm -f $container 2>$null | Out-Null
    $runArgs = @("run", "--rm", "-i", "--name", $container,
                 "--mount", "type=bind,src=$root,dst=/src,readonly",
                 "-v", "${jsLintNpmVolume}:/npm-cache", "-e", "npm_config_cache=/npm-cache",
                 $image, "sh", "-s")
    Write-Host "  Container: docker $($runArgs -join ' ')" -ForegroundColor DarkGray
    $proc = Start-Process -FilePath "docker" -ArgumentList $runArgs -WorkingDirectory $root -NoNewWindow -PassThru `
        -RedirectStandardInput $scriptFile -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr
    return @{
        Kind = "node"; Label = $Label; Tool = "ESLint in the pipeline's Node container"; Tag = ""; Process = $proc; Stream = $false; Files = @()
        OutputPath = $tmpOut; ErrorPath = $tmpErr; Stopwatch = [System.Diagnostics.Stopwatch]::StartNew(); Result = $null
    }
}

# ---------- E2E category helpers ----------

# One entry per feature [Category(...)] on the E2E fixtures. Every fixture also carries the broad
# [Category("E2E")], so `dotnet test --filter Category=E2E` runs them all; these are the granular
# slices the -tec picker exposes. Keep in sync when adding a new E2E fixture/category.
$E2eCategories = @(
    'Auth',
    'Dashboard',
    'Servers',
    'Agents',
    'Pipelines',
    'Projects',
    'Tasks',
    'Alerts',
    'Settings',
    'Security',
    'Rbac',
    'Logs',
    'Accessibility',
    'ResponsiveAdmin',
    'ProductionSmoke',
    'RollbackRelevant',
    'PageRules'
)

# With YLAUNCH_E2E_SHARDS=2 a full E2E run is two shards at the same time, each on its own stack. Shard 1 takes these categories,
# shard 2 takes every test that is not in them, so a fixture or a category added later is run without
# touching this list and no test can fall between the two. PageRules is one fixture of four long sweeps;
# the TRX of 2026-10-27 (90 tests, 516 s of test time) puts it at 245 s against 271 s for all the rest.
$E2eShardOneCategories = @('PageRules')
# Categories whose sweep runs only when named (owner decision of 2026-10-27): the sweep of every page is run by hand, from
# time to time (-tec PageRules), never by -ta nor by the candidate (the test skips itself without AETHEUS_PAGE_RULES_SWEEP=1).
$E2eManualCategories = @('PageRules')

function Resolve-E2eCategories([string]$filter) {
    if ($filter -eq 'all' -or $filter -eq '*') { return @() }
    $parts = $filter -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
    $resolved = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $parts) {
        if ($part -match '^\d+$') {
            $idx = [int]$part - 1
            if ($idx -lt 0 -or $idx -ge $E2eCategories.Count) {
                Write-Host "  Unknown category number: $part (valid: 1-$($E2eCategories.Count))" -ForegroundColor Red
                throw [System.ArgumentException]::new("Unknown E2E category number: $part")
            }
            $resolved.Add($E2eCategories[$idx])
        } elseif ($E2eCategories -icontains $part) {
            $resolved.Add(($E2eCategories | Where-Object { $_ -ieq $part } | Select-Object -First 1))
        } else {
            Write-Host "  Unknown category: '$part'" -ForegroundColor Red
            Write-Host "  Valid categories:" -ForegroundColor Yellow
            for ($i = 0; $i -lt $E2eCategories.Count; $i++) {
                Write-Host "    $($i + 1). $($E2eCategories[$i])" -ForegroundColor Yellow
            }
            throw [System.ArgumentException]::new("Unknown E2E category: $part")
        }
    }
    return $resolved.ToArray()
}

function Show-E2eMenu {
    Write-Host ""
    Write-Host "  E2E Test Categories" -ForegroundColor Cyan
    Write-Host "  -------------------" -ForegroundColor DarkGray
    for ($i = 0; $i -lt $E2eCategories.Count; $i++) {
        Write-Host "  $($i + 1). $($E2eCategories[$i])"
    }
    Write-Host ""
    Write-Host "  Enter numbers or names (comma-separated), or press Enter / * for all:" -ForegroundColor Yellow
    $userInput = Read-Host "  >"
    $userInput = $userInput.Trim()
    if ($userInput -eq '' -or $userInput -eq '*' -or $userInput -ieq 'all') { return @() }
    return Resolve-E2eCategories $userInput
}

function Wait-ForEndpoint([string]$url, [string]$label, [int]$maxSeconds = 60) {
    Write-Step "Waiting for $label to be ready..."
    $waited = 0
    while ($waited -lt $maxSeconds) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 -SkipCertificateCheck -ErrorAction SilentlyContinue
            if ($r.StatusCode -lt 400) {
                Write-Host "`r  $label ready after ${waited}s   " -ForegroundColor Green
                return $true
            }
        } catch { }
        Start-Sleep -Seconds 1
        $waited++
        Write-Host "`r  Waiting... ${waited}s   " -NoNewline -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "  $label did not respond after ${maxSeconds}s." -ForegroundColor Red
    return $false
}

# One E2E stack: a backend and a frontend on their own ports, from their own runtime snapshots, against
# their own database. Index 1 is the launcher's own stack (the ports computed above, the snapshots of
# Section 3, the database aetheus_e2e). Each further index listens another 1000 ports up (index 2 of a
# default test run: back 7301/7300, front 7401), runs from copies of the snapshots (the backend writes its
# logs and key ring beside its assemblies, the static server rewrites its appsettings.json at start) and
# keeps its file stores and its database apart, so two stacks never share a port, a database or a file.
function New-E2eStack([int]$Index) {
    $offset = ($Index - 1) * $e2eStackPortOffset
    $https = $backHttpsPort + $offset
    $http = $backHttpPort + $offset
    $front = $frontPort + $offset
    $first = $Index -eq 1
    return @{
        Index = $Index
        Name = "shard-$Index"
        BackHttpsPort = $https
        BackHttpPort = $http
        FrontPort = $front
        BackUrl = "https://localhost:${https}/health/live"
        FrontUrl = "https://localhost:$front"
        ApiBaseUrl = "https://localhost:$https"
        BackAspUrls = "https://localhost:${https};http://localhost:$http"
        FrontAspUrls = "https://localhost:$front"
        CorsFrontUrl = "https://localhost:$front"
        # The same front and API as the E2E browser container reaches them (Start-E2eContainerRun): over plain
        # HTTP, as QA reaches its stack (http://<front>:8080). Over HTTPS the container's Chromium refuses the
        # service worker of a host the development certificate does not cover ("An SSL certificate error
        # occurred when fetching the script"), which no test ignores. The front gets an HTTP listener one port
        # below its HTTPS one, as the backend has.
        FrontHttpPort = $front - 1
        ContainerFrontUrl = "http://${e2eContainerHost}:$($front - 1)"
        ContainerApiBaseUrl = "http://${e2eContainerHost}:$http"
        PublicApiUrl = if ($first) { $backPublicApiUrl } else { "https://localhost:$https" }
        DatabaseName = if ($first) { "aetheus_e2e" } else { "aetheus_s${Index}_e2e" }
        ConnString = if ($first) { $e2eConnString } else { $e2eConnString.Replace("Database=aetheus_e2e;", "Database=aetheus_s${Index}_e2e;") }
        BackSnapshotDir = if ($first) { $backSnapshotDir } else { Join-Path $root "TestResults\YLaunchE2eShard${Index}Back" }
        FrontHostDir = if ($first) { $frontSnapshotHostDir } else { Join-Path $root "TestResults\YLaunchE2eShard${Index}Front" }
        DataRoot = if ($first) { $null } else { Join-Path $root "TestResults\YLaunchE2eShard${Index}Data" }
    }
}

# A stack after the first has its own database in the dedicated E2E PostgreSQL container: the name ends in
# _e2e, which is all the guarded reset endpoint needs, and the destructive reset only ever sees that one.
function Initialize-E2eStack([hashtable]$Stack) {
    if ($Stack.Index -eq 1) { return }
    $database = $Stack.DatabaseName
    $exists = & docker exec aetheus-e2e-database psql -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$database'" 2>$null
    if ("$exists".Trim() -ne "1") {
        & docker exec aetheus-e2e-database psql -U postgres -d postgres -c "CREATE DATABASE $database" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not create the E2E database $database." }
    }
    $null = Copy-RuntimeSnapshot (Split-Path $Stack.BackSnapshotDir -Leaf) $backSnapshotDir "Aetheus.Back.dll"
    $null = Copy-RuntimeSnapshot (Split-Path $Stack.FrontHostDir -Leaf) $frontSnapshotHostDir "StaticServer.dll"
    if (Test-Path -LiteralPath $Stack.DataRoot) { Remove-Item -LiteralPath $Stack.DataRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $Stack.DataRoot -Force | Out-Null
}

function Start-Servers {
    param(
        [switch]$ForE2e,
        # An E2E shard's own stack (New-E2eStack): its ports, runtime snapshots, database and data directories.
        # Without it the stack is the launcher's own, exactly as before. Only the snapshot branch honours it.
        [hashtable]$Stack
    )

    $jobs = @{}
    if (-not $Stack) { $Stack = New-E2eStack -Index 1 }
    $stackBackHttpsPort = $Stack.BackHttpsPort
    $stackBackAspUrls = $Stack.BackAspUrls
    $stackFrontAspUrls = $Stack.FrontAspUrls
    $stackCorsFrontUrl = $Stack.CorsFrontUrl
    $stackPublicApiUrl = $Stack.PublicApiUrl
    $stackApiBaseUrl = $Stack.ApiBaseUrl
    if ($ForE2e) {
        # The browser runs in the QA browser container: the WebAssembly front calls the API by the name the
        # container reaches it by, and the backend accepts that front origin.
        $stackCorsFrontUrl = $Stack.ContainerFrontUrl
        $stackApiBaseUrl = $Stack.ContainerApiBaseUrl
        $stackFrontAspUrls = "$($Stack.FrontAspUrls);http://localhost:$($Stack.FrontHttpPort)"
    }
    $stackConnString = $Stack.ConnString
    $stackBackSnapshotDir = $Stack.BackSnapshotDir
    $stackFrontHostDir = $Stack.FrontHostDir
    $stackDataRoot = $Stack.DataRoot
    # Coexisting worktrees intentionally share the development database. Keep every
    # database-backed file store in the same per-user location as well; otherwise a
    # backend started from another worktree can observe valid artifact rows through
    # PostgreSQL while resolving their files below its own (empty) content root.
    $sharedDevelopmentDataRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        "Aetheus\shared-development"
    # Server jobs have no interactive console attached. Tell generated .NET apphosts to
    # write startup failures to stderr instead of displaying a modal Windows error dialog.
    # The launcher streams that stderr and reports the component's non-zero exit code.
    $dotnetJobInitialization = {
        $env:DOTNET_DISABLE_GUI_ERRORS = "1"
    }

    # E2E runs every test's [SetUp] through the real /api/auth/login endpoint,
    # which is protected by the production "login" rate limiter (5 req / IP /
    # minute). From a single loopback IP the suite blows past that after the
    # first few tests and the backend returns 429, which the UI renders as
    # "Invalid e-mail address or password." Disable the limiter for the E2E backend
    # only -this is exactly the documented `RateLimiting:Disabled=true`
    # mechanism the integration-test fixtures already use, and it does NOT
    # affect normal dev runs or production behavior.
    $e2eEnv = $ForE2e.IsPresent

    if ($ModeFront) {
        # In coexistence mode the launch profile's applicationUrl (5301/5401) would
        # override our ASPNETCORE_URLS, so we skip it with --no-launch-profile and
        # re-set the only env the profile gave us (ASPNETCORE_ENVIRONMENT=Development).
        # Default mode is left byte-for-byte unchanged (profile drives everything).
        if ($HotReload) {
            Write-Step "Starting Backend with hot reload (https://localhost:$backHttpsPort)..."
            $jobs.BackJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                $env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "1"
                $env:UseSharedCompilation = "false"
                if ($using:e2eEnv) {
                    $env:RateLimiting__Disabled = "true"
                    # The authenticated reset endpoint recreates the complete schema after startup.
                    # Delay the immediate task sweep so it cannot query a table between DROP SCHEMA
                    # and the last migration. Ordinary dev/prod hosts retain the zero-delay default.
                    $env:BackgroundServices__TaskStartupDelay = "00:30:00"
                    # No nightly AI model list read (R3-7): a seeded profile would send a fake key to a real provider.
                    $env:AiTasks__ModelListRefresh__Enabled = "false"
                    # Point the E2E backend at the isolated ephemeral DB (:15433); env vars override
                    # appsettings, so the dev DB (:15432) is never opened. Seed demo data too.
                    $env:ConnectionStrings__Default = $using:e2eConnString
                    $env:Seed__Demo = "true"
                } else {
                    # Local conformance data remains visible in the ordinary development control plane.
                    $env:ArtifactStorage__BasePath = Join-Path $using:sharedDevelopmentDataRoot "artifacts"
                    $env:PackageRegistry__BasePath = Join-Path $using:sharedDevelopmentDataRoot "packages"
                    $env:GitLight__RepositoriesPath = Join-Path $using:sharedDevelopmentDataRoot "git-repos"
                    $env:GitLight__CloneBaseUrl = "https://host.docker.internal:$using:backHttpsPort"
                    $env:Seed__ConformanceToto = "true"
                    $env:AppMonitoring__IngestBaseUrl = "https://host.docker.internal:$using:backHttpsPort"
                }
                $profileArgs = @()
                if ($using:portsOverridden) {
                    $env:ASPNETCORE_ENVIRONMENT = "Development"
                    $env:ASPNETCORE_URLS = $using:backAspUrls
                    $env:Cors__FrontendUrl = $using:corsFrontUrl
                    $env:Aetheus__PublicApiBaseUrl = $using:backPublicApiUrl
                    $profileArgs = @("--no-launch-profile")
                }
                Set-Location $using:backDir
                dotnet watch run --configuration Debug @profileArgs 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
            # Each watcher builds the same Shared project. Let one complete its initial build
            # before the next starts, or parallel csc processes race to write Shared.dll.
            if (-not (Wait-ForEndpoint -url $backUrl -label "Backend hot reload" -maxSeconds 60)) {
                return $jobs
            }
            Write-Step "Starting Frontend with hot reload (https://localhost:$frontPort)..."
            $jobs.FrontJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                $env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "1"
                $env:UseSharedCompilation = "false"
                $profileArgs = @()
                if ($using:portsOverridden) {
                    $env:ASPNETCORE_ENVIRONMENT = "Development"
                    $env:ASPNETCORE_URLS = $using:frontAspUrls
                    $profileArgs = @("--no-launch-profile")
                }
                Set-Location $using:frontDir
                dotnet watch run --configuration Debug @profileArgs 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
            if ($ModeAgent -and -not (Wait-ForEndpoint -url $frontUrl -label "Frontend hot reload" -maxSeconds 60)) {
                return $jobs
            }
        } else {
            Write-Step "Starting Backend snapshot (https://localhost:$stackBackHttpsPort)..."
            $jobs.BackJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                if ($using:e2eEnv) {
                    $env:RateLimiting__Disabled = "true"
                    # The authenticated reset endpoint recreates the complete schema after startup.
                    # Delay the immediate task sweep so it cannot query a table between DROP SCHEMA
                    # and the last migration. Ordinary dev/prod hosts retain the zero-delay default.
                    $env:BackgroundServices__TaskStartupDelay = "00:30:00"
                    # No nightly AI model list read (R3-7): a seeded profile would send a fake key to a real provider.
                    $env:AiTasks__ModelListRefresh__Enabled = "false"
                    # Point the E2E backend at the isolated ephemeral DB (:15433); env vars override
                    # appsettings, so the dev DB (:15432) is never opened. Seed demo data too.
                    $env:ConnectionStrings__Default = $using:stackConnString
                    $env:Seed__Demo = "true"
                    # A second E2E stack shares the content root with the first: its file stores (artifacts,
                    # packages, git repositories) live in its own directory, so the stacks never share files.
                    if ($using:stackDataRoot) {
                        $env:ArtifactStorage__BasePath = Join-Path $using:stackDataRoot "artifacts"
                        $env:PackageRegistry__BasePath = Join-Path $using:stackDataRoot "packages"
                        $env:GitLight__RepositoriesPath = Join-Path $using:stackDataRoot "git-repos"
                    }
                } else {
                    # Local conformance data remains visible in the ordinary development control plane.
                    $env:ArtifactStorage__BasePath = Join-Path $using:sharedDevelopmentDataRoot "artifacts"
                    $env:PackageRegistry__BasePath = Join-Path $using:sharedDevelopmentDataRoot "packages"
                    $env:GitLight__RepositoriesPath = Join-Path $using:sharedDevelopmentDataRoot "git-repos"
                    $env:GitLight__CloneBaseUrl = "https://host.docker.internal:$using:stackBackHttpsPort"
                    $env:Seed__ConformanceToto = "true"
                    $env:AppMonitoring__IngestBaseUrl = "https://host.docker.internal:$using:stackBackHttpsPort"
                }
                $env:ASPNETCORE_ENVIRONMENT = "Development"
                $env:ASPNETCORE_URLS = $using:stackBackAspUrls
                $env:Cors__FrontendUrl = $using:stackCorsFrontUrl
                $env:Aetheus__PublicApiBaseUrl = $using:stackPublicApiUrl
                Set-Location $using:backDir
                dotnet (Join-Path $using:stackBackSnapshotDir "Aetheus.Back.dll") 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
            Write-Step "Starting Frontend snapshot ($($Stack.FrontUrl))..."
            $jobs.FrontJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                $env:ASPNETCORE_ENVIRONMENT = "Development"
                $env:ASPNETCORE_URLS = $using:stackFrontAspUrls
                $env:API_BASE_URL = $using:stackApiBaseUrl
                Set-Location $using:stackFrontHostDir
                dotnet (Join-Path $using:stackFrontHostDir "StaticServer.dll") 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
        }
    }

    if ($ModeAgent) {
        if ($HotReload) {
            Write-Step "Starting Agent with hot reload..."
            $jobs.AgentJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                $env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "1"
                $env:UseSharedCompilation = "false"
                Set-Location $using:agentDir
                dotnet watch run --configuration Debug 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
        } else {
            Write-Step "Starting Agent snapshot..."
            $jobs.AgentJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                Set-Location $using:agentDir
                dotnet (Join-Path $using:agentSnapshotDir "Aetheus.Agent.Windows.dll") 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
        }
    }

    return $jobs
}

# The single definition of "this checkout" that BOTH kill passes below use. Matching the bare word
# "Aetheus" would also match a sibling worktree (every worktree path contains it), and matching just
# $root would let the parent checkout claim its own worktrees (their paths are nested under it). The
# source and local runtime snapshot boundaries pin it to this exact checkout.
function Get-AetheusOwnProcessPattern {
    $snapshotRoot = [regex]::Escape((Join-Path $root "TestResults") + [IO.Path]::DirectorySeparatorChar + "YLaunch")
    # A test run owns only the E2E snapshots it starts: the development instance (its snapshots, or
    # `dotnet watch` from src) belongs to the user and keeps running.
    if ($script:testRun) { return "${snapshotRoot}E2e" }
    # Symmetrically, a development launch leaves a test run's E2E stack alone.
    $source = [regex]::Escape((Join-Path $root "src") + [IO.Path]::DirectorySeparatorChar)
    "($source|$snapshotRoot(?!E2e))"
}

# True when the process is one of THIS checkout's own. The command line carries the boundary for
# `dotnet run` and for the Blazor dev server (which names the application path), the executable path
# carries it for the generated apphosts (Aetheus.Back.exe and friends); either is proof of ownership.
function Test-AetheusProcessBelongsToCheckout {
    param([int]$ProcessId, [string]$OwnPattern, $ProcessInfo)
    try {
        $info = if ($ProcessInfo) { $ProcessInfo } else { Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue }
        if (-not $info) { return $false }
        return ($info.CommandLine -and $info.CommandLine -match $OwnPattern) -or
               ($info.ExecutablePath -and $info.ExecutablePath -match $OwnPattern)
    } catch { return $false }
}

# Kills every Aetheus-related process: anything listening on the back/front ports plus any
# `dotnet` whose command line mentions Aetheus (back, front, agent). Used both to clear a
# previous run at startup and to fully tear the stack down on shutdown - Stop-Job alone leaves the
# `dotnet run` grandchildren orphaned. Both passes are scoped to this checkout; see
# Get-AetheusOwnProcessPattern for why, and for what went wrong when only one of them was.
function Stop-AetheusProcesses([switch]$ListenersOnly) {
    $portsToKill = $script:killPorts
    $ownPattern = Get-AetheusOwnProcessPattern
    foreach ($port in $portsToKill) {
        $connections = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
        foreach ($conn in $connections) {
            $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
            if (-not $proc -or $proc.ProcessName -eq "Idle") { continue }
            # Same boundary as the command-line sweep below. Without it this pass defeated the very
            # isolation that one protects: a worktree started on the default ports terminated the
            # parent checkout's stack with Stop-Process -Force, which surfaces as a bare exit code
            # -1 with no log on either side. Refuse rather than kill, because a foreign owner means
            # the port is genuinely taken and binding would fail anyway - saying whose it is turns a
            # silent execution into a diagnosable one.
            if (-not (Test-AetheusProcessBelongsToCheckout -ProcessId $proc.Id -OwnPattern $ownPattern)) {
                $owner = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" -ErrorAction SilentlyContinue).CommandLine
                throw ("Port $port is held by PID $($proc.Id) ($($proc.ProcessName)), which does not belong to this checkout ($root). " +
                       "Refusing to terminate another checkout's process. Stop it yourself, or give this worktree its own ports with -w. " +
                       "Owner command line: $owner")
            }
            Write-Host "  Killing PID $($proc.Id) ($($proc.ProcessName)) on port $port" -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($ListenersOnly) {
        return
    }

    # Read the process table once. Querying Win32_Process by PID for every MSBuild node made a
    # launcher restart spend minutes here when another build left hundreds of idle dotnet nodes.
    $dotnetProcesses = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue
    foreach ($info in $dotnetProcesses) {
        if (Test-AetheusProcessBelongsToCheckout -ProcessId $info.ProcessId -OwnPattern $ownPattern -ProcessInfo $info) {
            Write-Host "  Killing dotnet PID $($info.ProcessId) (Aetheus-related)" -ForegroundColor Yellow
            Stop-Process -Id $info.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Stop-Servers([hashtable]$jobs, [bool]$DumpLogs = $false) {
    Write-Step "Shutting down..."
    # Stop the listener children while their `dotnet run` parent jobs are still alive.
    # Disposing the jobs first orphans blazor-devserver and can make it raise an
    # unhandled managed exception while its inherited console handles disappear.
    Stop-AetheusProcesses -ListenersOnly
    foreach ($key in $jobs.Keys) {
        Wait-Job -Job $jobs[$key] -Timeout 3 -ErrorAction SilentlyContinue | Out-Null
        if ($jobs[$key].State -notin @('Completed', 'Failed', 'Stopped')) {
            Stop-Job -Job $jobs[$key] -ErrorAction SilentlyContinue
        }
        if ($DumpLogs) {
            Write-Host "  Last output from $key`:" -ForegroundColor Yellow
            @(Receive-Job -Job $jobs[$key] -ErrorAction SilentlyContinue) |
                Select-Object -Last 120 |
                ForEach-Object { Write-Host "  [$key] $_" -ForegroundColor DarkYellow }
        }
        Remove-Job -Job $jobs[$key] -Force -ErrorAction SilentlyContinue
    }
    # Finish any wrapper process that did not exit after its listener stopped.
    Stop-AetheusProcesses
    Write-Host "  Done." -ForegroundColor Green
}

# ============================================================
#  SSH TUNNEL HELPERS
# ============================================================

function Resolve-SshTarget {
    $raFile = Join-Path $root ".remoteagent"
    $raTarget = $null

    if (Test-Path $raFile) {
        $raTarget = (Get-Content $raFile -Raw).Trim()
        Write-Host "  Target: $raTarget  (edit .remoteagent to change)" -ForegroundColor DarkGray
    }

    if (-not $raTarget) {
        $raTarget = Read-Host "  SSH target (user@host or user@host:port)"
        $raTarget = $raTarget.Trim()
        if (-not $raTarget) {
            Write-Host "  No target - aborting." -ForegroundColor Red
            return $null
        }
        Set-Content -Path $raFile -Value $raTarget -NoNewline
    }

    $sshHost = $raTarget
    $sshPort = $null
    if ($raTarget -match '^(.+):(\d+)$') {
        $sshHost = $Matches[1]
        $sshPort = $Matches[2]
    }
    return @{ Host = $sshHost; Port = $sshPort; Raw = $raTarget }
}

function Start-SshTunnel($info) {
    $sshArgs = @("-NR", "5300:localhost:5300", $info.Host,
                 "-o", "ServerAliveInterval=60",
                 "-o", "ExitOnForwardFailure=yes",
                 "-o", "BatchMode=no")
    if ($info.Port) { $sshArgs += @("-p", $info.Port) }

    # Start-Process -NoNewWindow lets SSH own the console for password masking.
    $proc = Start-Process -FilePath "ssh" -ArgumentList $sshArgs -NoNewWindow -PassThru

    # Wait for the tunnel to establish: poll the forwarded port instead of
    # blindly waiting N seconds. This way we know it actually works.
    $deadline = (Get-Date).AddSeconds(20)
    $connected = $false
    while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
        Start-Sleep -Milliseconds 500
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new()
            $tcp.Connect("localhost", 5300)
            $tcp.Close()
            $connected = $true
            break
        } catch { }
    }

    if ($proc.HasExited) {
        Write-Host "  SSH failed (exit $($proc.ExitCode)). Check credentials." -ForegroundColor Red
        return $null
    }

    if (-not $connected) {
        Write-Host "  Tunnel did not establish in time (port 5300 not open)." -ForegroundColor Red
        Write-Host "  The backend may not be running yet - that's OK, the tunnel is up." -ForegroundColor Yellow
    }

    return $proc
}

# ============================================================
#  Preflight: SDK guard + mechanised code rules (report only), same code as the _Generic kit core
# ============================================================

# dotnet resolves global.json by walking up from the working directory; so does the launcher.
function Find-YGlobalJson([string]$Root) {
    $dir = [IO.DirectoryInfo]::new($Root)
    while ($dir) {
        $candidate = Join-Path $dir.FullName 'global.json'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
        $dir = $dir.Parent
    }
    return $null
}

function Show-YVersionGuard([string]$Root, [string]$Solution) {
    $globalJson = Find-YGlobalJson $Root
    if (-not $globalJson) {
        Write-Host "  [guard] global.json missing: the SDK floor is undeclared (STD-SDKPIN)." -ForegroundColor Yellow
        return
    }
    $floor = (Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json).sdk.version
    Push-Location -LiteralPath $Root
    try { $resolved = (& dotnet --version 2>$null | Select-Object -Last 1) } finally { Pop-Location }
    if (-not $resolved) {
        Write-Host "  [guard] dotnet --version failed: no installed SDK satisfies the floor $floor ($globalJson)." -ForegroundColor Yellow
        return
    }
    if ([version]($resolved -replace '-.*$', '') -lt [version]$floor) {
        Write-Host "  [guard] resolved SDK $resolved is below the global.json floor $floor." -ForegroundColor Yellow
    }
    $channel = "{0}.{1}" -f ([version]$floor).Major, ([version]$floor).Minor
    try {
        $index = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json" -TimeoutSec 4
        $entry = $index.'releases-index' | Where-Object { $_.'channel-version' -eq $channel } | Select-Object -First 1
        if ($entry -and [version]$entry.'latest-sdk' -gt [version]($resolved -replace '-.*$', '')) {
            Write-Host "  [guard] SDK $($entry.'latest-sdk') is published, this checkout resolves $resolved (floor $floor)." -ForegroundColor Yellow
        } else {
            Write-Host "  SDK $resolved (floor $floor, latest published $($entry.'latest-sdk'))." -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  SDK $resolved (floor $floor, release index unreachable)." -ForegroundColor DarkGray
    }
    if ($Solution -and (Test-Path -LiteralPath $Solution)) {
        $outdated = & dotnet list $Solution package --outdated 2>$null
        $references = @($outdated | Where-Object { $_ -match '^\s*>\s' } | ForEach-Object { ($_ -split '\s+' | Where-Object { $_ })[1] })
        # Recette R-535: documented pins are not counted while their end condition is not reached; an
        # expired one is named and counted again, so no exception outlives its reason.
        $excepted = @{}
        $exceptionsFile = Join-Path $Root 'scripts/outdated-package-exceptions.json'
        if (Test-Path -LiteralPath $exceptionsFile) {
            $propsFile = Join-Path (Split-Path -Parent $Solution) 'Directory.Build.props'
            $targetFramework = if (Test-Path -LiteralPath $propsFile) { ([xml](Get-Content -LiteralPath $propsFile -Raw)).Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1 } else { $null }
            $currentVersion = if ($targetFramework -match '^net(\d+\.\d+)$') { [version]$Matches[1] } else { $null }
            foreach ($exception in (Get-Content -LiteralPath $exceptionsFile -Raw | ConvertFrom-Json).exceptions) {
                $untilVersion = if ($exception.untilTargetFramework -match '^net(\d+\.\d+)$') { [version]$Matches[1] } else { $null }
                if ($currentVersion -and $untilVersion -and $currentVersion -ge $untilVersion) {
                    Write-Host "  [guard] exception expired for $($exception.package) ($targetFramework reached $($exception.untilTargetFramework)): update it or renew the exception (scripts/outdated-package-exceptions.json)." -ForegroundColor Yellow
                } else {
                    $excepted[$exception.package] = $true
                }
            }
        }
        $count = @($references | Where-Object { -not $excepted.ContainsKey($_) }).Count
        $exceptedCount = @($references | Where-Object { $excepted.ContainsKey($_) }).Count
        if ($count -gt 0) {
            Write-Host "  [guard] $count NuGet package reference(s) outdated (dotnet list package --outdated)." -ForegroundColor Yellow
        }
        if ($exceptedCount -gt 0) {
            Write-Host "  $exceptedCount outdated reference(s) pinned on purpose until their end condition (scripts/outdated-package-exceptions.json)." -ForegroundColor DarkGray
        }
    }
}

# verify-rules.ps1 lives in the _Generic kit only; it is never copied. Resolved through
# $env:GENERIC_KIT, else the conventional path. The launcher shows the count and never blocks: the
# /audit `rules` lens and CI run it blocking.
function Show-YRulesPreflight([string]$Root) {
    $kitRoot = if ($env:GENERIC_KIT) { $env:GENERIC_KIT } else { 'C:\Dev\_Generic' }
    $verifyRules = Join-Path $kitRoot 'verify-rules.ps1'
    if (-not (Test-Path -LiteralPath $verifyRules)) {
        Write-Host "  [rules] verify-rules.ps1 not found in the kit ($kitRoot): mechanised rules not checked." -ForegroundColor DarkGray
        return
    }
    try {
        $lines = @(& $verifyRules -Root $Root -Warn *>&1 | ForEach-Object { "$_" })
        $summary = $lines | Where-Object { $_ -match '^Regles mecanisees' } | Select-Object -First 1
        if ($summary) {
            $color = if ($summary -match 'aucun motif') { 'DarkGray' } else { 'Yellow' }
            Write-Host "  [rules] $summary Detail: & '$verifyRules' -Root '$Root' -Warn" -ForegroundColor $color
        } else {
            Write-Host "  [rules] verify-rules.ps1 produced no summary line." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  [rules] verify-rules.ps1 failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ============================================================
#  MAIN FLOW
# ============================================================

# ---------- 0. Tunnel-only mode (-ra without other flags) ----------

if ($tunnelOnly) {
    $sshInfo = Resolve-SshTarget
    if (-not $sshInfo) { exit 1 }
    Write-Step "SSH tunnel -> $($sshInfo.Host) (fwd 5300)..."
    $sshProc = Start-SshTunnel $sshInfo
    if (-not $sshProc) { exit 1 }

    Write-Host "  Tunnel active: $($sshInfo.Host):5300 -> localhost:5300" -ForegroundColor Green
    Write-Host "  Press Ctrl+C to close." -ForegroundColor DarkGray

    try {
        while (-not $sshProc.HasExited) { Start-Sleep -Seconds 2 }
    } finally {
        if (-not $sshProc.HasExited) { Stop-Process -Id $sshProc.Id -Force -ErrorAction SilentlyContinue }
        Write-Host "  Tunnel closed." -ForegroundColor DarkGray
    }
    exit 0
}

# ---------- 0b. Dump-only mode (-DumpDb) ----------

if ($DumpDb) {
    Write-Step "Snapshotting local PostgreSQL (pg_dump, no reset)..."
    Ensure-DevDb
    Backup-DevDb
    Write-Host "  Done." -ForegroundColor Green
    exit 0
}

# ---------- 1. Kill existing instances ----------

if ($agentOnly) {
    # The agent suite starts no server and holds no port: nothing of this checkout (or another) is stopped for it.
    Write-Host "  No process to stop: the agent suite runs in a container and holds no port." -ForegroundColor DarkGray
} else {
    Write-Step "Killing existing Aetheus processes..."

    Stop-AetheusProcesses

    Start-Sleep -Seconds 1
}

# ---------- 1b. Preflight: SDK floor guard + mechanised rules (report only, never blocks) ----------

Write-Step "Preflight (SDK guard, code rules)..."
Show-YVersionGuard -Root $root -Solution $solution
Show-YRulesPreflight -Root $root


# ---------- 2. SSH tunnel for remote agent (before build) ----------

$sshProc = $null
if ($RemoteAgent) {
    $sshInfo = Resolve-SshTarget
    if (-not $sshInfo) { exit 1 }
    Write-Step "SSH tunnel -> $($sshInfo.Host) (fwd 5300)..."
    $sshProc = Start-SshTunnel $sshInfo
    if (-not $sshProc) { exit 1 }
    Write-Host "  Tunnel active: $($sshInfo.Host):5300 -> localhost:5300" -ForegroundColor Green
}

# ---------- 3. Build ----------

if ($HotReload) {
    Write-Host "  Skipping build (dotnet watch handles it)." -ForegroundColor DarkGray
} else {
    if ($agentOnly) {
        Write-Host "  Host build skipped: the agent suite is built inside its Linux container." -ForegroundColor DarkGray
    } elseif ($e2eOnly) {
        Write-Step "Building the E2E runtime projects..."
        foreach ($project in @(
            (Join-Path $root "src\Aetheus.Back\Aetheus.Back.csproj"),
            (Join-Path $root "src\Aetheus.Front\Aetheus.Front.csproj"),
            (Join-Path $root "tests\Aetheus.E2E\Aetheus.E2E.csproj")
        )) {
            dotnet build $project --configuration $buildConfiguration --maxcpucount:1 -p:UseSharedCompilation=false -nodeReuse:false
            if ($LASTEXITCODE -ne 0) {
                Write-Host "BUILD FAILED." -ForegroundColor Red
                $script:ylaunchExitCode = 1
                return
            }
        }
    } else {
        Write-Step "Building solution..."
        dotnet build $solution --configuration $buildConfiguration --maxcpucount:1 -p:UseSharedCompilation=false -nodeReuse:false
        if ($LASTEXITCODE -ne 0) {
            Write-Host "BUILD FAILED." -ForegroundColor Red
            $script:ylaunchExitCode = 1
            return
        }
    }
    if (-not $agentOnly) { Write-Host "  Build succeeded." -ForegroundColor Green }

    if ($anyTest) {
        Write-Host "  Linux agent publish skipped for test-only execution." -ForegroundColor DarkGray
    } else {
        Write-Step "Publishing Linux agent (cross-compile linux-x64, self-contained)..."
        $linuxAgentDir = Join-Path $root "src\Aetheus.Agent.Linux"
    # Self-contained so the dev-served tarball runs on a target VPS WITHOUT the .NET
    # runtime installed (matches the Docker/prod build, and launch-linux.sh). A framework-
    # dependent build here ships an apphost that aborts with "No frameworks were found"
    # on any box lacking .NET 10 - the failure that bit the agent install. (S-TECH-39)
        dotnet publish $linuxAgentDir -r linux-x64 -c Debug --self-contained true -v q --nologo -p:UseSharedCompilation=false -nodeReuse:false
        if ($LASTEXITCODE -ne 0) {
        # Loud + non-silent: this is the linux-x64/publish output the /downloads endpoint serves. A
        # silent failure here means a remote agent self-update/install would pull the LAST good build
        # (stale code) without anyone noticing. Don't abort the launch, but make it impossible to miss.
        Write-Host "  ============================================================" -ForegroundColor Red
        Write-Host "  WARNING: Linux agent publish FAILED - /downloads will serve" -ForegroundColor Red
        Write-Host "           the PREVIOUS build. A remote agent would get STALE" -ForegroundColor Red
        Write-Host "           code. Fix the error above and re-run before deploying." -ForegroundColor Red
        Write-Host "  ============================================================" -ForegroundColor Red
        }
        else { Write-Host "  Linux agent published (self-contained)." -ForegroundColor Green }
    }
}

# The ordinary frontend process must read one immutable set of static assets for its whole lifetime.
# Running the Blazor dev server from bin/obj let a later build replace its manifest while the browser
# still used the old index, producing 404s for fingerprinted _framework files (recette R-253).
# Hot reload deliberately keeps the dev server and its live build output instead.
$frontSnapshotHostDir = $null
$backSnapshotDir = $null
$agentSnapshotDir = $null
$frontApiBaseUrl = "https://localhost:$backHttpsPort"
# What the frontend snapshot is made of, as one hash: path and content of every front source (bin/obj
# excluded, on either separator) and of every assembly the front build copied, the static server's
# sources, and the per-launch appsettings.Development.json. Content, not write time: the snapshot's own
# publish (-p:RunAnalyzers=false) and the next launch's build each recompile the front, rewriting
# byte-identical assemblies with a new timestamp, so a write-time key never matched twice.
function Get-FrontSnapshotFingerprint([string]$Configuration) {
    $devSettings = Join-Path $frontDir "wwwroot\appsettings.Development.json"
    $files = @(Get-ChildItem -LiteralPath $frontDir -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -ne $devSettings })
    $files += @(Get-ChildItem -LiteralPath (Join-Path $frontDir "bin\$Configuration") -Recurse -File -Filter "*.dll" -ErrorAction SilentlyContinue)
    $files += @(Get-ChildItem -LiteralPath (Join-Path $root "deploy\docker") -File | Where-Object { $_.Extension -in ".cs", ".csproj", ".json" })
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("configuration=$Configuration")
    foreach ($file in $files | Sort-Object FullName) {
        $lines.Add("$($file.FullName.Substring($root.Length))|$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)")
    }
    if (Test-Path -LiteralPath $devSettings) {
        $lines.Add("dev-settings=$((Get-FileHash -LiteralPath $devSettings -Algorithm SHA256).Hash)")
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join [char]10))
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

function Copy-RuntimeSnapshot([string]$name, [string]$sourceDir, [string]$entryDll) {
    $testResultsRoot = [IO.Path]::GetFullPath((Join-Path $root "TestResults") + [IO.Path]::DirectorySeparatorChar)
    $snapshotDir = [IO.Path]::GetFullPath((Join-Path $testResultsRoot $name))
    if (-not $snapshotDir.StartsWith($testResultsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime snapshot path must stay under TestResults."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $entryDll))) {
        throw "Runtime build output is incomplete: $entryDll."
    }
    if (Test-Path -LiteralPath $snapshotDir) {
        Remove-Item -LiteralPath $snapshotDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir "*") -Destination $snapshotDir -Recurse -Force -ErrorAction Stop
    if (-not (Test-Path -LiteralPath (Join-Path $snapshotDir $entryDll))) {
        throw "Runtime snapshot is incomplete: $entryDll."
    }
    return $snapshotDir
}
if ($ModeFront -and -not $HotReload -and (-not $anyTest -or $TestE2e)) {
    Write-Step "Copying Backend runtime snapshot..."
    $backSnapshotDir = Copy-RuntimeSnapshot $(if ($script:testRun) { "YLaunchE2eBack" } else { "YLaunchBack" }) (Join-Path $backDir "bin\$buildConfiguration\net10.0") "Aetheus.Back.dll"
}
if ($ModeAgent -and -not $HotReload -and -not $anyTest) {
    Write-Step "Copying Agent runtime snapshot..."
    $agentSnapshotDir = Copy-RuntimeSnapshot "YLaunchAgent" (Join-Path $agentDir "bin\$buildConfiguration\net10.0-windows") "Aetheus.Agent.Windows.dll"
}
if ($ModeFront -and -not $HotReload -and (-not $anyTest -or $TestE2e)) {
    Write-Step "Publishing a stable frontend snapshot..."
    $frontSnapshotDir = Join-Path $root $(if ($script:testRun) { "TestResults\YLaunchE2eFront" } else { "TestResults\YLaunchFront" })
    $testResultsRoot = [IO.Path]::GetFullPath((Join-Path $root "TestResults") + [IO.Path]::DirectorySeparatorChar)
    $snapshotPath = [IO.Path]::GetFullPath($frontSnapshotDir)
    if (-not $snapshotPath.StartsWith($testResultsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Frontend snapshot path must stay under TestResults."
    }
    $frontSnapshotSiteDir = Join-Path $frontSnapshotDir "site"
    $frontSnapshotHostDir = Join-Path $frontSnapshotDir "host"
    # Publishing the WebAssembly site takes ~40 s (759 files, each compressed twice) and ran on every
    # launch, changed or not. The snapshot is reused when nothing it is made of changed: the front
    # sources (wwwroot included), the assemblies its build copied (referenced projects, OmniEurope.Blazor
    # and the other packages), the static server and the launch's own appsettings.Development.json
    # (by content: this script rewrites it on every launch). Any doubt republishes.
    $frontSnapshotStamp = Join-Path $frontSnapshotDir "inputs.sha256"
    $frontSnapshotFingerprint = Get-FrontSnapshotFingerprint -Configuration $buildConfiguration
    $frontSnapshotReusable = (Test-Path -LiteralPath $frontSnapshotStamp) -and
        ((Get-Content -LiteralPath $frontSnapshotStamp -Raw).Trim() -eq $frontSnapshotFingerprint) -and
        (Test-Path (Join-Path $frontSnapshotHostDir "wwwroot\index.html")) -and
        (Test-Path (Join-Path $frontSnapshotHostDir "StaticServer.dll"))
    if ($frontSnapshotReusable) {
        Write-Host "  Frontend unchanged since the last snapshot: reused." -ForegroundColor DarkGray
    } else {
        if (Test-Path -LiteralPath $frontSnapshotDir) {
            Remove-Item -LiteralPath $frontSnapshotDir -Recurse -Force
        }
        dotnet publish (Join-Path $frontDir "Aetheus.Front.csproj") -c $buildConfiguration -o $frontSnapshotSiteDir --no-restore -p:RunAnalyzers=false -p:PublishTrimmed=false -p:UseSharedCompilation=false -m:1 -nodeReuse:false --nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "FRONTEND SNAPSHOT FAILED." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
        dotnet publish (Join-Path $root "deploy\docker\StaticServer.csproj") -c Debug -o $frontSnapshotHostDir -p:UseSharedCompilation=false -m:1 -nodeReuse:false --nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "STATIC SERVER SNAPSHOT FAILED." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
        Copy-Item -Path (Join-Path $frontSnapshotSiteDir "wwwroot\*") -Destination (Join-Path $frontSnapshotHostDir "wwwroot") -Recurse -Force -ErrorAction Stop
        Set-Content -LiteralPath $frontSnapshotStamp -Value $frontSnapshotFingerprint -NoNewline
    }
    if (-not (Test-Path (Join-Path $frontSnapshotHostDir "wwwroot\index.html")) -or
        -not (Test-Path (Join-Path $frontSnapshotHostDir "StaticServer.dll"))) {
        throw "Frontend snapshot is incomplete."
    }
}

# ---------- 3. Tests (granular) ----------

$testResults = @()
$testLabels  = @()
# Set when a suite failed: what is left (the coverage report, the E2E stacks, which take minutes) is not run,
# and the combined summary below names the failures and fails the run.
$testsAborted = $false
$testsWatch = [System.Diagnostics.Stopwatch]::StartNew()

if ($TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration) {
    if ($Coverage) {
        if (Test-Path $coverageDir) { Remove-Item $coverageDir -Recurse -Force }
    }

    # Platform-scoped tests ([PlatformFact("windows"|"linux")]) are left out at discovery by
    # tests/Shared/PlatformSpecificTests.cs, not here: putting it in the tests keeps a plain `dotnet test`
    # behaving exactly like this launcher and like CI, instead of failing on the foreign platform.

    # The suites are independent (each has its own project, output directory and TRX; the integration suite
    # brings its own Testcontainers databases), so Invoke-TestSuites runs them together. Under -c they stay
    # one after the other, as coverlet instruments each in turn and the coverage report merges what they wrote.
    $suites = [System.Collections.Generic.List[hashtable]]::new()
    $coverletArgs = if ($Coverage) { @("--coverlet") } else { @() }

    if ($TestBack) {
        $suites.Add(@{ Name = "Back"; Title = "Running backend unit tests..."; Failure = "BACKEND TESTS FAILED"
                       Target = (Join-Path $root "tests\Aetheus.Back.Tests"); ExtraArgs = $coverletArgs })
    }

    if ($TestFront) {
        $suites.Add(@{ Name = "Front"; Title = "Running frontend unit tests..."; Failure = "FRONTEND TESTS FAILED"
                       Target = (Join-Path $root "tests\Aetheus.Front.Tests"); ExtraArgs = $coverletArgs })
    }

    # Agent.Core is the third xUnit suite - part of the unit set (-t) and ALWAYS run under -c so
    # the aggregated coverage % reflects all three xUnit+bUnit suites (Invoke-CoverageReport globs
    # every cobertura XML in $coverageDir, so its output is picked up automatically). It runs in a Linux
    # container, as the CI runs it, never on the host (Start-AgentSuiteContainerRun).
    if ($TestAgent -or $Coverage) {
        $suites.Add(@{ Name = "Agent.Core"; Title = "Running agent-core unit tests..."; Failure = "AGENT-CORE TESTS FAILED"
                       Kind = "agent-container"; ExtraArgs = $coverletArgs })
    }

    # Analyzers is the Roslyn analyzer regression suite - part of the unit set (-t). Excluded from
    # coverage (it exercises the analyzer harness, not product code).
    if ($TestAnalyzers) {
        $suites.Add(@{ Name = "Analyzers"; Title = "Running analyzer tests..."; Failure = "ANALYZER TESTS FAILED"
                       Target = (Join-Path $root "tests\Aetheus.Analyzers.Tests"); ExtraArgs = @() })
    }

    # Package-level suites are mandatory under both -t and -c, but remain outside the product
    # coverage denominator (Back + Front + Agent.Core).
    if ($TestUnit -or $Coverage) {
        foreach ($observabilitySuite in @(
            @{ Path = "tests\Aetheus.Telemetry.Tests"; Label = "Telemetry" },
            @{ Path = "tests\Aetheus.WebAnalytics.Tests"; Label = "WebAnalytics" }
        )) {
            $suites.Add(@{ Name = $observabilitySuite.Label; Title = "Running $($observabilitySuite.Label) package tests..."
                           Failure = "$($observabilitySuite.Label.ToUpperInvariant()) TESTS FAILED"
                           Target = (Join-Path $root $observabilitySuite.Path); ExtraArgs = @() })
        }
    }

    # PLAN-002 lot 9.T: the JavaScript suites belong to the unit set (-t, hence -ta), with the same
    # file sets as the `Compile` step of aetheus-ci and the JavaScript stages of aetheus-quality.
    if ($TestUnit) {
        foreach ($jsSuite in @(
            @{ Files = @(Get-ChildItem -Path (Join-Path $root "tests\js\deploy") -Filter "*.test.mjs" -File); Label = "JS deploy-scripts" },
            @{ Files = @(Get-ChildItem -Path (Join-Path $root "tests\js") -File | Where-Object { $_.Name -like "*.test.cjs" -or $_.Name -like "*.test.mjs" }); Label = "JS tests" }
        )) {
            $files = @($jsSuite.Files | Sort-Object Name | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) })
            $suites.Add(@{ Name = $jsSuite.Label; Title = "Running $($jsSuite.Label) suites..."; Failure = "$($jsSuite.Label.ToUpperInvariant()) TESTS FAILED"
                           Kind = "node"; Files = $files })
        }
        # R-48 (candidate 2967): the locked ESLint, from a clean `npm ci`, in the pipeline's Node image.
        $suites.Add(@{ Name = "JS lint"; Title = "Running the locked ESLint in the pipeline's Node container..."; Failure = "JS LINT FAILED"
                       Kind = "js-lint-container" })
    }

    # PLAN-015 lot 0: the integration tests exercise the controllers and repositories the unit suites do
    # not reach; under -c their coverage is collected too, in its own subdirectory: the product report
    # still merges exactly the three unit suites, the CRAP measure reads every report under the coverage
    # directory. Without -c they run only when asked (-ti), as before.
    if ($Coverage) {
        $suites.Add(@{ Name = "Integration"; Title = "Running integration tests (Testcontainers) with coverage..."; Failure = "INTEGRATION TESTS FAILED"
                       Target = (Join-Path $root "tests\Aetheus.Back.IntegrationTests"); ExtraArgs = @("--coverlet")
                       ResultDirectory = (Join-Path $coverageDir "Integration") })
    } elseif ($TestIntegration) {
        $suites.Add(@{ Name = "Integration"; Title = "Running integration tests (Testcontainers)..."; Failure = "INTEGRATION TESTS FAILED"
                       Target = (Join-Path $root "tests\Aetheus.Back.IntegrationTests"); ExtraArgs = @() })
    }

    $phase = Invoke-TestSuites -Suites $suites.ToArray() -Sequential:($Coverage -or $env:YLAUNCH_SEQUENTIAL_SUITES -eq '1')
    foreach ($suiteResult in $phase.Results) {
        $testResults += $suiteResult
        $testLabels += $suiteResult.Name
    }
    if ($phase.Failed.Count -gt 0) { $testsAborted = $true }

    if ($Coverage -and -not $testsAborted) {
        Invoke-CoverageReport
        if (-not (Invoke-CrapGate -GatePath $crapGate -CoverageRoot $coverageDir -ScoresPath (Join-Path $reportDir "crap-scores.csv"))) {
            Write-Host "CRAP GATE FAILED - aborting." -ForegroundColor Red
            $script:ylaunchExitCode = 1
            return
        }
    }
}

# ---------- 4. E2E tests (needs servers) ----------

if ($TestE2e -and $testsAborted) {
    Write-Host "  E2E tests not started: a suite above failed." -ForegroundColor Yellow
}

if ($TestE2e -and -not $testsAborted) {
    $e2eRunCategories = @()
    # Same dot-source caveat as the implications block: read the value, not $PSBoundParameters.
    $filterProvided = -not [string]::IsNullOrEmpty($TestE2eFilter)
    if ($filterProvided) {
        if ($TestE2eFilter -eq '?') {
            $e2eRunCategories = Show-E2eMenu
        } else {
            $e2eRunCategories = Resolve-E2eCategories $TestE2eFilter
        }
    }

    $e2eExtraArgs = @()
    $e2eLabel     = 'Running E2E tests...'
    # The sweep of every page skips itself unless asked for by name (-tec PageRules) or with the two shards.
    $env:AETHEUS_PAGE_RULES_SWEEP = if (($e2eRunCategories | Where-Object { $E2eManualCategories -icontains $_ }) -or $e2eShardCount -gt 1) { '1' } else { '0' }
    if ($e2eRunCategories.Count -gt 0) {
        $filterParts = @($e2eRunCategories | ForEach-Object { "Category=$_" })
        $filterExpr = [string]::Join('|', $filterParts)
        $e2eExtraArgs = @("--filter", $filterExpr)
        $catList = [string]::Join(', ', $e2eRunCategories)
        $e2eLabel = "Running E2E tests [$catList]..."
    }

    # The browsers are the QA browser image's (Build-E2eRunnerImage), never the Windows host's: no Playwright
    # install on the host and no fallback to a host browser, so a local result is the result the CI gets.
    if (-not (Build-E2eRunnerImage)) {
        Write-Host "The QA browser image could not be built - aborting E2E (no host-browser fallback: it would not match the CI)." -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }

    # E2E runs against its OWN PostgreSQL (:15433), NOT the shared dev DB (:15432). Preserve its
    # dedicated Docker volume; E2ETestBase resets only the guarded aetheus_e2e database through
    # /api/dev/reset-db after the backend starts.
    Ensure-E2eDb

    # A full run is split in two shards that run at the same time, each against a stack of its own (ports,
    # runtime copies, database and file stores: New-E2eStack). A run restricted to categories (-tec) is
    # one shard on the launcher's own stack, as before. The shards partition the suite by category:
    # shard 1 takes $E2eShardOneCategories, shard 2 takes everything that is not in them, so no test
    # can fall between the two.
    $e2eStacks = @(1..$e2eShardCount | ForEach-Object { New-E2eStack -Index $_ })
    $e2eShards = @()
    if ($e2eShardCount -eq 1) {
        $e2eShards += @{ Name = "E2E"; Label = $e2eLabel; Tag = ""; Stack = $e2eStacks[0]; ExtraArgs = $e2eExtraArgs }
    } else {
        $shardOneFilter = [string]::Join('|', @($E2eShardOneCategories | ForEach-Object { "Category=$_" }))
        $shardTwoFilter = [string]::Join('&', @($E2eShardOneCategories | ForEach-Object { "Category!=$_" }))
        $e2eShards += @{ Name = "E2E shard 1"; Label = "Running E2E tests (shard 1/2: $($E2eShardOneCategories -join ', '))..."; Tag = "E2E 1/2"
                         Stack = $e2eStacks[0]; ExtraArgs = @("--filter", $shardOneFilter) }
        $e2eShards += @{ Name = "E2E shard 2"; Label = "Running E2E tests (shard 2/2: all other categories)..."; Tag = "E2E 2/2"
                         Stack = $e2eStacks[1]; ExtraArgs = @("--filter", $shardTwoFilter) }
    }

    $e2eJobs = @{}
    foreach ($stack in $e2eStacks) {
        Initialize-E2eStack $stack
        $stackJobs = Start-Servers -ForE2e -Stack $stack
        foreach ($key in $stackJobs.Keys) { $e2eJobs["$($stack.Name)-$key"] = $stackJobs[$key] }
    }
    $stacksReady = $true
    foreach ($stack in $e2eStacks) {
        $suffix = if ($e2eShardCount -eq 1) { "" } else { " ($($stack.Name))" }
        $backReady  = Wait-ForEndpoint -url $stack.BackUrl  -label "Backend$suffix"  -maxSeconds 60
        $frontReady = Wait-ForEndpoint -url $stack.FrontUrl -label "Frontend$suffix" -maxSeconds 60
        if (-not $backReady -or -not $frontReady) { $stacksReady = $false }
    }
    if (-not $stacksReady) {
        Write-Host "Servers not ready - aborting E2E." -ForegroundColor Red
        Stop-Servers $e2eJobs $true
        $script:ylaunchExitCode = 1
        return
    }
    # The local Blazor development host does not execute deploy/docker/StaticServer.Program.cs.
    # QA does not set this override, so security headers remain mandatory there by default.
    $env:E2E_REQUIRE_FRONTEND_SECURITY_HEADERS = "false"

    if ($e2eShardCount -eq 1) {
        Write-Step $e2eShards[0].Label
    } else {
        Write-Step "Running E2E tests: $e2eShardCount shards in parallel, each on its own stack and database..."
    }
    $e2eRuns = @()
    foreach ($shard in $e2eShards) {
        if ($e2eShardCount -gt 1) {
            Write-Host "  $($shard.Name): back https://localhost:$($shard.Stack.BackHttpsPort), front $($shard.Stack.FrontUrl), database $($shard.Stack.DatabaseName), filter $($shard.ExtraArgs[1])" -ForegroundColor Magenta
        }
        # The suite in the container reads the URLs its stack answers on from there (Start-E2eContainerRun:
        # host.docker.internal and the stack's ports, so a port-overridden worktree is targeted too).
        $e2eRuns += Start-E2eContainerRun -Stack $shard.Stack -ExtraArgs $shard.ExtraArgs -Label $shard.Label -Tag $shard.Tag
    }
    $e2eResults = @(Wait-TestRuns -Runs $e2eRuns -HeaderWithBlock:($e2eShardCount -gt 1))
    Stop-Servers $e2eJobs (@($e2eResults | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0)
    for ($shardIndex = 0; $shardIndex -lt $e2eShards.Count; $shardIndex++) {
        $re = $e2eResults[$shardIndex]
        $testResults += $re; $testLabels += $e2eShards[$shardIndex].Name
        if ($re.ExitCode -ne 0) {
            Write-Host ""
            if ($re.Failed -gt 0) {
                Write-Host "  FAILED: $($re.Failed)/$($re.Total) - $($e2eShards[$shardIndex].Name.ToUpperInvariant()) TESTS FAILED." -ForegroundColor Red
            } else {
                Write-Host "  $($e2eShards[$shardIndex].Name.ToUpperInvariant()) TEST PROCESS FAILED (exit code $($re.ExitCode)) after reporting $($re.Total)/$($re.ExpectedTotal) tests." -ForegroundColor Red
            }
        }
    }
}
$testsWatch.Stop()

# ---------- 5. Combined summary + exit (if any test ran) ----------

if ($testResults.Count -gt 0) {
    $totalPassed  = ($testResults | ForEach-Object { $_.Passed  } | Measure-Object -Sum).Sum
    $totalFailed  = ($testResults | ForEach-Object { $_.Failed  } | Measure-Object -Sum).Sum
    $totalSkipped = ($testResults | ForEach-Object { $_.Skipped } | Measure-Object -Sum).Sum
    $totalAll     = ($testResults | ForEach-Object { $_.Total   } | Measure-Object -Sum).Sum
    $processFailures = @($testResults | Where-Object {
        $_.ExitCode -ne 0 -or $_.Total -le 0 -or ($_.ExpectedTotal -gt 0 -and $_.Total -lt $_.ExpectedTotal)
    })
    $suitesRan    = $testLabels -join " + "
    Write-Host ""
    Write-Host "  =========================================" -ForegroundColor Cyan
    Write-Host "  Combined ($suitesRan)" -ForegroundColor White
    Write-Host "  -----------------------------------------" -ForegroundColor DarkGray
    Write-Host "  Passed : $totalPassed" -ForegroundColor Green
    if ($totalFailed -gt 0) {
        Write-Host "  Failed : $totalFailed" -ForegroundColor Red
    } else {
        Write-Host "  Failed : $totalFailed" -ForegroundColor DarkGray
    }
    if ($totalSkipped -gt 0) { Write-Host "  Skipped: $totalSkipped" -ForegroundColor Yellow }
    if ($processFailures.Count -gt 0) {
        Write-Host "  Process failures: $(($processFailures | ForEach-Object { $_.Label }) -join ', ')" -ForegroundColor Red
    }
    Write-Host "  Total  : $totalAll" -ForegroundColor White
    Write-Host "  =========================================" -ForegroundColor Cyan
    Write-Host ""
    # Suites and E2E shards overlap, so the stage times add up to more than the wall time of the tests.
    Write-Host "  Stage times (the suites of a stage run at the same time):" -ForegroundColor DarkGray
    for ($stageIndex = 0; $stageIndex -lt $testResults.Count; $stageIndex++) {
        Write-Host ("    {0,-18} {1,7:N1}s" -f $testLabels[$stageIndex], $testResults[$stageIndex].ElapsedSec) -ForegroundColor DarkGray
    }
    $stageSum = ($testResults | ForEach-Object { $_.ElapsedSec } | Measure-Object -Sum).Sum
    Write-Host ("  Test stages wall time: {0:N1}s (the stages added up: {1:N1}s)" -f $testsWatch.Elapsed.TotalSeconds, $stageSum) -ForegroundColor DarkGray
    Write-Host ""
    if ($totalFailed -gt 0 -or $processFailures.Count -gt 0) {
        Write-Host "  SOME TESTS FAILED." -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }
    Write-Host "  All tests passed." -ForegroundColor Green
    $script:ylaunchExitCode = 0
    return
}

# ---------- 6. Reset database (-r without tests) ----------

if ($Reset) {
    Reset-DevDb
} else {
    Ensure-DevDb
}

# ---------- 6b. Seed accounts (kit launcher core 1.0.5, "Seed accounts" of the deployment contract) ----------
# The development logins a start prints, so nobody has to look them up. Each entry names the configuration
# key the seeder reads (DbInitializer: Auth:AdminPassword) and the project that reads it; the value is
# resolved like ASP.NET Development does, highest precedence first: environment variable (':' -> '__'),
# user secrets of the project, then its appsettings.Development.json and appsettings.json. A key found
# nowhere prints "password not found": a password is never invented. Never shown during a test run.
$seedAccounts = @(
    @{ Login = "admin@aetheus.local"; PasswordKey = "Auth:AdminPassword"; Project = "src\Aetheus.Back\Aetheus.Back.csproj"; Role = "Admin" }
)

function Test-YSeedAccounts([object[]]$Accounts) {
    foreach ($a in @($Accounts | Where-Object { $_ })) {
        if ($a -isnot [hashtable] -or [string]::IsNullOrWhiteSpace([string]$a.Login)) { throw "Every seed account entry needs a Login: @{ Login = 'admin'; PasswordKey = '...'; Project = '...' }." }
        if ([string]::IsNullOrWhiteSpace([string]$a.PasswordKey)) { throw "Seed account '$($a.Login)' needs the PasswordKey the seeder reads." }
        if ([string]::IsNullOrWhiteSpace([string]$a.Project)) { throw "Seed account '$($a.Login)': PasswordKey needs the Project that reads it." }
    }
}

function Get-YJsonKey([string]$File, [string]$Key) {
    if (-not (Test-Path -LiteralPath $File)) { return $null }
    try { $node = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json -AsHashtable } catch { return $null }
    foreach ($part in $Key.Split(':')) {
        if ($node -isnot [System.Collections.IDictionary]) { return $null }
        $match = @($node.Keys | Where-Object { $_ -ieq $part }) | Select-Object -First 1
        if ($null -eq $match) { return $null }
        $node = $node[$match]
    }
    if ($node -is [string] -and $node) { return $node }
    return $null
}

function Resolve-YSeedPassword([hashtable]$Account, [string]$Root, [hashtable]$SecretsCache) {
    $key = [string]$Account.PasswordKey
    $fromEnv = [Environment]::GetEnvironmentVariable($key.Replace(':', '__'))
    if ($fromEnv) { return @{ Value = $fromEnv; Source = "env $($key.Replace(':', '__'))" } }
    $project = Join-Path $Root $Account.Project
    if (-not $SecretsCache.ContainsKey($project)) {
        $pairs = @{}
        if (Test-Path -LiteralPath $project) {
            foreach ($line in @(& dotnet user-secrets list --project $project 2>$null)) {
                if ("$line" -match '^\s*(.+?)\s+=\s+(.*)$') { $pairs[$Matches[1]] = $Matches[2] }
            }
        }
        $SecretsCache[$project] = $pairs
    }
    $secret = @($SecretsCache[$project].Keys | Where-Object { $_ -ieq $key }) | Select-Object -First 1
    if ($secret) { return @{ Value = [string]$SecretsCache[$project][$secret]; Source = 'user secrets' } }
    $folder = Split-Path -Parent $project
    foreach ($name in 'appsettings.Development.json', 'appsettings.json') {
        $value = Get-YJsonKey (Join-Path $folder $name) $key
        if ($value) { return @{ Value = $value; Source = $name } }
    }
    return $null
}

function Show-YSeedAccounts([object[]]$Accounts, [string]$Root) {
    if ($Accounts.Count -eq 0) { return }
    Test-YSeedAccounts $Accounts
    $cache = @{}
    Write-Host ""
    Write-Host "Seed accounts (development):" -ForegroundColor Cyan
    foreach ($a in $Accounts) {
        $resolved = Resolve-YSeedPassword $a $Root $cache
        $role = if ($a.Role) { "  [$($a.Role)]" } else { '' }
        if ($resolved) {
            Write-Host ("  {0}  /  {1}{2}" -f $a.Login, $resolved.Value, $role) -ForegroundColor White -NoNewline
            Write-Host "  ($($resolved.Source))" -ForegroundColor DarkGray
        } else {
            Write-Host ("  {0}  /  password not found (key {1}: env, user secrets, appsettings){2}" -f $a.Login, $a.PasswordKey, $role) -ForegroundColor Yellow
        }
    }
}

# ---------- 7. Start servers ----------

$servers = Start-Servers

# ---------- 8. Wait for backend + frontend, open browser ----------

if ($ModeFront) {
    $backReady = Wait-ForEndpoint -url $backUrl -label "Backend" -maxSeconds 30
    $frontReady = $false
    if ($backReady) {
        $frontReady = Wait-ForEndpoint -url $frontUrl -label "Frontend" -maxSeconds 30
    }
    if (-not $backReady -or -not $frontReady) {
        Write-Host "  Server startup failed. Dumping captured output and shutting down." -ForegroundColor Red
        Stop-Servers $servers $true
        $script:ylaunchExitCode = 1
        return
    }

    if (-not $Silent) {
        Write-Step "Opening browser -> $frontUrl"
        Start-Process $frontUrl
    } else {
        Write-Step "Servers ready (browser open skipped with -s)"
    }
    Show-YSeedAccounts $seedAccounts $root
} else {
    Write-Step "Agent started (no frontend in this mode)"
}

# ---------- 9. Stream output until Ctrl+C ----------

Write-Host ""
Write-Host "Press Ctrl+C to stop all services." -ForegroundColor Magenta
if ($env:YLAUNCH_VERBOSE -ne '1') { Write-Host "Only warnings and errors are shown from here (YLAUNCH_VERBOSE=1 shows every log line)." -ForegroundColor DarkGray }
Write-Host ""

# S-UX-36: each server job emits a "##PROMEXIT##<code>" marker as its final line so the teardown can
# report which component went down AND its exit code (a non-zero `dotnet run` exit leaves the job state
# at Completed, so the state alone cannot distinguish a clean stop from a crash).
$exitCodes = @{}

# Recette R-270: once the services are up, the console only relays what needs attention, warnings and
# errors. A .NET console log entry is a "level: Category[id]" line followed by indented lines; the
# whole entry is shown or hidden by its level. A line outside that format (dotnet watch, a stack
# trace start) is shown only when it names a warning or an error. YLAUNCH_VERBOSE=1 relays everything.
$relayAll = $env:YLAUNCH_VERBOSE -eq '1'
$relayVisible = @{ Back = $false; Front = $false; Agent = $false }
function Write-ServerLine([string]$label, [string]$line, [string]$color) {
    $prefix = "[$($label.ToUpperInvariant())]".PadRight(8)
    if ($relayAll) { Write-Host "$prefix$line" -ForegroundColor $color; return }
    if ($line -match '^(info|dbug|trce):\s') { $relayVisible[$label] = $false; return }
    if ($line -match '^(warn|fail|crit):\s') {
        $relayVisible[$label] = $true
        $levelColor = if ($line -match '^warn:') { 'Yellow' } else { 'Red' }
        Write-Host "$prefix$line" -ForegroundColor $levelColor
        return
    }
    if ($line -match '^\s') {
        if ($relayVisible[$label]) { Write-Host "$prefix$line" -ForegroundColor $color }
        return
    }
    $relayVisible[$label] = $line -match '(?i)\b(warn(ing)?|error|fail(ed|ure)?|exception|crit(ical)?)\b|[⚠❌]'
    if ($relayVisible[$label]) { Write-Host "$prefix$line" -ForegroundColor $color }
}

try {
    while ($true) {
        if ($servers.ContainsKey('BackJob')) {
            Receive-Job -Job $servers.BackJob  -ErrorAction SilentlyContinue |
                ForEach-Object { if ($_ -match '^##PROMEXIT##(-?\d+)$') { $exitCodes['Back'] = $matches[1] } else { Write-ServerLine 'Back' "$_" 'DarkCyan' } }
        }
        if ($servers.ContainsKey('FrontJob')) {
            Receive-Job -Job $servers.FrontJob -ErrorAction SilentlyContinue |
                ForEach-Object { if ($_ -match '^##PROMEXIT##(-?\d+)$') { $exitCodes['Front'] = $matches[1] } else { Write-ServerLine 'Front' "$_" 'DarkGreen' } }
        }
        if ($servers.ContainsKey('AgentJob')) {
            Receive-Job -Job $servers.AgentJob -ErrorAction SilentlyContinue |
                ForEach-Object { if ($_ -match '^##PROMEXIT##(-?\d+)$') { $exitCodes['Agent'] = $matches[1] } else { Write-ServerLine 'Agent' "$_" 'DarkYellow' } }
        }
        # If ANY launched service reaches a terminal state - crashed (Failed), exited cleanly
        # (Completed), or was killed externally (Stopped) - tear everything down. The services
        # live and die together: a half-running stack (e.g. backend gone, frontend orphaned) is
        # never what we want, and it leaves ylaunch streaming into the void.
        $terminalStates = @('Completed', 'Failed', 'Stopped')
        $downKey = @('BackJob', 'FrontJob', 'AgentJob') |
            Where-Object { $servers.ContainsKey($_) -and $terminalStates -contains $servers[$_].State } |
            Select-Object -First 1
        if ($downKey) {
            $label = $downKey -replace 'Job', ''
            $state = $servers[$downKey].State
            # Drain the final job output so a trailing ##PROMEXIT## marker is captured before we read it.
            $tail = Receive-Job -Job $servers[$downKey] -ErrorAction SilentlyContinue
            foreach ($line in $tail) {
                if ($line -match '^##PROMEXIT##(-?\d+)$') { $exitCodes[$label] = $matches[1] }
                else { Write-Host "  $line" -ForegroundColor DarkGray }
            }
            $exitInfo = if ($exitCodes.ContainsKey($label)) { "exit code: $($exitCodes[$label])" } else { "exit code: unknown" }
            $color = if ($state -eq 'Failed' -or ($exitCodes.ContainsKey($label) -and $exitCodes[$label] -ne '0')) { 'Red' } else { 'Yellow' }
            Write-Host "$label stopped (state: $state, $exitInfo) - shutting down all services." -ForegroundColor $color
            break
        }
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($sshProc -and -not $sshProc.HasExited) {
        Write-Host "  Closing SSH tunnel (PID $($sshProc.Id))..." -ForegroundColor DarkGray
        Stop-Process -Id $sshProc.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-Servers $servers
}
