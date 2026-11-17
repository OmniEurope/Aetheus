# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
#
# VPS bench (ylaunch -vb): runs the integration suite (level 1) or the E2E suite (level 2) inside Docker
# containers whose CPU and memory are limited, to reproduce the failures a candidate shows on the production VPS
# and never on a fast developer PC (candidate 339: two tests green locally, flaky on the VPS).
#
# Only the containers are limited, never the host. The CPU limit is a cpuset (the same N cores of the Docker VM for
# EVERY container of the bench, so they share N cores as the processes of one VPS do) plus `--cpus N` on each.
# The default of 4 CPUs and 8 GB is an ESTIMATE of the VPS: its memory is 15.6 GB, its core count is not measured.
#
# Dot-sourced by launch-core.ps1 once its helpers (Write-Step, Build-E2eRunnerImage, $e2eRunnerScript) exist.
# Every container, network, volume and image the bench creates is named with the `aetheus-bench-` prefix and removed
# at the end of the run, by name only, on every path.

$benchPrefix = "aetheus-bench"
$benchResultsRoot = Join-Path $root "TestResults\VpsBench"
$benchNuGetVolume = "$benchPrefix-nuget"
$benchWorkDir = "/work"
$benchResultsInContainer = "/tmp/aetheus-bench-results"

# Level 1: build the integration suite in Release in a container of the pinned SDK, then run it as the QA stage
# does (`dotnet test --project <tests> --no-build`, Microsoft.Testing.Platform, the same parallelism). The test
# host boots the real backend in-process and starts its PostgreSQL through Testcontainers on the host's Docker
# socket; that container carries the bench's name label, CPU set and memory limit
# (AETHEUS_TESTCONTAINERS_POSTGRES_* read by SharedPostgresContainer.RunLabels).
$benchIntegrationScript = @'
set -u
RESULTS=/tmp/aetheus-bench-results
rm -rf "$RESULTS" /work /tmp/src.tar
mkdir -p "$RESULTS" /work
{ uname -sr; dotnet --version; echo "cpus=$(nproc)"; } | tr '\n' ' ' > "$RESULTS/platform.txt"
echo "Copying the checkout into the container..."
tar -C /src -cf /tmp/src.tar --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules \
  --exclude=./TestResults --exclude=./.claude/worktrees --exclude=./.worktrees --exclude=./.pipeline-publish \
  --exclude=./src/Aetheus.Back/wwwroot/downloads . || { echo "Could not read the checkout." >&2; exit 1; }
tar -C /work -xf /tmp/src.tar || { echo "Could not copy the checkout." >&2; exit 1; }
rm -f /tmp/src.tar
# A Linux checkout holds LF where this Windows checkout holds CRLF; see the agent suite container of ylaunch.
grep -rIlZ -e "$(printf '\r')" /work --exclude='*.ps1' --exclude='*.cmd' --exclude='*.bat' | xargs -0 -r sed -i 's/\r$//'
cd /work
# The first IPv4 address of the host: a name makes Npgsql try the IPv6 one first, unreachable from the Docker network.
TESTCONTAINERS_HOST_OVERRIDE="$(getent ahostsv4 host.docker.internal | awk 'NR==1{print $1}')"
export TESTCONTAINERS_HOST_OVERRIDE
echo "Testcontainers host: $TESTCONTAINERS_HOST_OVERRIDE"
echo "Building the integration suite (Release)..."
BUILD_START=$(date +%s)
dotnet build tests/Aetheus.Back.IntegrationTests --configuration Release --nologo -v q -p:RunAnalyzers=false -p:TreatWarningsAsErrors=false 2>&1 | tail -n 15
BUILD_EXIT=${PIPESTATUS[0]}
[ "$BUILD_EXIT" = 0 ] || { echo "Build failed (exit $BUILD_EXIT)." >&2; exit "$BUILD_EXIT"; }
echo "Build seconds: $(( $(date +%s) - BUILD_START ))"
RAW_EXIT=0
set -f
TEST_START=$(date +%s)
dotnet test --project tests/Aetheus.Back.IntegrationTests --configuration Release --no-build --no-restore --no-progress \
  --report-trx --report-trx-filename integration.trx --results-directory "$RESULTS" $BENCH_TEST_ARGS || RAW_EXIT=$?
