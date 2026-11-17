# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
#
# Linux suite (ylaunch -tl): runs the full test set (back, front, analyzers, agent core, observability packages,
# integration) inside ONE Linux container of the SDK image the release builds pin (deploy/docker/Dockerfile.back,
# the build-source stage), built in Release with the analyzers like the CI, so that a test that is green on the Windows
# workstation and red on the Linux agent (candidate 347: a stand-in docker written without its execute bit; candidate
# 2961: the agent suite) fails here first. It is run by the agent before a candidate, never by the pre-push hook, and
# `-ta` is unchanged.
#
# Only the container is limited, never the host: half the CPUs of the Docker VM (`--cpus` and a cpuset, shared with the
# integration suite's PostgreSQL container) and 8 GB (`--memory`; Roslyn ran out of memory at 6 GB). There is no host
# fallback: without a Linux Docker engine the suite fails.
#
# The integration suite starts its PostgreSQL through Testcontainers on the host's Docker engine: the engine socket is
# mounted into the container, Ryuk is disabled (it needs a privileged socket-mounting reaper) and the run's containers
# are removed by label at the end, on every path; the docker CLI some of its tests call is copied in from docker:cli.
# The E2E suite is not part of it (it needs a whole running stack): `-vb -BenchLevel 2` is its limited-container run.
#
# Dot-sourced by launch-core.ps1 once its helpers (Write-Step) exist. Everything it creates is named with the
# `aetheus-linux-suite-` prefix; the cache volumes `aetheus-linux-suite-nuget` and `aetheus-linux-suite-node` are kept
# between runs.

$linuxSuitePrefix = "aetheus-linux-suite"
$linuxSuiteNuGetVolume = "$linuxSuitePrefix-nuget"
$linuxSuiteNodeVolume = "$linuxSuitePrefix-node"
$linuxSuiteResultsRoot = Join-Path $root "TestResults\LinuxSuite"
$linuxSuiteResultsInContainer = "/tmp/aetheus-linux-results"

# Name -> project, in run order (cheap suites first, the integration suite last).
$linuxSuiteProjects = [ordered]@{
    Analyzers    = "tests/Aetheus.Analyzers.Tests"
    Agent        = "tests/Aetheus.Agent.Core.Tests"
    Telemetry    = "tests/Aetheus.Telemetry.Tests"
    WebAnalytics = "tests/Aetheus.WebAnalytics.Tests"
    Front        = "tests/Aetheus.Front.Tests"
    Back         = "tests/Aetheus.Back.Tests"
    Integration  = "tests/Aetheus.Back.IntegrationTests"
}

$linuxSuiteRunnerScript = @'
set -u
RESULTS=/tmp/aetheus-linux-results
rm -rf "$RESULTS" /work /tmp/src.tar
mkdir -p "$RESULTS" /work
{ uname -sr; dotnet --version; echo "cpus=$(nproc)"; } | tr '\n' ' ' > "$RESULTS/platform.txt"
echo "Copying the checkout into the container..."
tar -C /src -cf /tmp/src.tar --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules \
  --exclude=./TestResults --exclude=./.claude/worktrees --exclude=./.worktrees --exclude=./.pipeline-publish \
  --exclude=./src/Aetheus.Back/wwwroot/downloads \
  --exclude=./src/Aetheus.Back/Data/git-repos . || TAR_EXIT=$?
