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
# Every build output read after the build (runtime snapshots, the front publish, playwright.ps1)
# follows this value: `-c -te` builds Release, and E2E used to copy and run stale Debug outputs.
$anyTest = $TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration -or $TestE2e
$e2eOnly = $TestE2e -and -not ($TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration -or $Coverage)
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
    Write-Host "  -t,  -TestUnit         Run ALL unit tests (back + front + agent-core + analyzers + observability)"
    Write-Host "  -ti, -TestIntegration  Run integration tests (Aetheus.Back.IntegrationTests, Testcontainers)"
    Write-Host "  -te                    Run ALL E2E tests (isolated dedicated DB :15433, dev DB untouched)"
    Write-Host "  -tec <filter>          Run specific E2E categories (implies -te)"
    Write-Host "    -tec ?               Show interactive category picker"
    Write-Host "    -tec 1               Category by number"
    Write-Host "    -tec 1,2,3           Categories by number"
    Write-Host "    -tec Auth            Category by name"
    Write-Host "    -tec Auth,Acts,3     Mix of names and numbers"
    Write-Host "  -ta, -TestAll          Run EVERYTHING: unit + integration + E2E (dev DB never reset)"
    Write-Host "  -c,  -Coverage         Collect coverage + open HTML report (Back+Front+Agent.Core; integration coverage kept beside it, implies -tb -tf)"
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
$script:killPorts = @($backHttpsPort, $frontPort)
$frontUrl     = "https://localhost:$frontPort"
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
$e2eConnString = "Host=localhost;Port=15433;Database=aetheus_e2e;Username=postgres;Password=postgres"
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
        $adminUser = if ($env:VPSSIM_ADMIN_USER) { $env:VPSSIM_ADMIN_USER } else { [string]$developmentAuth.AdminUser }
        $adminPassword = if ($env:VPSSIM_ADMIN_PASSWORD) { $env:VPSSIM_ADMIN_PASSWORD } else { [string]$developmentAuth.AdminPassword }
        if ([string]::IsNullOrWhiteSpace($adminUser) -or [string]::IsNullOrWhiteSpace($adminPassword)) {
            throw "Development admin credentials are missing from appsettings.Development.json."
        }
        try {
            $loginBody = @{ username = $adminUser; password = $adminPassword } | ConvertTo-Json -Compress
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

function Invoke-TestRun {
    param(
        [string[]]$ExtraArgs = @(),
        [string]$Label = "Running tests...",
        [string]$Target = "",
        [switch]$StreamOutput,
        # Where TRX and Cobertura land instead of the default (PLAN-015: the integration coverage goes
        # beside the product report, which merges exactly the three unit suites).
        [string]$ResultDirectory = ""
    )
    if (-not $Target) { $Target = $solution }
    Write-Step $Label

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

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $testIndex = 0
    $streamPassed = 0
    $streamFailed = 0
    $streamSkipped = 0
    $streamTotal = 0
    $processStreamLine = {
        param([string]$line)
        # Microsoft.Testing.Platform, --output Detailed: "passed <test> (209ms)", then a "from <dll>" line.
        if ($line -match '^(passed|failed|skipped)\s+(.+?)\s+\(([^()]+)\)\s*$') {
            $testIndex++
            $status = $Matches[1]
            $testName = $Matches[2]
            $duration = $Matches[3].Trim()
            $pad = if ($streamTotal -gt 0) { "$streamTotal".Length } else { 2 }
            $idx = "$testIndex".PadLeft($pad, '0')
            $tot = if ($streamTotal -gt 0) { "/$streamTotal" } else { "" }
            if ($status -eq 'passed') {
                $streamPassed++
                Write-Host "  [${idx}${tot}] PASS " -NoNewline -ForegroundColor Green
                Write-Host "$testName " -NoNewline
                Write-Host "($duration)" -ForegroundColor DarkGray
            } elseif ($status -eq 'skipped') {
                $streamSkipped++
                Write-Host "  [${idx}${tot}] SKIP " -NoNewline -ForegroundColor Yellow
                Write-Host "$testName " -NoNewline -ForegroundColor Yellow
                Write-Host "($duration)" -ForegroundColor DarkGray
            } else {
                $streamFailed++
                Write-Host "  [${idx}${tot}] FAIL " -NoNewline -ForegroundColor Red
                Write-Host "$testName " -NoNewline -ForegroundColor Red
                Write-Host "($duration)" -ForegroundColor DarkGray
            }
        }
    }

    $outputReader = $null
    try {
        if ($StreamOutput) {
            # Keep one shared reader at its current byte position. Re-running Get-Content against
            # the entire growing log every 500 ms made streaming I/O quadratic for long suites.
            $outputFile = [System.IO.File]::Open(
                $tmpOut,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
            $outputReader = [System.IO.StreamReader]::new($outputFile)
        }

        while (-not $proc.HasExited) {
            if ($outputReader) {
                while ($null -ne ($line = $outputReader.ReadLine())) {
                    . $processStreamLine $line
                }
            } else {
                Write-Host "`r  Elapsed: $([int]$sw.Elapsed.TotalSeconds)s   " -NoNewline -ForegroundColor DarkGray
            }
            Start-Sleep -Milliseconds 500
        }

        $proc.WaitForExit()
        if ($outputReader) {
            while ($null -ne ($line = $outputReader.ReadLine())) {
                . $processStreamLine $line
            }
        }
    } finally {
        if ($outputReader) {
            $outputReader.Dispose()
        }
    }
    Write-Host ""
    $sw.Stop()
    $exitCode = $proc.ExitCode
    $trxPath = Join-Path $resultDirectory $trxName
    $trxCounters = $null
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
    $expectedTotal = $streamTotal
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
    } elseif (-not $summaryLine -and $StreamOutput -and ($streamPassed + $streamFailed + $streamSkipped) -gt 0) {
        $completedTotal = $streamPassed + $streamFailed + $streamSkipped
        $summaryLine = "Test summary: total: ${completedTotal}; failed: ${streamFailed}; succeeded: ${streamPassed}; skipped: ${streamSkipped}"
    }
    Write-TestSummary -summaryLine "$summaryLine" -elapsedSec $sw.Elapsed.TotalSeconds

    $passed = 0; $failed = 0; $total = 0; $skipped = 0
    if ($summaryLine -match "total:\s*(\d+).*?failed:\s*(\d+).*?succeeded:\s*(\d+)") {
        $total  = [int]$Matches[1]; $failed = [int]$Matches[2]; $passed = [int]$Matches[3]
        if ($summaryLine -match "skipped:\s*(\d+)") { $skipped = [int]$Matches[1] }
    } elseif ($StreamOutput -and ($streamPassed + $streamFailed) -gt 0) {
        $passed = $streamPassed; $failed = $streamFailed; $skipped = $streamSkipped
        $total = $streamPassed + $streamFailed + $streamSkipped
    }
    return @{
        Label = $Label
        ExitCode = $exitCode
        Passed = $passed
        Failed = $failed
        Total = $total
        ExpectedTotal = $expectedTotal
        Skipped = $skipped
    }
}

function Invoke-CoverageReport {
    Write-Step "Generating HTML coverage report..."
    # coverlet.MTP writes coverage.cobertura.<timestamp>.xml at the top of the results directory. Give
    # each report the <id>/coverage.cobertura.xml layout the CI readers expect (as
    # deploy/scripts/normalize-coverage-report.sh does there), then merge exactly the three suites.
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
    'ProductionSmoke'
)

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

function Start-Servers {
    param([switch]$ForE2e)

    $jobs = @{}
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
    # "Invalid username or password." Disable the limiter for the E2E backend
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
            Write-Step "Starting Backend snapshot (https://localhost:$backHttpsPort)..."
            $jobs.BackJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                if ($using:e2eEnv) {
                    $env:RateLimiting__Disabled = "true"
                    # The authenticated reset endpoint recreates the complete schema after startup.
                    # Delay the immediate task sweep so it cannot query a table between DROP SCHEMA
                    # and the last migration. Ordinary dev/prod hosts retain the zero-delay default.
                    $env:BackgroundServices__TaskStartupDelay = "00:30:00"
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
                $env:ASPNETCORE_ENVIRONMENT = "Development"
                $env:ASPNETCORE_URLS = $using:backAspUrls
                $env:Cors__FrontendUrl = $using:corsFrontUrl
                $env:Aetheus__PublicApiBaseUrl = $using:backPublicApiUrl
                Set-Location $using:backDir
                dotnet (Join-Path $using:backSnapshotDir "Aetheus.Back.dll") 2>&1; "##PROMEXIT##$LASTEXITCODE"
            }
            Write-Step "Starting Frontend snapshot (https://localhost:$frontPort)..."
            $jobs.FrontJob = Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock {
                $env:ASPNETCORE_ENVIRONMENT = "Development"
                $env:ASPNETCORE_URLS = $using:frontAspUrls
                $env:API_BASE_URL = $using:frontApiBaseUrl
                Set-Location $using:frontSnapshotHostDir
                dotnet (Join-Path $using:frontSnapshotHostDir "StaticServer.dll") 2>&1; "##PROMEXIT##$LASTEXITCODE"
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
    $source = [regex]::Escape((Join-Path $root "src") + [IO.Path]::DirectorySeparatorChar)
    $snapshots = [regex]::Escape((Join-Path $root "TestResults") + [IO.Path]::DirectorySeparatorChar + "YLaunch")
    "($source|$snapshots)"
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

Write-Step "Killing existing Aetheus processes..."

Stop-AetheusProcesses

Start-Sleep -Seconds 1

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
    if ($e2eOnly) {
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
    Write-Host "  Build succeeded." -ForegroundColor Green

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
    $backSnapshotDir = Copy-RuntimeSnapshot "YLaunchBack" (Join-Path $backDir "bin\$buildConfiguration\net10.0") "Aetheus.Back.dll"
}
if ($ModeAgent -and -not $HotReload -and -not $anyTest) {
    Write-Step "Copying Agent runtime snapshot..."
    $agentSnapshotDir = Copy-RuntimeSnapshot "YLaunchAgent" (Join-Path $agentDir "bin\$buildConfiguration\net10.0-windows") "Aetheus.Agent.Windows.dll"
}
if ($ModeFront -and -not $HotReload -and (-not $anyTest -or $TestE2e)) {
    Write-Step "Publishing a stable frontend snapshot..."
    $frontSnapshotDir = Join-Path $root "TestResults\YLaunchFront"
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

if ($TestBack -or $TestFront -or $TestAgent -or $TestAnalyzers -or $TestIntegration) {
    if ($Coverage) {
        if (Test-Path $coverageDir) { Remove-Item $coverageDir -Recurse -Force }
    }

    # Platform-scoped tests ([PlatformFact("windows"|"linux")]) are left out at discovery by
    # tests/Shared/PlatformSpecificTests.cs, not here: putting it in the tests keeps a plain `dotnet test`
    # behaving exactly like this launcher and like CI, instead of failing on the foreign platform.

    if ($TestBack) {
        $backTestDir = Join-Path $root "tests\Aetheus.Back.Tests"
        if ($Coverage) {
            $extraBack = @("--coverlet")
        } else {
            $extraBack = @()
        }
        $rb = Invoke-TestRun -Target $backTestDir -ExtraArgs $extraBack -Label "Running backend unit tests..."
        $testResults += $rb; $testLabels += "Back"
        if ($rb.ExitCode -ne 0) { Write-Host "BACKEND TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }

    if ($TestFront) {
        $frontTestDir = Join-Path $root "tests\Aetheus.Front.Tests"
        if ($Coverage) {
            $extraFront = @("--coverlet")
        } else {
            $extraFront = @()
        }
        $rf = Invoke-TestRun -Target $frontTestDir -ExtraArgs $extraFront -Label "Running frontend unit tests..."
        $testResults += $rf; $testLabels += "Front"
        if ($rf.ExitCode -ne 0) { Write-Host "FRONTEND TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }

    # Agent.Core is the third xUnit suite - part of the unit set (-t) and ALWAYS run under -c so
    # the aggregated coverage % reflects all three xUnit+bUnit suites (Invoke-CoverageReport globs
    # every cobertura XML in $coverageDir, so its output is picked up automatically).
    if ($TestAgent -or $Coverage) {
        $agentTestDir = Join-Path $root "tests\Aetheus.Agent.Core.Tests"
        if ($Coverage) {
            $extraAgent = @("--coverlet")
        } else {
            $extraAgent = @()
        }
        $ra = Invoke-TestRun -Target $agentTestDir -ExtraArgs $extraAgent -Label "Running agent-core unit tests..."
        $testResults += $ra; $testLabels += "Agent.Core"
        if ($ra.ExitCode -ne 0) { Write-Host "AGENT-CORE TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }

    # Analyzers is the Roslyn analyzer regression suite - part of the unit set (-t). Excluded from
    # coverage (it exercises the analyzer harness, not product code).
    if ($TestAnalyzers) {
        $analyzersTestDir = Join-Path $root "tests\Aetheus.Analyzers.Tests"
        $ran = Invoke-TestRun -Target $analyzersTestDir -Label "Running analyzer tests..."
        $testResults += $ran; $testLabels += "Analyzers"
        if ($ran.ExitCode -ne 0) { Write-Host "ANALYZER TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }

    # Package-level suites are mandatory under both -t and -c, but remain outside the product
    # coverage denominator (Back + Front + Agent.Core).
    if ($TestUnit -or $Coverage) {
        foreach ($observabilitySuite in @(
            @{ Path = "tests\Aetheus.Telemetry.Tests"; Label = "Telemetry" },
            @{ Path = "tests\Aetheus.WebAnalytics.Tests"; Label = "WebAnalytics" }
        )) {
            $suiteDir = Join-Path $root $observabilitySuite.Path
            $result = Invoke-TestRun -Target $suiteDir -Label "Running $($observabilitySuite.Label) package tests..."
            $testResults += $result; $testLabels += $observabilitySuite.Label
            if ($result.ExitCode -ne 0) {
                Write-Host "$($observabilitySuite.Label.ToUpperInvariant()) TESTS FAILED - aborting." -ForegroundColor Red
                $script:ylaunchExitCode = 1
                return
            }
        }
    }

    # PLAN-015 lot 0: the integration tests exercise the controllers and repositories the unit suites do
    # not reach; under -c their coverage is collected too, in its own subdirectory: the product report
    # still merges exactly the three unit suites, the CRAP measure reads every report under the coverage
    # directory. Without -c they run only when asked (-ti), as before.
    if ($Coverage) {
        $integrationTestDir = Join-Path $root "tests\Aetheus.Back.IntegrationTests"
        $rci = Invoke-TestRun -Target $integrationTestDir -ExtraArgs @("--coverlet") -ResultDirectory (Join-Path $coverageDir "Integration") -Label "Running integration tests (Testcontainers) with coverage..."
        $testResults += $rci; $testLabels += "Integration"
        if ($rci.ExitCode -ne 0) { Write-Host "INTEGRATION TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }

    if ($Coverage) { Invoke-CoverageReport }

    if ($TestIntegration -and -not $Coverage) {
        $integrationTestDir = Join-Path $root "tests\Aetheus.Back.IntegrationTests"
        $ri = Invoke-TestRun -Target $integrationTestDir -Label "Running integration tests (Testcontainers)..."
        $testResults += $ri; $testLabels += "Integration"
        if ($ri.ExitCode -ne 0) { Write-Host "INTEGRATION TESTS FAILED - aborting." -ForegroundColor Red; $script:ylaunchExitCode = 1; return }
    }
}

# ---------- 4. E2E tests (needs servers) ----------

if ($TestE2e) {
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
    if ($e2eRunCategories.Count -gt 0) {
        $filterParts = @($e2eRunCategories | ForEach-Object { "Category=$_" })
        $filterExpr = [string]::Join('|', $filterParts)
        $e2eExtraArgs = @("--filter", $filterExpr)
        $catList = [string]::Join(', ', $e2eRunCategories)
        $e2eLabel = "Running E2E tests [$catList]..."
    }

    # Self-provision Playwright browsers so -te works on any machine without a
    # manual `playwright.ps1 install`. The generated playwright.ps1 appears
    # after the solution build above; `install` is idempotent (skips
    # already-present builds) and pinned to the Microsoft.Playwright NuGet
    # version, so it stays correct across package upgrades. Fail fast here
    # rather than letting every test fail with "Executable doesn't exist".
    $playwrightScript = Join-Path $root "Tests\Aetheus.E2E\bin\$buildConfiguration\net10.0\playwright.ps1"
    if (-not (Test-Path $playwrightScript)) {
        Write-Host "playwright.ps1 not found at $playwrightScript - solution not built? Aborting E2E." -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }
    Write-Step "Ensuring Playwright browsers (idempotent)..."
    & pwsh -NoProfile -File $playwrightScript install
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Playwright browser install failed (exit $LASTEXITCODE) - aborting E2E." -ForegroundColor Red
        $script:ylaunchExitCode = 1
        return
    }
    Write-Host "  Playwright browsers ready." -ForegroundColor Green

    # E2E runs against its OWN PostgreSQL (:15433), NOT the shared dev DB (:15432). Preserve its
    # dedicated Docker volume; E2ETestBase resets only the guarded aetheus_e2e database through
    # /api/dev/reset-db after the backend starts.
    Ensure-E2eDb

    $servers = Start-Servers -ForE2e
    $backReady  = Wait-ForEndpoint -url $backUrl  -label "Backend"  -maxSeconds 60
    $frontReady = Wait-ForEndpoint -url $frontUrl -label "Frontend" -maxSeconds 60
    if (-not $backReady -or -not $frontReady) {
        Write-Host "Servers not ready - aborting E2E." -ForegroundColor Red
        Stop-Servers $servers $true
        $script:ylaunchExitCode = 1
        return
    }
    # Tell the E2E suite which ports the stack is actually on (E2ETestBase reads these, falling back
    # to the 5301/5401 defaults). Without this, a port-overridden worktree (.ylaunch.local) starts the
    # servers on its own ports but Playwright would target the defaults and fail to connect.
    $env:E2E_FRONTEND_URL = "https://localhost:$frontPort"
    $env:E2E_BACKEND_URL  = "https://localhost:$backHttpsPort"
    # The local Blazor development host does not execute deploy/docker/StaticServer.Program.cs.
    # QA does not set this override, so security headers remain mandatory there by default.
    $env:E2E_REQUIRE_FRONTEND_SECURITY_HEADERS = "false"
    $re = Invoke-TestRun -Target (Join-Path $root "tests\Aetheus.E2E") `
                         -ExtraArgs $e2eExtraArgs -Label $e2eLabel -StreamOutput
    Stop-Servers $servers ($re.ExitCode -ne 0)
    $testResults += $re; $testLabels += "E2E"
    if ($re.ExitCode -ne 0) {
        Write-Host ""
        if ($re.Failed -gt 0) {
            Write-Host "  FAILED: $($re.Failed)/$($re.Total) - E2E TESTS FAILED." -ForegroundColor Red
        } else {
            Write-Host "  E2E TEST PROCESS FAILED (exit code $($re.ExitCode)) after reporting $($re.Total)/$($re.ExpectedTotal) tests." -ForegroundColor Red
        }
    }
}

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
    @{ Login = "admin"; PasswordKey = "Auth:AdminPassword"; Project = "src\Aetheus.Back\Aetheus.Back.csproj"; Role = "Admin" }
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