echo "Test seconds: $(( $(date +%s) - TEST_START ))" | tee "$RESULTS/test-seconds.txt"
printf "%s\n" "$RAW_EXIT" > "$RESULTS/raw-exit-code"
exit "$RAW_EXIT"
'@ -replace "`r`n", "`n"

function Get-VpsBenchSdkImage {
    $dockerfile = Join-Path $root "deploy\docker\Dockerfile.back"
    $match = Select-String -LiteralPath $dockerfile -Pattern '^FROM\s+(mcr\.microsoft\.com/dotnet/sdk:\S+)\s+AS\s+build-source\s*$' | Select-Object -First 1
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value
}

# The limits of one bench run, validated against the Docker VM (never more than it has).
function Get-VpsBenchLimits([int]$Cpus, [int]$MemoryGb) {
    $vmCpus = [int](& docker info --format '{{.NCPU}}' 2>$null | Select-Object -First 1)
    if ($Cpus -lt 1 -or $Cpus -gt $vmCpus) {
        throw "BenchCpus must be between 1 and $vmCpus (the CPUs of the Docker VM), got $Cpus."
    }
    if ($MemoryGb -lt 4) { throw "BenchMemoryGb must be at least 4 (the backend alone is limited to 2 GB in QA), got $MemoryGb." }
    $vmMemoryGb = [math]::Floor([double](& docker info --format '{{.MemTotal}}' 2>$null | Select-Object -First 1) / 1GB)
    if ($MemoryGb -gt $vmMemoryGb) { throw "BenchMemoryGb must not exceed the $vmMemoryGb GB of the Docker VM, got $MemoryGb." }
    return @{
        Cpus = $Cpus
        CpuSet = if ($Cpus -eq 1) { "0" } else { "0-$($Cpus - 1)" }
        MemoryGb = $MemoryGb
        # The share of the memory kept for PostgreSQL; the runner (level 1: the test host) takes the rest.
        DatabaseMb = [int]([math]::Max(1024, [math]::Floor($MemoryGb * 1024 / 4)))
    }
}

function Remove-VpsBenchContainers([string]$RunId) {
    # By label (Testcontainers' own containers) and by name: only this run's, never a pattern of other projects.
    $labelled = @(& docker ps -a -q --filter "label=aetheus.testcontainers.run=$benchPrefix-$RunId" 2>$null)
    if ($labelled.Count -gt 0) { & docker rm -f @labelled 2>&1 | Out-Null }
    $named = @(& docker ps -a --format '{{.Names}}' 2>$null | Where-Object { $_ -ceq "$benchPrefix-tests-$RunId" -or $_ -ceq "$benchPrefix-e2e-$RunId" -or $_ -ceq "$benchPrefix-publish-$RunId" -or $_ -ceq "$benchPrefix-cli-$RunId" })
    foreach ($name in $named) { & docker rm -f $name 2>&1 | Out-Null }
}

function Write-VpsBenchTrxSummary([string]$Trx) {
    if (-not (Test-Path -LiteralPath $Trx)) { Write-Host "  No TRX at $Trx." -ForegroundColor Red; return $null }
    [xml]$xml = Get-Content -LiteralPath $Trx -Raw
    $counters = $xml.TestRun.ResultSummary.Counters
    $results = @($xml.TestRun.Results.UnitTestResult)
    $failed = @($results | Where-Object outcome -eq "Failed")
    Write-Host ("  Summary: total {0}, executed {1}, passed {2}, failed {3}." -f $counters.total, $counters.executed, $counters.passed, $counters.failed) -ForegroundColor Cyan
    foreach ($test in $failed) {
        $message = [string]$test.Output.ErrorInfo.Message
        if ($message.Length -gt 300) { $message = $message.Substring(0, 300) + "..." }
        Write-Host "    FAILED $($test.testName): $message" -ForegroundColor Red
    }
    return @{ Total = [int]$counters.total; Failed = [int]$counters.failed }
}