# git-repos is the dev back end's runtime store (a junction to the main checkout in a worktree: a dangling link here).
# GNU tar exits 1 when a file changed while it was read; 2 is fatal.
[ "${TAR_EXIT:-0}" -le 1 ] || { echo "Could not read the checkout." >&2; exit 1; }
tar -C /work -xf /tmp/src.tar || { echo "Could not copy the checkout." >&2; exit 1; }
rm -f /tmp/src.tar
# A Linux checkout holds LF where this Windows checkout holds CRLF (.gitattributes: text=auto); the files
# .gitattributes forces to CRLF keep them. Without it the C# raw-string scripts of the tests carry a CR.
grep -rIlZ -e "$(printf '\r')" /work --exclude='*.ps1' --exclude='*.cmd' --exclude='*.bat' | xargs -0 -r sed -i 's/\r$//'
cd /work
# The SDK image marks itself as a container for ASP.NET (DOTNET_RUNNING_IN_CONTAINER), which makes Data Protection log a
# "keys may not be persisted outside of the container" warning that a test asserts is absent; the CI agent is a plain
# host without the marker, so the suite runs without it.
unset DOTNET_RUNNING_IN_CONTAINER
# Some tests read the checkout through `git ls-files` (the source-publication and gitignore guards); the copy has no
# .git, so a fresh repository (nothing committed: untracked files minus .gitignore) gives them the same listing.
git init -q . || { echo "git init failed in the copy." >&2; exit 1; }
# The delivery-pipeline tests need the pinned Node.js on the PATH, as the CI agent has (PinnedNodeRuntime): the official
# archive of that exact version, verified against the pinned SHA-256, cached in a volume.
NODE_ARCHIVE="/opt/node-cache/node-v$NODE_VERSION-linux-x64.tar.gz"
if [ ! -f "$NODE_ARCHIVE" ]; then
  curl -fsSL --retry 3 -o "$NODE_ARCHIVE.part" "https://nodejs.org/dist/v$NODE_VERSION/node-v$NODE_VERSION-linux-x64.tar.gz" \
    && mv "$NODE_ARCHIVE.part" "$NODE_ARCHIVE" || { echo "Could not download Node.js $NODE_VERSION." >&2; exit 1; }
fi
echo "$NODE_SHA256  $NODE_ARCHIVE" | sha256sum -c - >/dev/null || { rm -f "$NODE_ARCHIVE"; echo "Node.js archive does not match its pinned SHA-256." >&2; exit 1; }
mkdir -p /opt/node && tar -xzf "$NODE_ARCHIVE" -C /opt/node --strip-components=1 || { echo "Could not unpack Node.js." >&2; exit 1; }
export PATH="/opt/node/bin:$PATH"
echo "Node.js: $(node --version)"
# The docker CLI (copied in for the integration suite) is on the PATH of that suite only: the other suites run
# as on the agent, whose tests write their own stand-in docker and must never meet a real one.
if [ -x /usr/local/bin/docker ]; then mkdir -p /opt/docker-cli && mv /usr/local/bin/docker /opt/docker-cli/docker; fi
# The first IPv4 address of the host: a name makes Npgsql try the IPv6 one first, unreachable from the Docker network.
TESTCONTAINERS_HOST_OVERRIDE="$(getent ahostsv4 host.docker.internal | awk 'NR==1{print $1}')"
export TESTCONTAINERS_HOST_OVERRIDE
echo "Testcontainers host: $TESTCONTAINERS_HOST_OVERRIDE"
set -f
OVERALL=0
IGNORE=""
# A filter selects no test in most suites: exit code 8 (zero tests ran) is then expected, not a failure.
[ -z "$LINUX_TEST_ARGS" ] || IGNORE="--ignore-exit-code 8"
for SUITE in $LINUX_SUITES; do
  case "$SUITE" in
    Analyzers) PROJECT=tests/Aetheus.Analyzers.Tests ;;
    Agent) PROJECT=tests/Aetheus.Agent.Core.Tests ;;
    Telemetry) PROJECT=tests/Aetheus.Telemetry.Tests ;;
    WebAnalytics) PROJECT=tests/Aetheus.WebAnalytics.Tests ;;
    Front) PROJECT=tests/Aetheus.Front.Tests ;;
    Back) PROJECT=tests/Aetheus.Back.Tests ;;
    Integration) PROJECT=tests/Aetheus.Back.IntegrationTests ;;
    *) echo "Unknown suite $SUITE" >&2; exit 2 ;;
  esac
  RUN_PATH="$PATH"
  [ "$SUITE" != Integration ] || RUN_PATH="/opt/docker-cli:$PATH"
  echo "=== $SUITE: build (Release, analyzers on) and run ==="
  START=$(date +%s)
  RC=0
  PATH="$RUN_PATH" dotnet test --project "$PROJECT" --configuration Release --no-progress \
    --report-trx --report-trx-filename "$SUITE.trx" --results-directory "$RESULTS" $IGNORE $LINUX_TEST_ARGS || RC=$?
  echo "=== $SUITE: exit $RC after $(( $(date +%s) - START )) s ==="
  printf "%s\n" "$RC" > "$RESULTS/exit-$SUITE"
  [ "$RC" = 0 ] || OVERALL=1
