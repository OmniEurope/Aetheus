# SPDX-License-Identifier: EUPL-1.2
# Pre-commit check: runs the same pinned Gitleaks as the security pipelines on the STAGED content, so a
# secret-shaped value (a real one or a test fixture) is refused at commit time instead of grading the
# next nightly down a day later (nightly 2712: findings #1245/#1246 on a fake test key).
#
# The image comes from scanner-manifest.json, the source the agent scans with, and the repository's
# .gitleaks.toml and .gitleaksignore apply exactly as they do there (the ignore file only holds
# history fingerprints). Without Docker the scan cannot run: the commit goes through with a visible warning,
# and the pipelines still scan.
$ErrorActionPreference = 'Stop'

$files = @(git diff --cached --name-only --diff-filter=ACMR)
if ($files.Count -eq 0) { exit 0 }

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'Secret scan skipped: Docker is not running. The security pipelines will still scan this commit.'
    exit 0
}

$manifest = Get-Content -Raw scanner-manifest.json | ConvertFrom-Json
$image = ($manifest.scanners | Where-Object key -eq 'gitleaks').image
if (-not $image) { Write-Error 'scanner-manifest.json has no gitleaks image.'; exit 1 }

$root = Join-Path ([IO.Path]::GetTempPath()) ("aetheus-staged-secrets-" + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    # In batches, as check-staged-whitespace.ps1 does: a large commit exceeds the Windows command-line limit.
    $paths = @('.gitleaks.toml', '.gitleaksignore') + $files | Select-Object -Unique
    $batches = $paths | ForEach-Object -Begin { $i = 0 } -Process { [pscustomobject]@{ Group = [math]::Floor($i++ / 200); Path = $_ } } |
        Group-Object Group
    foreach ($batch in $batches) {
        git -c core.autocrlf=false checkout-index "--prefix=$root/" -- @($batch.Group.Path)
        if ($LASTEXITCODE -ne 0) { Write-Error 'Could not write the staged files out of the index.'; exit 1 }
    }

    docker run --rm --network none --mount "type=bind,src=$root,dst=/src,readonly" --workdir /src --entrypoint gitleaks $image `
        dir . --config .gitleaks.toml --gitleaks-ignore-path .gitleaksignore --redact=100 --exit-code 1 --no-banner
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Gitleaks found a secret-shaped value in the staged files (listed above).'
        Write-Host 'A real secret: remove it. A test value: make it not look like a key (no sk-/token prefix).'
        Write-Host 'The current tree takes no exception (PLAN-002 D3): change the value, never allowlist it.'
        exit 1
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