function Invoke-VpsBenchIntegration([hashtable]$Limits, [string]$Filter, [switch]$KeepCache) {
    $runId = (Get-Date -Format "MMddHHmmss")
    $image = Get-VpsBenchSdkImage
    if (-not $image) { throw "The SDK image was not found in deploy\docker\Dockerfile.back (the build-source stage)." }
    $container = "$benchPrefix-tests-$runId"
    $outDir = Join-Path $benchResultsRoot "level1-$runId"
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $runnerMb = $Limits.MemoryGb * 1024 - $Limits.DatabaseMb
    $startedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    Write-Host "  Level 1: integration suite in $container ($($Limits.Cpus) CPUs on cpuset $($Limits.CpuSet), $runnerMb MB), PostgreSQL limited to $($Limits.DatabaseMb) MB." -ForegroundColor Cyan
    $createArgs = @("create", "--name", $container,
        "--cpus", "$($Limits.Cpus)", "--cpuset-cpus", $Limits.CpuSet, "--memory", "${runnerMb}m", "--memory-swap", "${runnerMb}m",
        "--mount", "type=bind,src=$root,dst=/src,readonly",
        "-v", "/var/run/docker.sock:/var/run/docker.sock",
        "-v", "${benchNuGetVolume}:/nuget", "-e", "NUGET_PACKAGES=/nuget",
        "--add-host", "host.docker.internal:host-gateway",
        "-e", "TESTCONTAINERS_RYUK_DISABLED=true",
        "-e", "AETHEUS_TESTCONTAINERS_RUN=$benchPrefix-$runId", "-e", "AETHEUS_TESTCONTAINERS_STARTED_AT=$startedAt",
        "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_CPUSET=$($Limits.CpuSet)", "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_CPUS=$($Limits.Cpus)",
        "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_MEMORY_MB=$($Limits.DatabaseMb)",
        "-e", "BENCH_TEST_ARGS=$Filter", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
        $image, "bash", "/tmp/aetheus-bench-run.sh")
    $exit = 1
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & docker @createArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker create of $container failed (exit $LASTEXITCODE; is $image pullable?)." }
        $scriptFile = Join-Path $env:TEMP "aetheus-bench-run-$(Get-Random).sh"
        [System.IO.File]::WriteAllText($scriptFile, $benchIntegrationScript)
        & docker cp $scriptFile "${container}:/tmp/aetheus-bench-run.sh" | Out-Null
        $copied = $LASTEXITCODE
        Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
        if ($copied -ne 0) { throw "docker cp of the bench script into $container failed (exit $copied)." }
        # Some tests stop and kill their PostgreSQL container with the `docker` CLI, which the SDK image does not carry:
        # the CLI of the official docker:cli image is copied in as a tar stream (it keeps the file mode).
        $cli = "$benchPrefix-cli-$runId"
        & docker create --name $cli docker:cli | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker create of $cli (image docker:cli) failed (exit $LASTEXITCODE)." }
        & cmd /c "docker cp ${cli}:/usr/local/bin/docker - | docker cp - ${container}:/usr/local/bin/"
        if ($LASTEXITCODE -ne 0) { throw "Could not copy the docker CLI into $container (exit $LASTEXITCODE)." }
        & docker rm -f $cli 2>&1 | Out-Null
        # Filtered live: the build tail, the test lines that are failures, the summary; never the full log.
        & docker start -a $container 2>&1 | Where-Object { $_ -match '^(Copying|Building|Build seconds|Test seconds|Test run summary|\s+(total|failed|succeeded|skipped|duration):|\s*failed |.*error )' } |
            ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        & docker cp "${container}:${benchResultsInContainer}/." $outDir 2>&1 | Out-Null
    } finally {
        Remove-VpsBenchContainers $runId
        if (-not $KeepCache) { & docker volume rm $benchNuGetVolume 2>&1 | Out-Null }
    }
    $summary = Write-VpsBenchTrxSummary (Join-Path $outDir "integration.trx")
    $rawExitFile = Join-Path $outDir "raw-exit-code"
    if (Test-Path -LiteralPath $rawExitFile) { $exit = [int](Get-Content -LiteralPath $rawExitFile -Raw).Trim() }
    Write-Host ("  Wall clock {0:n1} min (build + tests, copy included). Evidence: {1}" -f $watch.Elapsed.TotalMinutes, $outDir) -ForegroundColor Cyan
    if ($null -eq $summary) { return 1 }
    return $exit
}

