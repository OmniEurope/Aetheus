# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
#
# Pipeline rehearsal (ylaunch -pr), recette R-29: the pipelines the setup wizard generates are run for real, on a
# simulated VPS, before anything about them is announced fixed. One command, no manual step:
#   1. an isolated Aetheus stack (database, back, front, built from this checkout as the CI builds them) and a simulated
#      VPS (Dockerfile.vpssim: systemd, Docker, Apache, certbot) on the stack's network, every container limited
#      (--cpus, --cpuset-cpus, memory); the host is never loaded;
#   2. the real agent installed on the simulated VPS with the real installer and a registration token;
#   3. a project holding the small Blazor application of tests/fixtures/rehearsal-blazor, pushed to its internal Git
#      repository, the simulated VPS linked to it and to its production environment;
#   4. the pipelines created through the real setup wizard, in a browser (PipelineRehearsalWizardTests);
#   5. candidate (and the deployment it starts), nightly, a commit that moves a source file, candidate and deployment
#      again, then the fast release, each run approved where it asks and checked: every run Success, the deployed
#      application answering https://<its host>/health through Apache on the simulated VPS with the expected version.
# What is stubbed, and only that: the certificate authority. No ACME server can reach a local box, so a self-signed
# certificate for the application's host is placed in /etc/letsencrypt/live before the first deployment (the TLS step
# then finds a valid certificate and issues nothing) and the host name resolves to the box through its /etc/hosts.
#
# Dot-sourced by launch-core.ps1 after scripts/vps-bench.ps1, whose helpers it reuses (limits, publish, images).
# Everything it creates is named aetheus-rehearsal-<id> and removed at the end unless -RehearsalKeep.

$rehearsalPrefix = "aetheus-rehearsal"
$rehearsalResultsRoot = Join-Path $root "TestResults\PipelineRehearsal"
$rehearsalVpsImage = "${rehearsalPrefix}-vpssim:local"
$rehearsalAdminEmail = "admin@aetheus.local"
$rehearsalBranch = "develop"

# The wizard options the rehearsal selects: CI, quality, security and QA (the QA runs the fixture's browser tests in the pinned
# browser image, the security scans run on the source), the candidate, the nightly, the production deployment with its fast
# release and rollback. Deliberately NOT selected, and said so in the summary: the extended QA, the security history, and
# telemetry, audience and dependency updates (declined with their skip options). Every step of a selected option must really run.
$rehearsalWizardOptions = "ci,quality,security,qa,candidate,nightly,production,fast-deploy,rollback,lang-dotnet,skip-telemetry,skip-audience,skip-dependency-updates"
$rehearsalNotSelected = @("qa-extended", "security-history", "telemetry", "audience", "dependency-updates")
# The stages an option not selected owns (the nightly names them with a Nightly prefix): expected not to run.
$rehearsalOptionStages = @{ "qa-extended" = "QAExtended"; "security-history" = "SecurityHistory" }
# What the pipelines a candidate or the nightly triggers must really have run (stage names of the templates).
$rehearsalRequiredStages = @{
    "Quality"  = @("Quality", "Install duplication detector", "Check duplication", "Grade quality")
    "Security" = @("Scan source", "Grade security")
    "QA"       = @("V integration suite", "Deploy V", "V deployed probes", "V E2E suite", "Roll back to V-1", "Tear down QA stack")
}

function Write-RehearsalLine([string]$Text, [string]$Color = "Gray") { Write-Host "  $Text" -ForegroundColor $Color }

function Get-RehearsalFreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

