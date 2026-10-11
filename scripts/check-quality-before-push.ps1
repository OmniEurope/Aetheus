# SPDX-License-Identifier: EUPL-1.2
# Pre-push check (recette R2-144): the quality findings the pipeline reports on duplication and
# JavaScript lint, found on the workstation before the push instead of in the nightly. Duplicated block
# 1176 and no-control-regex 1243 reached production analysis although both run in seconds here.
#
# - jscpd runs on the commit being pushed, exported with LF endings as the pipeline's Linux checkout has
#   them: on the CRLF working copy jscpd finds a different set of clones (it found 0 where the pipeline
#   found 1), so the working tree is never what is measured.
# - ESLint runs on the tracked JavaScript files with the repository's eslint.config.cjs, as the pipeline
#   does on its clean checkout (the working tree holds untracked build output that would drown it).
# The using-directive order (dotnet format IMPORTS) is enforced by UsingDirectiveOrderAuditTests, which
# the test-backend pre-push task already runs.
param([string] $Revision = 'HEAD')
$ErrorActionPreference = 'Stop'

$repo = (git rev-parse --show-toplevel).Trim()
$node = (Get-Command node -ErrorAction SilentlyContinue)?.Source
$jscpd = Join-Path $repo 'node_modules/jscpd/run-jscpd.js'
$eslint = Join-Path $repo 'node_modules/eslint/bin/eslint.js'
if (-not $node -or -not (Test-Path $jscpd) -or -not (Test-Path $eslint)) {
    Write-Error 'node and the locked quality tools are required (run: npm ci). The check refuses rather than passing unchecked.'
    exit 1
}

$failed = $false
$work = Join-Path ([IO.Path]::GetTempPath()) ('aetheus-prepush-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $tree = Join-Path $work 'tree'
    $report = Join-Path $work 'jscpd'
    New-Item -ItemType Directory -Path $tree | Out-Null
    $archive = Join-Path $work 'head.tar'
    git -c core.autocrlf=false -c core.eol=lf archive --format=tar -o $archive $Revision
    if ($LASTEXITCODE -ne 0) { Write-Error 'Could not export the commit being pushed.'; exit 1 }
    # Windows' own bsdtar: the Git for Windows tar first on PATH reads "C:" as a remote host.
    $tar = if ($env:SystemRoot -and (Test-Path (Join-Path $env:SystemRoot 'System32/tar.exe'))) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
    & $tar -xf $archive -C $tree
    if ($LASTEXITCODE -ne 0) { Write-Error 'Could not unpack the exported commit.'; exit 1 }

    Push-Location $tree
    try {
        & $node $jscpd . --reporters json --output $report *> $null
    }
    finally {
        Pop-Location
    }
    $reportFile = Join-Path $report 'jscpd-report.json'
    if (-not (Test-Path $reportFile)) { Write-Error 'jscpd produced no report.'; exit 1 }
    $duplicates = @((Get-Content $reportFile -Raw | ConvertFrom-Json).duplicates)
    if ($duplicates.Count -gt 0) {
        $failed = $true
        Write-Host "jscpd: $($duplicates.Count) duplicated block(s), each one a pipeline finding:"
        foreach ($d in $duplicates) {
            Write-Host "  $($d.firstFile.name):$($d.firstFile.start) <-> $($d.secondFile.name):$($d.secondFile.start) ($($d.lines) lines)"
        }
    }

    $files = @(git -C $repo ls-files -- '*.js' '*.jsx' '*.mjs' '*.cjs' '*.ts' '*.tsx')
    for ($i = 0; $i -lt $files.Count; $i += 100) {
        $batch = $files[$i..([Math]::Min($i + 99, $files.Count - 1))]
        Push-Location $repo
        try {
            & $node $eslint --no-warn-ignored @batch
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed) {
    Write-Host ''
    Write-Host 'Fix the duplication or lint findings above before pushing: the quality pipeline reports each one.'
    exit 1
}
Write-Host 'Quality pre-check: no duplicated block, no lint error.'