done
exit "$OVERALL"
'@ -replace "`r`n", "`n"

# The Node.js version and SHA-256 the CI agent installs, read from the code that installs it so the suite cannot drift from it.
function Get-LinuxSuiteNodePin {
    $source = Join-Path $root "src\Aetheus.Agent.Core\Functions\PinnedNodeRuntime.cs"
    $text = Get-Content -LiteralPath $source -Raw
    $version = [regex]::Match($text, 'const string Version = "([0-9.]+)"')
    $sha = [regex]::Match($text, 'const string LinuxX64Sha256 = "([0-9a-f]{64})"')
    if (-not $version.Success -or -not $sha.Success) { return $null }
    return @{ Version = $version.Groups[1].Value; Sha256 = $sha.Groups[1].Value }
}

function Get-LinuxSuiteSdkImage {
    $dockerfile = Join-Path $root "deploy\docker\Dockerfile.back"
    $match = Select-String -LiteralPath $dockerfile -Pattern '^FROM\s+(mcr\.microsoft\.com/dotnet/sdk:\S+)\s+AS\s+build-source\s*$' | Select-Object -First 1
    if (-not $match) { return $null }
    return $match.Matches[0].Groups[1].Value
}

# One line per suite from its TRX (a suite with no TRX did not get to run its tests: failed), and the overall verdict.
function Write-LinuxSuiteSummary([string]$OutDir, [string[]]$Suites, [bool]$Filtered) {
    $failedSuites = 0
    foreach ($suite in $Suites) {
        $trx = Join-Path $OutDir "$suite.trx"
        $exitFile = Join-Path $OutDir "exit-$suite"
        $rc = if (Test-Path -LiteralPath $exitFile) { (Get-Content -LiteralPath $exitFile -Raw).Trim() } else { "not run" }
        if (-not (Test-Path -LiteralPath $trx)) {
            if ($Filtered -and $rc -eq "0") {
                Write-Host ("  {0,-13} no test matched the filter (exit {1})." -f $suite, $rc) -ForegroundColor DarkGray
                continue
            }
            Write-Host ("  {0,-13} NO TRX (exit {1}): the suite did not run its tests." -f $suite, $rc) -ForegroundColor Red
            $failedSuites++
            continue
        }
        [xml]$xml = Get-Content -LiteralPath $trx -Raw
        $results = @($xml.TestRun.Results.UnitTestResult)
        $failed = @($results | Where-Object outcome -eq "Failed")
        $skipped = @($results | Where-Object outcome -eq "NotExecuted").Count
        $passed = @($results | Where-Object outcome -eq "Passed").Count
        $ok = ($rc -eq "0") -and ($failed.Count -eq 0)
        if (-not $ok) { $failedSuites++ }
        Write-Host ("  {0,-13} total {1}, passed {2}, failed {3}, skipped {4} (exit {5})" -f $suite, $results.Count, $passed, $failed.Count, $skipped, $rc) `
            -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
        foreach ($test in ($failed | Select-Object -First 40)) {
            $message = [string]$test.Output.ErrorInfo.Message
            if ($message.Length -gt 300) { $message = $message.Substring(0, 300) + "..." }
            Write-Host "    FAILED $($test.testName): $message" -ForegroundColor Red
        }
    }
    return $failedSuites
}

function Remove-LinuxSuiteContainers([string]$RunId) {
    # By label (Testcontainers' own containers) and by name: only this run's, never a pattern of other projects.
    $labelled = @(& docker ps -a -q --filter "label=aetheus.testcontainers.run=$linuxSuitePrefix-$RunId" 2>$null)
    if ($labelled.Count -gt 0) { & docker rm -f @labelled 2>&1 | Out-Null }
    foreach ($name in @("$linuxSuitePrefix-$RunId", "$linuxSuitePrefix-cli-$RunId")) {
        if (@(& docker ps -a -q --filter "name=^/$name$" 2>$null).Count -gt 0) { & docker rm -f $name 2>&1 | Out-Null }
    }
}

# Returns the process exit code: 0 only when every selected suite ran its tests and none failed.
function Invoke-LinuxSuite([string[]]$Suites, [string]$TestArgs) {
    $dockerOs = $null
    try { $dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null | Select-Object -First 1) } catch { $dockerOs = $null }
    if ("$dockerOs".Trim() -ne "linux") {
        Write-Host "  Docker did not report a running Linux engine (got '$dockerOs'): is Docker running in Linux containers mode?" -ForegroundColor Red
        Write-Host "  The Linux suite runs only in a Linux container: it is NOT run on the Windows host." -ForegroundColor Red
        return 1
    }
    $image = Get-LinuxSuiteSdkImage
    if (-not $image) {
        Write-Host "  The SDK image was not found in deploy\docker\Dockerfile.back (the build-source stage)." -ForegroundColor Red
        return 1
    }
    $nodePin = Get-LinuxSuiteNodePin
    if (-not $nodePin) {
        Write-Host "  The pinned Node.js version and digest were not found in src\Aetheus.Agent.Core\Functions\PinnedNodeRuntime.cs." -ForegroundColor Red
        return 1
    }
    $selected = @($Suites | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($selected.Count -eq 0) { $selected = @($linuxSuiteProjects.Keys) }
    $unknown = @($selected | Where-Object { -not $linuxSuiteProjects.Contains($_) })
    if ($unknown.Count -gt 0) {
        Write-Host "  Unknown suite(s): $($unknown -join ', '). Known: $(@($linuxSuiteProjects.Keys) -join ', ')." -ForegroundColor Red
        return 1
    }
    # Run order is the table's, whatever the order asked.
    $selected = @($linuxSuiteProjects.Keys | Where-Object { $selected -contains $_ })

    $vmCpus = [int](& docker info --format '{{.NCPU}}' 2>$null | Select-Object -First 1)
    $vmMemoryGb = [math]::Floor([double](& docker info --format '{{.MemTotal}}' 2>$null | Select-Object -First 1) / 1GB)
    $cpus = [math]::Max(1, [int][math]::Floor($vmCpus / 2))
    $cpuSet = if ($cpus -eq 1) { "0" } else { "0-$($cpus - 1)" }
    $memoryGb = 8
    $databaseMb = 2048
    if ($vmMemoryGb -lt $memoryGb + 2) {
        Write-Host "  The Docker VM has $vmMemoryGb GB: the Linux suite needs $memoryGb GB for the runner plus 2 GB for PostgreSQL. Raise the Docker memory limit." -ForegroundColor Red
        return 1
    }

    $runId = (Get-Date -Format "MMddHHmmss")
    $container = "$linuxSuitePrefix-$runId"
    $outDir = Join-Path $linuxSuiteResultsRoot $runId
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $startedAt = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    Write-Step "Linux suite: $($selected -join ', ') in $container ($cpus CPUs on cpuset $cpuSet, $memoryGb GB; PostgreSQL limited to $databaseMb MB)"
    $needsDockerCli = $selected -contains "Integration"
    $createArgs = @("create", "--name", $container,
        "--cpus", "$cpus", "--cpuset-cpus", $cpuSet, "--memory", "${memoryGb}g", "--memory-swap", "${memoryGb}g",
        "--mount", "type=bind,src=$root,dst=/src,readonly",
        "-v", "/var/run/docker.sock:/var/run/docker.sock",
        "-v", "${linuxSuiteNuGetVolume}:/nuget", "-e", "NUGET_PACKAGES=/nuget",
        "-v", "${linuxSuiteNodeVolume}:/opt/node-cache",
        "-e", "NODE_VERSION=$($nodePin.Version)", "-e", "NODE_SHA256=$($nodePin.Sha256)",
        "--add-host", "host.docker.internal:host-gateway",
        "-e", "TESTCONTAINERS_RYUK_DISABLED=true",
        "-e", "AETHEUS_TESTCONTAINERS_RUN=$linuxSuitePrefix-$runId", "-e", "AETHEUS_TESTCONTAINERS_STARTED_AT=$startedAt",
        "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_CPUSET=$cpuSet", "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_CPUS=$cpus",
        "-e", "AETHEUS_TESTCONTAINERS_POSTGRES_MEMORY_MB=$databaseMb",
        "-e", "LINUX_SUITES=$($selected -join ' ')", "-e", "LINUX_TEST_ARGS=$TestArgs",
        "-e", "HUSKY=0", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
        $image, "bash", "/tmp/aetheus-linux-run.sh")
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & docker @createArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker create of $container failed (exit $LASTEXITCODE; is $image pullable?)." }
        $scriptFile = Join-Path $env:TEMP "aetheus-linux-run-$(Get-Random).sh"
        [System.IO.File]::WriteAllText($scriptFile, $linuxSuiteRunnerScript)
        & docker cp $scriptFile "${container}:/tmp/aetheus-linux-run.sh" | Out-Null
        $copied = $LASTEXITCODE
        Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
        if ($copied -ne 0) { throw "docker cp of the runner script into $container failed (exit $copied)." }
        if ($needsDockerCli) {
            # Some integration tests stop and kill their PostgreSQL container with the `docker` CLI, which the SDK image
            # does not carry: the CLI of the official docker:cli image is copied in as a tar stream (it keeps the file mode).
            $cli = "$linuxSuitePrefix-cli-$runId"
            & docker create --name $cli docker:cli | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "docker create of $cli (image docker:cli) failed (exit $LASTEXITCODE)." }
            & cmd /c "docker cp ${cli}:/usr/local/bin/docker - | docker cp - ${container}:/usr/local/bin/"
            if ($LASTEXITCODE -ne 0) { throw "Could not copy the docker CLI into $container (exit $LASTEXITCODE)." }
            & docker rm -f $cli 2>&1 | Out-Null
        }
        # Filtered live: the suite markers, the build tail, the failures, the summaries; never the full log.
        & docker start -a $container 2>&1 | Where-Object { $_ -match '^(Copying|Testcontainers host|===|\s+(total|failed|succeeded|skipped|duration):|Test run summary|\s*failed |.*error )' } |
            ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        & docker cp "${container}:${linuxSuiteResultsInContainer}/." $outDir 2>&1 | Out-Null
    } catch {
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        return 1
    } finally {
        Remove-LinuxSuiteContainers $runId
    }
    $platformFile = Join-Path $outDir "platform.txt"
    if (Test-Path -LiteralPath $platformFile) { Write-Host "  Ran in: $((Get-Content -LiteralPath $platformFile -Raw).Trim())" -ForegroundColor Cyan }
    $failedSuites = Write-LinuxSuiteSummary -OutDir $outDir -Suites $selected -Filtered (-not [string]::IsNullOrEmpty($TestArgs))
    Write-Host ("  Wall clock {0:n1} min. Evidence: {1}" -f $watch.Elapsed.TotalMinutes, $outDir) -ForegroundColor Cyan
    if ($failedSuites -gt 0) { Write-Host "  LINUX SUITE FAILED ($failedSuites suite(s))." -ForegroundColor Red; return 1 }
    Write-Host "  LINUX SUITE PASSED." -ForegroundColor Green
    return 0
}