# Level 2: the stack of the QA stage (deploy/compose/remote.compose.yml: front, back, PostgreSQL), built from this
# checkout as the CI builds it (scripts/publish-application.sh in a Linux container of the pinned SDK, then
# Dockerfile.back and Dockerfile.front), run with the env file QA writes, and the E2E suite in the QA browser image
# against it. The bench adds only an override file: no published port, the cpuset and CPU limit on every service,
# and the database memory limit; the back (2 GB) and front (256 MB) memory limits are QA's own.
$benchPublishScript = @'
set -u
rm -rf /work /tmp/publish /tmp/src.tar
mkdir -p /work /tmp/publish
echo "Copying the checkout into the container..."
tar -C /src -cf /tmp/src.tar --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules \
  --exclude=./TestResults --exclude=./.claude/worktrees --exclude=./.worktrees --exclude=./.pipeline-publish \
  --exclude=./src/Aetheus.Back/wwwroot/downloads . || TAR_EXIT=$?
# GNU tar exits 1 when a file changed while it was read (the checkout's root is written by the launcher meanwhile); 2 is fatal.
[ "${TAR_EXIT:-0}" -le 1 ] || { echo "Could not read the checkout." >&2; exit 1; }
tar -C /work -xf /tmp/src.tar || { echo "Could not copy the checkout." >&2; exit 1; }
rm -f /tmp/src.tar
grep -rIlZ -e "$(printf '\r')" /work --exclude='*.ps1' --exclude='*.cmd' --exclude='*.bat' | xargs -0 -r sed -i 's/\r$//'
cd /work
# The WebAssembly native build (emscripten, the wasm-tools workload) runs a python the SDK image does not carry; the CI host has one.
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends python3 >/dev/null || { echo "Could not install python3 for the WebAssembly build." >&2; exit 1; }
export DOTNET=dotnet APP_VERSION=0.0.0-bench WORKSPACE=/work
sh scripts/publish-application.sh /tmp/publish all || { echo "publish-application.sh failed." >&2; exit 1; }
tar -C /tmp/publish -cf - . | tar -C /out -xf - || { echo "Could not copy the publish out." >&2; exit 1; }
echo "Publish done."
'@ -replace "`r`n", "`n"

function New-VpsBenchSecret([int]$Bytes) { return [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes)).ToLowerInvariant() }

function Invoke-VpsBenchBuildLine([string]$What, [scriptblock]$Command) {
    # A build prints a lot: only its last lines are kept, and only said when it fails.
    $lines = @(& $Command 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $lines | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "$What failed (exit $LASTEXITCODE)."
    }
}

# The application publish, as the CI makes it, in a Linux container of the pinned SDK, then the back and front images
# from the CI's Dockerfiles (their builds are not limited: only the run is) and the QA browser image. Shared by the
# bench level 2 and the pipeline rehearsal (scripts/pipeline-rehearsal.ps1). Returns the commit the images carry.
function Build-VpsBenchStackImages([hashtable]$Limits, [string]$Image, [string]$Publisher, [string]$PublishDir, [string]$OutDir,
    [string]$BackImage, [string]$FrontImage, [string]$Label) {
    Write-Step "${Label}: publishing the application ($Publisher)"
    New-Item -ItemType Directory -Path $PublishDir | Out-Null
    & docker create --name $Publisher --cpus "$($Limits.Cpus)" --cpuset-cpus $Limits.CpuSet `
        --mount "type=bind,src=$root,dst=/src,readonly" --mount "type=bind,src=$PublishDir,dst=/out" `
        -v "${benchNuGetVolume}:/nuget" -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 `
        $Image bash /tmp/aetheus-bench-publish.sh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker create of $Publisher failed (exit $LASTEXITCODE)." }
    $scriptFile = Join-Path $env:TEMP "aetheus-bench-publish-$(Get-Random).sh"
    [System.IO.File]::WriteAllText($scriptFile, $benchPublishScript)
    & docker cp $scriptFile "${Publisher}:/tmp/aetheus-bench-publish.sh" | Out-Null
    Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    $publishLog = Join-Path $OutDir "publish.log"
    & docker start -a $Publisher *> $publishLog
    $publishExit = [int](& docker inspect --format '{{.State.ExitCode}}' $Publisher)
    if ($publishExit -ne 0) {
        Get-Content -LiteralPath $publishLog -Tail 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        & docker rm -f $Publisher 2>&1 | Out-Null
        throw "The application publish failed (exit $publishExit); log: $publishLog"
    }
    & docker rm -f $Publisher 2>&1 | Out-Null

    Write-Step "${Label}: building $BackImage and $FrontImage"
    $commit = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
    Invoke-VpsBenchBuildLine "docker build of the backend image" { & docker build --progress=plain -f (Join-Path $root "deploy\docker\Dockerfile.back") `
        --build-arg APP_VERSION=0.0.0-bench --build-arg "SOURCE_COMMIT=$commit" -t $BackImage $root }
    Invoke-VpsBenchBuildLine "docker build of the frontend image" { & docker build --progress=plain -f (Join-Path $root "deploy\docker\Dockerfile.front") -t $FrontImage $root }
    if (-not (Build-E2eRunnerImage)) { throw "The QA browser image could not be built." }
    return $commit
}