# The back's HTTPS listener: the agent refuses plain HTTP to a non-loopback backend, as on a real VPS.
function New-RehearsalBackCertificate([string]$HostName, [string]$Directory, [string]$Password) {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$HostName", $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName($HostName)
    $request.CertificateExtensions.Add($san.Build())
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
    [System.IO.File]::WriteAllBytes((Join-Path $Directory "back.pfx"),
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
    [System.IO.File]::WriteAllText((Join-Path $Directory "back.crt"), $certificate.ExportCertificatePem() + "`n")
}

# ---------------------------------------------------------------- API ----------------------------------------------------------------

function Invoke-RehearsalApi([hashtable]$State, [string]$Method, [string]$Path, $Body = $null) {
    $headers = @{ Authorization = "Bearer $($State.Token)" }
    $uri = "$($State.ApiUrl)$Path"
    try {
        if ($null -eq $Body) { return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 120 }
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType "application/json" `
            -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 120
    } catch {
        # The API's refusal says why (its message); the bare status code does not.
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        throw "$Method $Path refused: $detail"
    }
}

function Connect-RehearsalApi([hashtable]$State) {
    $login = Invoke-RestMethod -Method Post -Uri "$($State.ApiUrl)/api/auth/login" -ContentType "application/json" `
        -Body (@{ email = $rehearsalAdminEmail; password = $State.AdminPassword } | ConvertTo-Json) -TimeoutSec 60
    if (-not $login.token) { throw "The admin login of the rehearsal stack returned no token." }
    $State.Token = $login.token
}

function Wait-RehearsalCondition([string]$What, [int]$TimeoutSeconds, [scriptblock]$Condition, [int]$IntervalSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = $null
        try { $value = & $Condition } catch { $value = $null }
        if ($value) { return $value }
        Start-Sleep -Seconds $IntervalSeconds
    }
    throw "Timed out after $TimeoutSeconds s waiting for: $What"
}

# ------------------------------------------------------------ the stack -------------------------------------------------------------

function Start-RehearsalStack([hashtable]$State, [hashtable]$Limits) {
    $runId = $State.RunId
    $proj = $State.Project
    $image = Get-VpsBenchSdkImage
    if (-not $image) { throw "The SDK image was not found in deploy\docker\Dockerfile.back (the build-source stage)." }
    $publishDir = Join-Path $root ".pipeline-publish"
    if (Test-Path -LiteralPath $publishDir) {
        throw "$publishDir already exists (the application publish the images are built from): remove it, the rehearsal writes its own there."
    }
    try {
        $State.Commit = Build-VpsBenchStackImages -Limits $Limits -Image $image -Publisher "$proj-publish" -PublishDir $publishDir `
            -OutDir $State.OutDir -BackImage $State.BackImage -FrontImage $State.FrontImage -Label "Rehearsal"
    } finally {
        if ((Test-Path -LiteralPath $publishDir) -and (Split-Path -Parent $publishDir) -eq $root.TrimEnd('\', '/')) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }
    }

    $certDir = Join-Path $State.OutDir "certs"
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
    New-RehearsalBackCertificate $State.BackHost $certDir $State.PfxPassword

    @(
        "APPNAME=aetheus", "ENV=rehearsal-$runId", "PORT_FRONT=18080", "PORT_BACK=18081",
        "API_BASE_URL=http://$($State.BackHost):8080", "APP_VERSION=0.0.0-rehearsal", "SOURCE_COMMIT=$($State.Commit)",
        "RATE_LIMITING_DISABLED=true", "AI_MODEL_LIST_REFRESH_ENABLED=false", "ASPNETCORE_ENVIRONMENT=Development",
        "AETHEUS_ENVIRONMENT_TIER=qa", "SEED_DEMO=false", "SERVER_HEARTBEAT_TIMEOUT=00:02:00",
        "DB_NAME=aetheus_rehearsal", "DB_USER=aetheus", "DB_PASSWORD=$(New-VpsBenchSecret 16)", "JWT_KEY=$(New-VpsBenchSecret 32)",
        "ADMIN_EMAIL=$rehearsalAdminEmail", "ADMIN_PASSWORD=$($State.AdminPassword)",
        "ENCRYPTION_KEY=$(New-VpsBenchSecret 32)", "ENCRYPTION_SALT=$(New-VpsBenchSecret 16)",
        "FRONT_URL=http://$($State.FrontHost):8080", "FRONT_URL_ACCEPT=http://$($State.FrontHost):8080",
        "FRONT_URL_PROD=http://$($State.FrontHost):8080",
        "AETHEUS_BACK_IMAGE=$($State.BackImage)", "AETHEUS_FRONT_IMAGE=$($State.FrontImage)"
    ) | Set-Content -LiteralPath $State.EnvFile -Encoding ascii

    $limitLines = @("    cpuset: `"$($Limits.CpuSet)`"", "    cpus: $($Limits.Cpus)")
    $certMount = ($certDir -replace '\\', '/')
    @(
        "services:",
        "  front:", "    ports: !reset []", $limitLines,
        "  back:", "    ports: !override", "      - `"127.0.0.1:$($State.BackPort):8080`"", $limitLines,
        "    environment:",
        "      - ASPNETCORE_URLS=http://+:8080;https://+:8443",
        "      - ASPNETCORE_Kestrel__Certificates__Default__Path=/rehearsal-certs/back.pfx",
        "      - ASPNETCORE_Kestrel__Certificates__Default__Password=$($State.PfxPassword)",
        "    volumes:", "      - `"${certMount}:/rehearsal-certs:ro`"",
        "  database:", $limitLines, "    mem_limit: $($State.DatabaseMb)m",
        "  vpssim:",
        "    image: $rehearsalVpsImage",
        "    container_name: $($State.VpsHost)",
        "    hostname: $($State.VpsHost)",
        "    ports: !reset []", $limitLines, "    mem_limit: $($State.VpsMb)m", "    memswap_limit: $($State.VpsMb)m"
    ) | ForEach-Object { $_ } | Set-Content -LiteralPath $State.OverrideFile -Encoding ascii

    Write-Step "Rehearsal: building the simulated VPS image $rehearsalVpsImage (Dockerfile.vpssim, cached)"
    Invoke-VpsBenchBuildLine "docker compose build of the simulated VPS" {
        & docker compose -p $proj --env-file $State.EnvFile @($State.ComposeArgs) build vpssim }

    Write-Step "Rehearsal: starting $proj (database $($State.DatabaseMb) MB, back 2 GB, front 256 MB, VPS $($State.VpsMb) MB, all on cpuset $($Limits.CpuSet), $($Limits.Cpus) CPUs)"
    $up = & docker compose -p $proj --env-file $State.EnvFile @($State.ComposeArgs) up -d --wait --wait-timeout 900 2>&1
    if ($LASTEXITCODE -ne 0) {
        $up | Select-Object -Last 30 | ForEach-Object { Write-RehearsalLine $_ Red }
        & docker logs --tail 40 $State.BackHost 2>&1 | ForEach-Object { Write-RehearsalLine $_ Red }
        throw "The rehearsal stack did not come up (docker compose up exit $LASTEXITCODE)."
    }
    Wait-RehearsalCondition "the back API on $($State.ApiUrl)" 300 {
        (Invoke-WebRequest -Uri "$($State.ApiUrl)/health/live" -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode -eq 200 } | Out-Null
}

function Invoke-RehearsalVps([hashtable]$State, [string]$Script, [hashtable]$Environment = @{}) {
    $envArgs = @()
    foreach ($key in $Environment.Keys) { $envArgs += @("-e", "$key=$($Environment[$key])") }
    $output = & docker exec @envArgs $State.VpsHost bash -c ($Script -replace "`r`n", "`n") 2>&1
    $State.LastVpsExit = $LASTEXITCODE
    return $output
}

function Install-RehearsalAgent([hashtable]$State) {
    Write-Step "Rehearsal: installing the agent on $($State.VpsHost) with the real installer"
    Wait-RehearsalCondition "systemd and dockerd in $($State.VpsHost)" 300 {
        Invoke-RehearsalVps $State "systemctl is-system-running --wait >/dev/null 2>&1; docker info >/dev/null 2>&1 && echo ready" | Out-Null
        $State.LastVpsExit -eq 0 } | Out-Null
    & docker cp (Join-Path $State.OutDir "certs\back.crt") "$($State.VpsHost):/usr/local/share/ca-certificates/aetheus-rehearsal-back.crt" | Out-Null
    Invoke-RehearsalVps $State "update-ca-certificates >/dev/null" | Out-Null

    $token = (Invoke-RehearsalApi $State Post "/api/auth/registration-tokens" @{ expirationHours = 24 }).token
    if (-not $token) { throw "No registration token was minted." }
    $log = Join-Path $State.OutDir "agent-install.log"
    Invoke-RehearsalVps $State @'
set -eu
cd /opt/agent-pkg
chmod +x install-agent-linux.sh Aetheus.Agent.Linux 2>/dev/null || true
./install-agent-linux.sh --server-url "$REHEARSAL_SERVER_URL" --token "$REHEARSAL_TOKEN" --name "$REHEARSAL_AGENT_NAME" \
  --module pipeline-runner --module server-management --module deployment --enable-docker --enable-certbot-manage --yes
'@ @{ REHEARSAL_SERVER_URL = "https://$($State.BackHost):8443"; REHEARSAL_TOKEN = $token; REHEARSAL_AGENT_NAME = $State.AgentName } |
        Set-Content -LiteralPath $log -Encoding utf8
    if ($State.LastVpsExit -ne 0) {
        Get-Content -LiteralPath $log -Tail 30 | ForEach-Object { Write-RehearsalLine $_ Red }
        throw "install-agent-linux.sh failed on the simulated VPS (exit $($State.LastVpsExit)); log: $log"
    }
    $server = Wait-RehearsalCondition "the agent $($State.AgentName) online and able to deploy" 600 {
        $page = Invoke-RehearsalApi $State Get "/api/servers?page=1&pageSize=50"
        @($page.items) | Where-Object { $_.name -eq $State.AgentName -and $_.status -eq "Online" -and $_.pipelineRunnerEnabled -and $_.deploymentTargetAvailable } | Select-Object -First 1
    } 10
    $State.ServerId = [int]$server.id
    $State.ServerHostname = [string]$server.hostname
    Write-RehearsalLine "Agent $($State.AgentName) online (server $($State.ServerId)), pipeline runner and deployment target." Green
}

# ----------------------------------------------------------- the project ------------------------------------------------------------

function Invoke-RehearsalGit([hashtable]$State, [string[]]$Arguments) {
    $basic = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("${rehearsalAdminEmail}:$($State.AdminPassword)"))
    $output = & git -C $State.RepoDir -c core.autocrlf=false -c user.name=rehearsal -c user.email=rehearsal@aetheus.local `
        -c "http.extraHeader=Authorization: Basic $basic" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { $output | ForEach-Object { Write-RehearsalLine $_ Red }; throw "git $($Arguments[0]) failed (exit $LASTEXITCODE)." }
}

function New-RehearsalProject([hashtable]$State) {
    Write-Step "Rehearsal: project $($State.ProjectName), its internal repository, the simulated VPS and the prod environment"
    $project = Invoke-RehearsalApi $State Post "/api/projects" @{ name = $State.ProjectName; description = "Pipeline rehearsal (R-29)"; defaultBranch = "main"; tags = @() }
    $State.ProjectId = [int]$project.id
    $repo = Invoke-RehearsalApi $State Post "/api/git/repos" @{ projectId = $State.ProjectId; name = $State.ProjectName; description = $null; defaultBranch = "main" }
    $State.RepoSlug = [string]$repo.slug

    # The fixture, in a fresh repository, every text file with LF line endings as a Linux checkout holds them.
    $fixture = Join-Path $root "tests\fixtures\rehearsal-blazor"
    if (Test-Path -LiteralPath $State.RepoDir) { Remove-Item -LiteralPath $State.RepoDir -Recurse -Force }
    New-Item -ItemType Directory -Path $State.RepoDir -Force | Out-Null
    Get-ChildItem -LiteralPath $fixture -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
        $target = Join-Path $State.RepoDir $_.FullName.Substring($fixture.Length + 1)
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [System.IO.File]::WriteAllText($target, ([System.IO.File]::ReadAllText($_.FullName) -replace "`r`n", "`n"))
    }
    $State.PushUrl = "http://127.0.0.1:$($State.BackPort)/git/$($State.ProjectId)/$($State.RepoSlug).git"
    Invoke-RehearsalGit $State @("init", "-q", "-b", "main")
    Invoke-RehearsalGit $State @("add", "-A")
    Invoke-RehearsalGit $State @("commit", "-q", "-m", "feat: rehearsal Blazor application")
    Invoke-RehearsalGit $State @("branch", $rehearsalBranch)
    Invoke-RehearsalGit $State @("push", "-q", $State.PushUrl, "main", $rehearsalBranch)

    $projectServer = Invoke-RehearsalApi $State Post "/api/projects/$($State.ProjectId)/servers" @{
        type = "AgentServer"; serverId = $State.ServerId; displayName = $State.AgentName; host = $State.ServerHostname; port = $null; notes = $null }
    $environment = Invoke-RehearsalApi $State Post "/api/environments" @{
        name = "prod"; description = ""; type = "Production"; projectId = $State.ProjectId; requireApproval = $false; approvalTimeoutMinutes = 1440; serverIds = @() }
    Invoke-RehearsalApi $State Post "/api/environments/$($environment.id)/project-servers/$($projectServer.id)" | Out-Null
    Write-RehearsalLine "Project $($State.ProjectId), repository $($State.RepoSlug) (main and $rehearsalBranch pushed), server linked to prod." Green
}

# The browser half: the setup wizard in the QA browser image, on the stack's network, with the same limits.
function Invoke-RehearsalWizard([hashtable]$State, [hashtable]$Limits) {
    Write-Step "Rehearsal: creating the pipelines through the setup wizard (browser)"
    $runner = "$($State.Project)-wizard"
    & docker rm -f $runner 2>&1 | Out-Null
    $values = "DOMAIN=$($State.Domain);CERTBOT_EMAIL=rehearsal@$($State.Domain)"
    & docker create --name $runner --network "$($State.Project)_default" --cpus "$($Limits.Cpus)" --cpuset-cpus $Limits.CpuSet `
        --memory 2048m --memory-swap 2048m `
        -e "E2E_FRONTEND_URL=http://$($State.FrontHost):8080" -e "E2E_BACKEND_URL=http://$($State.BackHost):8080" `
        -e E2E_ADMIN_USER=admin -e "E2E_ADMIN_EMAIL=$rehearsalAdminEmail" -e "E2E_ADMIN_PASSWORD=$($State.AdminPassword)" `
        -e E2E_PRESERVE_DATABASE=true -e E2E_PREVIOUS_PAYLOAD_ONLY=false -e E2E_WORKERS=1 `
        -e "E2E_TEST_FILTER=FullyQualifiedName~PipelineRehearsalWizardTests" `
        -e E2E_TEST_PROJECT=tests/Aetheus.E2E/Aetheus.E2E.csproj -e "AETHEUS_E2E_RESULTS_DIR=$e2eContainerResults" `
        -e "REHEARSAL_PROJECT_ID=$($State.ProjectId)" -e "REHEARSAL_PREFIX=$($State.Prefix)" -e "REHEARSAL_BRANCH=$rehearsalBranch" `
        -e "REHEARSAL_WIZARD_OPTIONS=$($State.WizardOptions)" -e "REHEARSAL_PRODUCTION_VALUES=$values" `
        -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 $e2eImageTag sh /tmp/aetheus-rehearsal-e2e.sh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker create of $runner failed (exit $LASTEXITCODE)." }
    $scriptFile = Join-Path $env:TEMP "aetheus-rehearsal-e2e-$(Get-Random).sh"
    [System.IO.File]::WriteAllText($scriptFile, $e2eRunnerScript)
    & docker cp $scriptFile "${runner}:/tmp/aetheus-rehearsal-e2e.sh" | Out-Null
    Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    & docker start -a $runner 2>&1 | Where-Object { $_ -match '^\s*(Failed|Passed|Skipped|Total tests|\s*Error Message)|Assert|Expected|But was|Exception' } |
        ForEach-Object { Write-RehearsalLine $_ DarkGray }
    $wizardDir = Join-Path $State.OutDir "wizard"
    & docker cp "${runner}:${e2eContainerResults}/." $wizardDir 2>&1 | Out-Null
    & docker rm -f $runner 2>&1 | Out-Null
    # No fake green: the wizard counts only when its one test really ran and passed.
    $summary = Write-VpsBenchTrxSummary (Join-Path $wizardDir "e2e.trx")
    if ($null -eq $summary -or $summary.Total -ne 1 -or $summary.Failed -ne 0) {
        throw "The setup wizard test did not run and pass exactly once (evidence: $wizardDir)."
    }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $wizardDir "e2e.trx") -Raw
    if ([int]$trx.TestRun.ResultSummary.Counters.passed -ne 1) { throw "The setup wizard test was not executed (evidence: $wizardDir)." }

    $pipelines = @((Invoke-RehearsalApi $State Get "/api/pipelines?projectId=$($State.ProjectId)&page=1&pageSize=100").items)
    $State.Pipelines = @{}
    foreach ($suffix in @("candidate", "nightly", "deploy-prod", "release-fast")) {
        $pipeline = $pipelines | Where-Object { $_.name -eq "$($State.Prefix)-$suffix" } | Select-Object -First 1
        if (-not $pipeline) { throw "The wizard did not create $($State.Prefix)-$suffix (created: $(($pipelines | ForEach-Object name) -join ', '))." }
        $State.Pipelines[$suffix] = [int]$pipeline.id
    }
    foreach ($pipeline in $pipelines) {
        (Invoke-RehearsalApi $State Get "/api/pipelines/$($pipeline.id)").yamlDefinition |
            Set-Content -LiteralPath (Join-Path $wizardDir "$($pipeline.name).yaml") -Encoding utf8
    }
    Write-RehearsalLine "Wizard created: $(($pipelines | ForEach-Object name) -join ', ')." Green
}

# The certificate authority stub (see the header): a self-signed certificate for the application's host, trusted by the box.
function Set-RehearsalApplicationHost([hashtable]$State) {
    $libraries = @((Invoke-RehearsalApi $State Get "/api/variable-libraries?projectId=$($State.ProjectId)&page=1&pageSize=100").items)
    $prod = $libraries | Where-Object { $_.name -like "*.prod" } | Select-Object -First 1
    if (-not $prod) { throw "The wizard created no '<project>.prod' library (libraries: $(($libraries | ForEach-Object name) -join ', '))." }
    $entries = @{}
    foreach ($entry in @((Invoke-RehearsalApi $State Get "/api/variable-libraries/$($prod.id)/entries?page=1&pageSize=200").items)) { $entries[$entry.key] = [string]$entry.value }
    if ($entries["DOMAIN"] -ne $State.Domain) { throw "The library $($prod.name) holds DOMAIN='$($entries["DOMAIN"])', not the '$($State.Domain)' typed in the wizard." }
    $subdomain = $entries["APP_SUBDOMAIN"]
    $State.AppHost = "$($entries["HOST_PREFIX"])$(if ($subdomain) { "$subdomain." })$($entries["DOMAIN"])"
    Write-RehearsalLine "Application host $($State.AppHost) (library $($prod.name)); placing its self-signed certificate (CA stub)." Cyan
    Invoke-RehearsalVps $State @'
set -eu
LIVE="/etc/letsencrypt/live/$APP_HOST"
mkdir -p "$LIVE"
openssl req -x509 -newkey rsa:2048 -nodes -days 365 -keyout "$LIVE/privkey.pem" -out "$LIVE/fullchain.pem" \
  -subj "/CN=$APP_HOST/O=Aetheus pipeline rehearsal (self-signed, NOT a public CA)" \
  -addext "subjectAltName=DNS:$APP_HOST" -addext "basicConstraints=critical,CA:TRUE" >/dev/null 2>&1
chmod 644 "$LIVE/fullchain.pem"; chmod 600 "$LIVE/privkey.pem"
install -m 644 "$LIVE/fullchain.pem" "/usr/local/share/ca-certificates/rehearsal-$APP_HOST.crt"
update-ca-certificates >/dev/null 2>&1
grep -q " $APP_HOST\$" /etc/hosts || echo "127.0.0.1 $APP_HOST" >> /etc/hosts
'@ @{ APP_HOST = $State.AppHost } | Out-Null
    if ($State.LastVpsExit -ne 0) { throw "Could not place the certificate of $($State.AppHost) on the simulated VPS." }
}

# -------------------------------------------------------------- runs ---------------------------------------------------------------

$rehearsalTerminal = @("Success", "Failed", "Cancelled", "Partial", "RolledBack", "Rejected")

function Write-RehearsalRunFailure([hashtable]$State, $Run, [int]$Depth = 0) {
    $indent = "  " * $Depth
    Write-RehearsalLine "${indent}Run $($Run.id) ($($Run.pipelineName) #$($Run.buildNumber)): $($Run.status)" Red
    foreach ($warning in @($Run.warnings)) { if ($warning) { Write-RehearsalLine "${indent}  warning: $warning" Yellow } }
    foreach ($step in @($Run.steps) | Where-Object { $_.status -in @("Failed", "Timeout", "Cancelled") }) {
        Write-RehearsalLine "${indent}  step [$($step.stageName)] $($step.stepName): $($step.status), exit $($step.exitCode), $($step.failureReason)" Red
        if ($step.triggeredRunId -and $Depth -lt 3) {
            Write-RehearsalRunFailure $State (Invoke-RehearsalApi $State Get "/api/pipelines/runs/$($step.triggeredRunId)") ($Depth + 1)
        } elseif ($step.taskId) {
            try {
                @(Invoke-RehearsalApi $State Get "/api/logs/task/$($step.taskId)?maxLines=25") | ForEach-Object { Write-RehearsalLine "${indent}    | $($_.message)" DarkGray }
            } catch { Write-RehearsalLine "${indent}    (log unreadable: $($_.Exception.Message))" DarkGray }
        }
    }
}

function Wait-RehearsalRun([hashtable]$State, [int]$RunId, [int]$TimeoutMinutes = 120) {
    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    $lastStatus = ""
    while ([DateTime]::UtcNow -lt $deadline) {
        $run = Invoke-RehearsalApi $State Get "/api/pipelines/runs/$RunId"
        if ($run.status -ne $lastStatus) { Write-RehearsalLine "run $RunId ($($run.pipelineName) #$($run.buildNumber)): $($run.status)" DarkGray; $lastStatus = $run.status }
        if ($run.status -in $rehearsalTerminal) { return $run }
        if ($run.status -eq "WaitingForApproval") {
            # A test stack, not production: the rehearsal approves what a run asks, and says so.
            foreach ($approval in @(Invoke-RehearsalApi $State Get "/api/pipelines/approvals/pending") | Where-Object { $_.pipelineRunId -eq $RunId }) {
                Invoke-RehearsalApi $State Post "/api/pipelines/approvals/$($approval.approvalId)/decide" @{ decision = "Approved"; comments = "Approved by the pipeline rehearsal (test stack)." } | Out-Null
                Write-RehearsalLine "approved '$($approval.stageName)' of run $RunId" Yellow
                $State.Approvals++
            }
        }
        Start-Sleep -Seconds 15
        if ([DateTime]::UtcNow.Minute % 10 -eq 0) { Connect-RehearsalApi $State }
    }
    throw "Run $RunId did not end within $TimeoutMinutes minutes."
}

function Start-RehearsalRun([hashtable]$State, [string]$Suffix, [hashtable]$Parameters = @{}) {
    $pipelineId = $State.Pipelines[$Suffix]
    $declared = @(Invoke-RehearsalApi $State Get "/api/pipelines/$pipelineId/parameters")
    $given = @{}
    foreach ($key in $Parameters.Keys) { if ($declared | Where-Object name -eq $key) { $given[$key] = $Parameters[$key] } }
    $missing = @($declared | Where-Object { $_.required -and [string]::IsNullOrEmpty([string]$_.default) -and -not $given.ContainsKey($_.name) } | ForEach-Object name)
    if ($missing.Count -gt 0) { throw "$($State.Prefix)-$Suffix requires parameters the rehearsal does not give: $($missing -join ', ')." }
    $run = Invoke-RehearsalApi $State Post "/api/pipelines/$pipelineId/run" @{ parameters = $given; sourceBranch = $rehearsalBranch; idempotencyKey = [guid]::NewGuid().ToString() }
    Write-RehearsalLine "started $($State.Prefix)-$Suffix run $($run.id) $(if ($given.Count) { "($(($given.Keys | ForEach-Object { "$_=$($given[$_])" }) -join ', '))" })" Cyan
    return [int]$run.id
}

function Get-RehearsalStepLog([hashtable]$State, $Step, [int]$Lines = 400) {
    if (-not $Step.taskId) { return @() }
    return @(Invoke-RehearsalApi $State Get "/api/logs/task/$($Step.taskId)?maxLines=$Lines") | ForEach-Object { "$($_.message)" }
}

# Keeps what a failure needs to be diagnosed without rerunning: for a run, the pipelines it triggered (quality, security, QA,
# and theirs in turn) as run-<id>.json, the run's grade and gate result (/api/analysis/runs/<id>/result: every measure, its
# observed value and threshold), and each step's log as logs/run-<id>/<stage>-<step>.log. The calls are the ones the
# rehearsal already makes (run, step log); an evidence call that fails is noted in the file, never hidden and never fatal.
function Save-RehearsalRunEvidence([hashtable]$State, $Run, [int]$Depth = 0) {
    $logDir = Join-Path $State.OutDir "logs\run-$($Run.id)"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    try {
        Invoke-RehearsalApi $State Get "/api/analysis/runs/$($Run.id)/result" | ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath (Join-Path $State.OutDir "analysis-result-$($Run.id).json") -Encoding utf8
    } catch { "analysis result unreadable: $($_.Exception.Message)" | Set-Content -LiteralPath (Join-Path $State.OutDir "analysis-result-$($Run.id).json") -Encoding utf8 }
    foreach ($step in @($Run.steps)) {
        if ($step.taskId) {
            $name = ("$($step.stageName)-$($step.stepName)" -replace '[^A-Za-z0-9._-]+', '_')
            try { (Get-RehearsalStepLog $State $step 5000) | Set-Content -LiteralPath (Join-Path $logDir "$name.log") -Encoding utf8 }
            catch { "step log unreadable: $($_.Exception.Message)" | Set-Content -LiteralPath (Join-Path $logDir "$name.log") -Encoding utf8 }
        }
        if ($step.triggeredRunId -and $Depth -lt 3) {
            $child = Invoke-RehearsalApi $State Get "/api/pipelines/runs/$($step.triggeredRunId)"
            $child | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $State.OutDir "run-$($child.id).json") -Encoding utf8
            Save-RehearsalRunEvidence $State $child ($Depth + 1)
        }
    }
}

# A run that stands for a selected option (the candidate, the nightly) must have really run it, never been Cancelled or
# Skipped: every step of the run Success (but the stages an option not selected owns), and each pipeline it triggers
# (quality, security, QA) Success with its required stages Success. A child stage skipped by its own condition (the image
# scan of a project with no container image, the JavaScript scan of one with no lockfile) is not an option: it is named in
# the summary, not failed. The QA's browser tests must have printed a VSTest summary with passed tests and no failure;
# with $ExpectRollback the rollback to V-1 must really have been exercised, not attested as bootstrap.
function Test-RehearsalRunCoverage([hashtable]$State, [string]$Label, $Run, [switch]$ExpectRollback) {
    $selected = $State.WizardOptions -split ','
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($step in @($Run.steps)) {
        $ownerOption = @($rehearsalOptionStages.Keys | Where-Object { $_ -notin $selected -and $step.stageName -match "^(Nightly)?$($rehearsalOptionStages[$_])$" }) | Select-Object -First 1
        if ($step.status -ne "Success") {
            if ($ownerOption) { $State.NotRunByDesign.Add("${Label}: stage $($step.stageName) $($step.status) (option '$ownerOption' not selected)"); continue }
            $problems.Add("[$($step.stageName)] $($step.stepName): $($step.status)$(if ($step.skippedCondition) { " (condition $($step.skippedCondition))" })")
            continue
        }
        $kind = $step.stageName -replace '^Nightly', ''
        if (-not $step.triggeredRunId -or -not $rehearsalRequiredStages.ContainsKey($kind)) { continue }
        $child = Invoke-RehearsalApi $State Get "/api/pipelines/runs/$($step.triggeredRunId)"
        $child | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $State.OutDir "run-$($child.id).json") -Encoding utf8
        $name = "$Label $kind run $($child.id) ($($child.pipelineName))"
        if ($child.status -ne "Success") { $problems.Add("$name ended $($child.status)"); continue }
        $report = [System.Collections.Generic.List[string]]::new()
        foreach ($required in $rehearsalRequiredStages[$kind]) {
            $stageSteps = @($child.steps | Where-Object stageName -eq $required)
            if ($stageSteps.Count -eq 0) { $problems.Add("$name has no stage '$required'"); continue }
            # A step of the stage skipped by the template's own condition (the JavaScript scan of a project with no lockfile) is
            # named in the summary; any other step of it that is not Success, or a stage with no Success step at all, fails.
            $bad = @($stageSteps | Where-Object { $_.status -ne "Success" -and -not $_.skippedCondition })
            $ran = @($stageSteps | Where-Object { $_.status -eq "Success" })
            if ($bad.Count -gt 0 -or $ran.Count -eq 0) { $problems.Add("$name stage '$required': $(($stageSteps | ForEach-Object status) -join ', ')"); continue }
            foreach ($skipped in @($stageSteps | Where-Object { $_.status -ne "Success" })) {
                $State.NotRunByDesign.Add("$name stage $required step '$($skipped.stepName)' $($skipped.status) (template condition $($skipped.skippedCondition))")
            }
            $report.Add("$required=Success")
        }
        foreach ($other in @($child.steps | Where-Object { $_.status -ne "Success" -and $_.stageName -notin $rehearsalRequiredStages[$kind] })) {
            $State.NotRunByDesign.Add("$name stage $($other.stageName) $($other.status)$(if ($other.skippedCondition) { " (template condition $($other.skippedCondition) false)" })")
        }
        foreach ($warning in @($child.warnings) | Where-Object { $_ }) {
            Write-RehearsalLine "$name warning: $warning" Yellow
            if ("$warning" -match 'No browser end-to-end command|No QA environment was deployed|browser tests were not run') { $problems.Add("$name warning says a step did nothing: $warning") }
        }
        if ($kind -eq "QA") {
            $e2e = @($child.steps | Where-Object stageName -eq "V E2E suite") | Select-Object -First 1
            $log = Get-RehearsalStepLog $State $e2e
            if (-not ($log | Where-Object { $_ -match 'Browser tests in ' })) { $problems.Add("$name V E2E suite did not run in the browser image (no 'Browser tests in' line)") }
            elseif (-not ($log | Where-Object { $_ -match 'Passed!\s+-\s+Failed:\s+0,\s+Passed:\s+[1-9]' })) { $problems.Add("$name V E2E suite printed no passing VSTest summary (Failed: 0, Passed: n)") }
            else { $report.Add("browser tests passed: $((($log | Where-Object { $_ -match 'Passed!\s+-\s+Failed:' } | Select-Object -Last 1) -replace '^\s+', ''))") }
            if ($ExpectRollback) {
                $rollback = Get-RehearsalStepLog $State (@($child.steps | Where-Object stageName -eq "Roll back to V-1") | Select-Object -First 1)
                if (($rollback | Where-Object { $_ -match 'Bootstrap attestation' }) -or -not ($rollback | Where-Object { $_ -match 'after the rollback' })) { $problems.Add("$name rollback to V-1 was not exercised (bootstrap attestation or no probe after the rollback)") }
                else { $report.Add("rollback to V-1 exercised and probed") }
            }
        }
        $State.Coverage.Add("${name}: $($report -join '; ')")
    }
    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-RehearsalLine "coverage: $_" Red }
        throw "$Label did not really run its selected options: $($problems -join ' | ')"
    }
}

# One step of the scenario: its run must end in Success, else the rehearsal stops on it with the failing step.
function Complete-RehearsalRun([hashtable]$State, [string]$Label, [int]$RunId, [switch]$Coverage, [switch]$ExpectRollback) {
    $run = Wait-RehearsalRun $State $RunId
    $State.Results.Add([pscustomobject]@{ Step = $Label; Run = $RunId; Pipeline = $run.pipelineName; Status = $run.status })
    $run | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $State.OutDir "run-$RunId.json") -Encoding utf8
    # The runs it triggered and their step logs are kept whatever the outcome (a failure far from the parent run needs them).
    if ($Coverage -or $run.status -ne "Success") { Save-RehearsalRunEvidence $State $run }
    if ($run.status -ne "Success") {
        Write-RehearsalRunFailure $State $run
        throw "$Label failed: run $RunId ended $($run.status)."
    }
    if ($Coverage) { Test-RehearsalRunCoverage $State $Label $run -ExpectRollback:$ExpectRollback }
    return $run
}

function Get-RehearsalDownstreamRun([hashtable]$State, [int]$RunId) {
    $run = Invoke-RehearsalApi $State Get "/api/pipelines/runs/$RunId"
    $id = Wait-RehearsalCondition "the deployment started by candidate run $RunId (on_success)" 180 {
        $lineage = Invoke-RehearsalApi $State Get "/api/pipelines/runs/$RunId/lineage"
        @($lineage.downstream) | Where-Object { $_.runId } | Select-Object -First 1 -ExpandProperty runId
    } 10
    if (-not $id) { foreach ($warning in @($run.warnings)) { Write-RehearsalLine "warning: $warning" Yellow } }
    return [int]$id
}

function Get-RehearsalReleaseVersion([hashtable]$State, [int]$RunId) {
    $release = Invoke-RehearsalApi $State Get "/api/releases/by-run/$RunId"
    if (-not $release.version) { throw "Run $RunId published no release version." }
    return [string]$release.version
}

# The deployed application, through Apache and its certificate on the simulated VPS, as a visitor reaches it.
function Test-RehearsalDeployment([hashtable]$State, [string]$Label, [string]$ExpectedVersion) {
    $probe = Invoke-RehearsalVps $State @'
set -u
for i in $(seq 1 24); do
  HEALTH="$(curl -fsS --max-time 10 "https://$APP_HOST/health" 2>&1)" && VERSION="$(curl -fsS --max-time 10 "https://$APP_HOST/version" 2>&1)" && { echo "health=$HEALTH"; echo "version=$VERSION"; exit 0; }
  sleep 5
done
echo "health=$HEALTH"; exit 1
'@ @{ APP_HOST = $State.AppHost }
    $health = (($probe | Where-Object { "$_" -like "health=*" } | Select-Object -Last 1) -replace '^health=', '')
    $version = (($probe | Where-Object { "$_" -like "version=*" } | Select-Object -Last 1) -replace '^version=', '')
    $ok = $State.LastVpsExit -eq 0 -and $health -eq "Healthy" -and $version -eq $ExpectedVersion
    $State.Results.Add([pscustomobject]@{ Step = $Label; Run = ""; Pipeline = "https://$($State.AppHost)/health"; Status = "health '$health', version '$version' (expected $ExpectedVersion)" })
    if (-not $ok) { throw "${Label}: https://$($State.AppHost) answered health '$health', version '$version'; expected Healthy and $ExpectedVersion." }
    Write-RehearsalLine "${Label}: https://$($State.AppHost)/health Healthy, version $version." Green
}

function Invoke-RehearsalScenario([hashtable]$State) {
    Write-Step "Rehearsal: candidate 1 and the deployment it starts"
    $candidate1 = Start-RehearsalRun $State "candidate" @{ allowBootstrap = "true" }
    Complete-RehearsalRun $State "candidate 1" $candidate1 -Coverage | Out-Null
    $version1 = Get-RehearsalReleaseVersion $State $candidate1
    $deploy1 = Get-RehearsalDownstreamRun $State $candidate1
    Complete-RehearsalRun $State "deploy-prod 1 (release $version1)" $deploy1 | Out-Null
    Test-RehearsalDeployment $State "deployment 1" $version1

    Write-Step "Rehearsal: nightly"
    Complete-RehearsalRun $State "nightly" (Start-RehearsalRun $State "nightly") -Coverage | Out-Null

    Write-Step "Rehearsal: a commit that moves a source file, then candidate 2 and its deployment"
    Invoke-RehearsalGit $State @("checkout", "-q", $rehearsalBranch)
    # The wizard committed the .pipeline/ files on the server: build the move on top of them.
    Invoke-RehearsalGit $State @("pull", "-q", "--ff-only", $State.PushUrl, $rehearsalBranch)
    New-Item -ItemType Directory -Path (Join-Path $State.RepoDir "src\RehearsalBlazor\Versioning") -Force | Out-Null
    Invoke-RehearsalGit $State @("mv", "src/RehearsalBlazor/VersionInfo.cs", "src/RehearsalBlazor/Versioning/VersionInfo.cs")
    Invoke-RehearsalGit $State @("commit", "-q", "-m", "refactor: move VersionInfo into Versioning")
    Invoke-RehearsalGit $State @("push", "-q", $State.PushUrl, $rehearsalBranch)
    $candidate2 = Start-RehearsalRun $State "candidate"
    Complete-RehearsalRun $State "candidate 2 (file moved)" $candidate2 -Coverage -ExpectRollback | Out-Null
    $version2 = Get-RehearsalReleaseVersion $State $candidate2
    if ($version2 -eq $version1) { throw "Candidate 2 published the same version as candidate 1 ($version1)." }
    $deploy2 = Get-RehearsalDownstreamRun $State $candidate2
    Complete-RehearsalRun $State "deploy-prod 2 (release $version2)" $deploy2 | Out-Null
    Test-RehearsalDeployment $State "deployment 2" $version2

    Write-Step "Rehearsal: fast release"
    $fast = Start-RehearsalRun $State "release-fast"
    Complete-RehearsalRun $State "release-fast" $fast | Out-Null
    Test-RehearsalDeployment $State "fast release" (Get-RehearsalReleaseVersion $State $fast)
}

# ------------------------------------------------------------- entry ---------------------------------------------------------------

function Remove-RehearsalStack([hashtable]$State) {
    Write-RehearsalLine "Removing the rehearsal stack, its containers, network, volumes and images ($($State.Project))..." DarkGray
    & docker rm -f "$($State.Project)-wizard" 2>&1 | Out-Null
    & docker compose -p $State.Project --env-file $State.EnvFile @($State.ComposeArgs) down -v --remove-orphans 2>&1 | Out-Null
    & docker rmi $State.BackImage $State.FrontImage 2>&1 | Out-Null
}

function Invoke-PipelineRehearsal([int]$Cpus, [int]$MemoryGb, [switch]$Keep, [string]$Reuse) {
    $dockerOs = $null
    try { $dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null | Select-Object -First 1) } catch { $dockerOs = $null }
    if ("$dockerOs".Trim() -ne "linux") { Write-RehearsalLine "Docker did not report a running Linux engine (got '$dockerOs')." Red; return 1 }
    if ($MemoryGb -lt 10) { Write-RehearsalLine "RehearsalMemoryGb must be at least 10 (back 2 GB, database 1 GB, browser 2 GB, VPS the rest), got $MemoryGb." Red; return 1 }
    $limits = Get-VpsBenchLimits $Cpus $MemoryGb
    $reusing = -not [string]::IsNullOrEmpty($Reuse)
    $runId = if ($reusing) { $Reuse } else { (Get-Date -Format "MMddHHmmss") }
    $stackDir = Join-Path $rehearsalResultsRoot $runId
    $statePath = Join-Path $stackDir "stack.json"
    $proj = "$rehearsalPrefix-$runId"
    $scenarioId = (Get-Date -Format "HHmmss")
    $State = @{
        RunId = $runId; Project = $proj; Approvals = 0
        BackHost = "aetheus-rehearsal-$runId-back"; FrontHost = "aetheus-rehearsal-$runId-front"; VpsHost = "$proj-vpssim"
        BackImage = "${rehearsalPrefix}-back:$runId"; FrontImage = "${rehearsalPrefix}-front:$runId"
        EnvFile = Join-Path $stackDir "stack.env"; OverrideFile = Join-Path $stackDir "rehearsal.compose.yml"
        DatabaseMb = 1024; VpsMb = [int]($MemoryGb * 1024 - 2048 - 256 - 1024 - 2048)
        AgentName = "rehearsal-vps"; Domain = "rehearsal.test"
        ProjectName = "Rehearsal Blazor $scenarioId"; Prefix = "rehearsal-$scenarioId"
        WizardOptions = $rehearsalWizardOptions; Coverage = [System.Collections.Generic.List[string]]::new()
        NotRunByDesign = [System.Collections.Generic.List[string]]::new()
        OutDir = Join-Path $stackDir "scenario-$scenarioId"
        Results = [System.Collections.Generic.List[object]]::new()
    }
    $State.ComposeArgs = @("-f", (Join-Path $root "deploy\compose\remote.compose.yml"), "-f", (Join-Path $root "deploy\compose\vpssim.compose.yml"), "-f", $State.OverrideFile)
    $State.RepoDir = Join-Path $State.OutDir "repo"
    New-Item -ItemType Directory -Path $State.OutDir -Force | Out-Null
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = 1
    $failure = $null
    try {
        if ($reusing) {
            if (-not (Test-Path -LiteralPath $statePath)) { throw "No kept rehearsal stack ${proj}: $statePath is missing." }
            $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            foreach ($name in @("AdminPassword", "PfxPassword", "BackPort", "ServerId", "ServerHostname")) { $State[$name] = $saved.$name }
            $State.ApiUrl = "http://127.0.0.1:$($State.BackPort)"
            Connect-RehearsalApi $State
            if (-not (Build-E2eRunnerImage)) { throw "The QA browser image could not be built." }
        } else {
            $State.AdminPassword = New-VpsBenchSecret 16
            $State.PfxPassword = New-VpsBenchSecret 16
            $State.BackPort = Get-RehearsalFreePort
            $State.ApiUrl = "http://127.0.0.1:$($State.BackPort)"
            Write-Step "Pipeline rehearsal $proj ($($limits.Cpus) CPUs on cpuset $($limits.CpuSet), $MemoryGb GB in all)"
            Start-RehearsalStack $State $limits
            Connect-RehearsalApi $State
            Install-RehearsalAgent $State
            @{ AdminPassword = $State.AdminPassword; PfxPassword = $State.PfxPassword; BackPort = $State.BackPort; ServerId = $State.ServerId; ServerHostname = $State.ServerHostname } |
                ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
        }
        New-RehearsalProject $State
        Invoke-RehearsalWizard $State $limits
        Set-RehearsalApplicationHost $State
        Invoke-RehearsalScenario $State
        $exit = 0
    } catch {
        $failure = $_.Exception.Message
        Write-RehearsalLine "REHEARSAL FAILED: $failure" Red
    } finally {
        Write-Step "Pipeline rehearsal summary ($proj, scenario $scenarioId, $([math]::Round($watch.Elapsed.TotalMinutes, 1)) min)"
        foreach ($result in $State.Results) {
            $color = if ($result.Status -eq "Success" -or "$($result.Status)" -like "health 'Healthy'*") { "Green" } else { "Red" }
            Write-RehearsalLine ("{0,-36} {1,-8} {2,-40} {3}" -f $result.Step, $result.Run, $result.Pipeline, $result.Status) $color
        }
        Write-RehearsalLine "Wizard options selected: $($State.WizardOptions)" Cyan
        Write-RehearsalLine "Options deliberately not selected: $($rehearsalNotSelected -join ', ')" Cyan
        foreach ($line in $State.Coverage) { Write-RehearsalLine "ran for real: $line" Green }
        foreach ($line in ($State.NotRunByDesign | Select-Object -Unique)) { Write-RehearsalLine "not run, by design: $line" Yellow }
        Write-RehearsalLine "Approvals given by the rehearsal: $($State.Approvals). Stubbed: the certificate authority only (self-signed certificate for $($State.AppHost))." Cyan
        if ($failure) { Write-RehearsalLine "Result: FAILED - $failure" Red } else { Write-RehearsalLine "Result: every run Success, every step of the selected options (quality, security, QA with its browser tests and rollback) really ran, every deployment answered with its version." Green }
        $State.Results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $State.OutDir "summary.json") -Encoding utf8
        Write-RehearsalLine "Evidence: $($State.OutDir)" Cyan
        if ($Keep -or $reusing) {
            Write-RehearsalLine "Stack $proj kept: run the scenario again with  .\launch-windows.ps1 -pr -RehearsalReuse $runId" Yellow
            $composeText = ($State.ComposeArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
            Write-RehearsalLine "Remove it with: docker compose -p $proj --env-file `"$($State.EnvFile)`" $composeText down -v ; docker rmi $($State.BackImage) $($State.FrontImage)" Yellow
        } elseif (Test-Path -LiteralPath $State.EnvFile) {
            Remove-RehearsalStack $State
        }
        if (-not $Keep -and -not $reusing) { & docker volume rm $benchNuGetVolume 2>&1 | Out-Null }
    }
    return $exit
}