function Invoke-VpsBenchE2e([hashtable]$Limits, [string]$Filter, [switch]$KeepCache, [switch]$KeepStack, [string]$Reuse) {
    $reusing = -not [string]::IsNullOrEmpty($Reuse)
    $runId = if ($reusing) { $Reuse } else { (Get-Date -Format "MMddHHmmss") }
    $proj = "$benchPrefix-$runId"
    $image = Get-VpsBenchSdkImage
    if (-not $image) { throw "The SDK image was not found in deploy\docker\Dockerfile.back (the build-source stage)." }
    $publishDir = Join-Path $root ".pipeline-publish"
    if (-not $reusing -and (Test-Path -LiteralPath $publishDir)) {
        throw "$publishDir already exists (the application publish the images are built from): remove it, the bench writes its own there and removes it at the end."
    }
    $stackDir = Join-Path $benchResultsRoot "level2-$runId"
    $outDir = if ($reusing) { Join-Path $benchResultsRoot ("level2-$runId-rerun-" + (Get-Date -Format "HHmmss")) } else { $stackDir }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $backImage = "$benchPrefix-back:$runId"
    $frontImage = "$benchPrefix-front:$runId"
    $runner = "$benchPrefix-e2e-$runId"
    $publisher = "$benchPrefix-publish-$runId"
    $composeFile = Join-Path $root "deploy\compose\remote.compose.yml"
    $envFile = Join-Path $stackDir "stack.env"
    $overrideFile = Join-Path $stackDir "limits.compose.yml"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = 1
    $limitLines = @("    cpuset: `"$($Limits.CpuSet)`"", "    cpus: $($Limits.Cpus)")
    try {
        if ($reusing) {
            # A stack kept by -BenchKeepStack: only the E2E suite is built again (the tests may have changed), and the
            # CPU limits of the three containers follow this call's.
            if (-not (Test-Path -LiteralPath $envFile)) { throw "No kept stack ${proj}: $envFile is missing." }
            foreach ($service in @("front", "back", "database")) {
                $name = "$proj-$service"
                if (-not (& docker ps --filter "name=^/$name$" --format "{{.Names}}")) { throw "The kept stack container $name is not running." }
                & docker update --cpus "$($Limits.Cpus)" --cpuset-cpus $Limits.CpuSet $name | Out-Null
            }
            $adminPassword = ((Select-String -LiteralPath $envFile -Pattern '^ADMIN_PASSWORD=(.+)$').Matches[0].Groups[1].Value)
            if (-not (Build-E2eRunnerImage)) { throw "The QA browser image could not be built." }
        } else {
        # 1 and 2. The application publish and the images, as the CI makes them.
        $commit = Build-VpsBenchStackImages -Limits $Limits -Image $image -Publisher $publisher -PublishDir $publishDir `
            -OutDir $outDir -BackImage $backImage -FrontImage $frontImage -Label "Bench level 2"

        # 3. The stack, with QA's env file and the bench's limits.
        $adminPassword = New-VpsBenchSecret 16
        $databaseMb = $Limits.DatabaseMb
        @(
            "APPNAME=aetheus", "ENV=bench-$runId", "PORT_FRONT=18080", "PORT_BACK=18081",
            "API_BASE_URL=http://aetheus-bench-$runId-back:8080", "APP_VERSION=0.0.0-bench", "SOURCE_COMMIT=$commit",
            "RATE_LIMITING_DISABLED=true", "AI_MODEL_LIST_REFRESH_ENABLED=false", "ASPNETCORE_ENVIRONMENT=Development",
            "AETHEUS_ENVIRONMENT_TIER=qa", "SEED_DEMO=true", "SEED_DEMO_DATE=2026-07-14", "SERVER_HEARTBEAT_TIMEOUT=08:00:00",
            "DB_NAME=aetheus_e2e", "DB_USER=aetheus", "DB_PASSWORD=$(New-VpsBenchSecret 16)", "JWT_KEY=$(New-VpsBenchSecret 32)",
            "ADMIN_PASSWORD=$adminPassword", "ENCRYPTION_KEY=$(New-VpsBenchSecret 32)", "ENCRYPTION_SALT=$(New-VpsBenchSecret 16)",
            "FRONT_URL=http://aetheus-bench-$runId-front:8080", "FRONT_URL_ACCEPT=http://aetheus-bench-$runId-front:8080",
            "FRONT_URL_PROD=http://aetheus-bench-$runId-front:8080",
            "AETHEUS_BACK_IMAGE=$backImage", "AETHEUS_FRONT_IMAGE=$frontImage"
        ) | Set-Content -LiteralPath $envFile -Encoding ascii
        @(
            "services:",
            "  front:", "    ports: !reset []", $limitLines,
            "  back:", "    ports: !reset []", $limitLines,
            "  database:", $limitLines, "    mem_limit: ${databaseMb}m"
        ) | ForEach-Object { $_ } | Set-Content -LiteralPath $overrideFile -Encoding ascii
        Write-Step "Bench level 2: starting the stack $proj ($($Limits.Cpus) CPUs on cpuset $($Limits.CpuSet), database $databaseMb MB, back 2 GB and front 256 MB as in QA)"
        $up = & docker compose -p $proj --env-file $envFile -f $composeFile -f $overrideFile up -d --wait --wait-timeout 900 2>&1
        if ($LASTEXITCODE -ne 0) {
            $up | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            & docker logs --tail 40 "aetheus-bench-$runId-back" 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            throw "The stack did not come up (docker compose up exit $LASTEXITCODE)."
        }
        }

        # 4. The E2E suite, in the QA browser image, on the stack's network, with the same limits.
        $runnerMb = [int]($Limits.MemoryGb * 1024 / 2)
        Write-Step "Bench level 2: E2E suite ($runner, $($Limits.Cpus) CPUs on cpuset $($Limits.CpuSet), $runnerMb MB), filter '$Filter'"
        $workers = if ($env:AETHEUS_E2E_WORKERS) { $env:AETHEUS_E2E_WORKERS } else { "1" }
        & docker create --name $runner --network "${proj}_default" --cpus "$($Limits.Cpus)" --cpuset-cpus $Limits.CpuSet `
            --memory "${runnerMb}m" --memory-swap "${runnerMb}m" `
            -e "E2E_FRONTEND_URL=http://aetheus-bench-$runId-front:8080" -e "E2E_BACKEND_URL=http://aetheus-bench-$runId-back:8080" `
            -e E2E_ADMIN_USER=admin -e E2E_ADMIN_EMAIL=admin@aetheus.local -e "E2E_ADMIN_PASSWORD=$adminPassword" `
            -e E2E_PRESERVE_DATABASE=true -e E2E_PREVIOUS_PAYLOAD_ONLY=false -e "E2E_WORKERS=$workers" -e "E2E_TEST_FILTER=$Filter" `
            -e E2E_TEST_PROJECT=tests/Aetheus.E2E/Aetheus.E2E.csproj -e "AETHEUS_E2E_RESULTS_DIR=$e2eContainerResults" `
            -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 $e2eImageTag sh /tmp/aetheus-bench-e2e.sh | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker create of $runner failed (exit $LASTEXITCODE)." }
        $scriptFile = Join-Path $env:TEMP "aetheus-bench-e2e-$(Get-Random).sh"
        [System.IO.File]::WriteAllText($scriptFile, $e2eRunnerScript)
        & docker cp $scriptFile "${runner}:/tmp/aetheus-bench-e2e.sh" | Out-Null
        Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
        $suiteWatch = [System.Diagnostics.Stopwatch]::StartNew()
        & docker start -a $runner 2>&1 | Where-Object { $_ -match '^\s*(Failed|Passed!|Failed!|Total tests|Passed:|Failed:|Skipped:|\s*Error Message|Test Run)|Assert|Expected|But was' } |
            ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        Write-Host ("  E2E suite wall clock {0:n1} min." -f $suiteWatch.Elapsed.TotalMinutes) -ForegroundColor Cyan
        & docker cp "${runner}:${e2eContainerResults}/." (Join-Path $outDir "e2e") 2>&1 | Out-Null
    } finally {
        if ($KeepStack) {
            Remove-VpsBenchContainers $runId
            Write-Host "  Stack $proj kept (-BenchKeepStack): run the E2E suite again on it with  .\launch-windows.ps1 -vb -BenchLevel 2 -BenchReuse $runId" -ForegroundColor Yellow
            Write-Host "  Remove it with: docker compose -p $proj --env-file `"$envFile`" -f `"$composeFile`" -f `"$overrideFile`" down -v --remove-orphans ; docker rmi $backImage $frontImage" -ForegroundColor Yellow
        } else {
            Write-Host "  Removing the bench stack, containers, network, volumes and images ($proj)..." -ForegroundColor DarkGray
            & docker compose -p $proj --env-file $envFile -f $composeFile -f $overrideFile down -v --remove-orphans 2>&1 | Out-Null
            Remove-VpsBenchContainers $runId
            & docker rmi $backImage $frontImage 2>&1 | Out-Null
        }
        if (-not $KeepCache -and -not $KeepStack) { & docker volume rm $benchNuGetVolume 2>&1 | Out-Null }
        if (-not $reusing -and (Test-Path -LiteralPath $publishDir) -and (Split-Path -Parent $publishDir) -eq $root.TrimEnd('\', '/')) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }
    }
    $summary = Write-VpsBenchTrxSummary (Join-Path $outDir "e2e\e2e.trx")
    $rawExitFile = Join-Path $outDir "e2e\raw-exit-code"
    if (Test-Path -LiteralPath $rawExitFile) { $exit = [int](Get-Content -LiteralPath $rawExitFile -Raw).Trim() }
    Write-Host ("  Wall clock {0:n1} min (publish, images, stack and tests). Evidence: {1}" -f $watch.Elapsed.TotalMinutes, $outDir) -ForegroundColor Cyan
    if ($null -eq $summary) { return 1 }
    return $exit
}

function Invoke-VpsBench([int]$Level, [int]$Cpus, [int]$MemoryGb, [string]$Filter, [switch]$KeepCache, [switch]$KeepStack, [string]$Reuse) {
    $dockerOs = $null
    try { $dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null | Select-Object -First 1) } catch { $dockerOs = $null }
    if ("$dockerOs".Trim() -ne "linux") {
        Write-Host "  Docker did not report a running Linux engine (got '$dockerOs'): is Docker running in Linux containers mode?" -ForegroundColor Red
        return 1
    }
    $limits = Get-VpsBenchLimits $Cpus $MemoryGb
    Write-Step "VPS bench, level ${Level}: $($limits.Cpus) CPUs (cpuset $($limits.CpuSet)), $($limits.MemoryGb) GB (defaults 4 CPUs / 8 GB are an ESTIMATE of the VPS)"
    if ($Level -eq 1) { return Invoke-VpsBenchIntegration $limits $Filter -KeepCache:$KeepCache }
    return Invoke-VpsBenchE2e $limits $Filter -KeepCache:$KeepCache -KeepStack:$KeepStack -Reuse $Reuse
}
