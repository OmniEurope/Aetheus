# SPDX-License-Identifier: EUPL-1.2
# Pre-push task (user decision 2026-11-08, recette R-38): develop always carries every commit of origin/main.
#
# A commit written straight onto main (for example a pipeline whose definition lives on main, edited in Aetheus's
# pipeline editor: a4652b6b3 on 2026-11-03) makes main diverge from develop. Every candidate built from develop then
# misses it, and the production deployment's "Advance main" step can never fast-forward main again: it stayed on
# 2026-11-03 through several deployments.
#
# The user's rule: the hook does not block, it updates. When main has moved, origin/main is merged into develop and
# the merged develop is pushed in place of the old tip: git has already chosen the commit this push sends, so the
# hook pushes the merged tip itself (that inner push runs every pre-push task, tests included, on the merged tree,
# and this task finds nothing to merge there), then stops the outer push of the old tip, which the remote would
# refuse anyway since it is now behind. Only a merge conflict stops without pushing: it needs a choice, so the merge
# is undone and the conflicting files are named.
#
# Only a push from develop is concerned. Without a reachable origin the task says so and lets the push go.
$ErrorActionPreference = 'Stop'

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'develop') { exit 0 }

git fetch --quiet origin main 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: origin/main could not be fetched: develop was not compared with main." -ForegroundColor Yellow
    exit 0
}

git merge-base --is-ancestor origin/main HEAD
if ($LASTEXITCODE -eq 0) { exit 0 }

$missing = @(git log --format='%h %ad %s' --date=short HEAD..origin/main)
Write-Host "origin/main has $($missing.Count) commit(s) that develop does not contain, merging them into develop:"
$missing | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }

if (@(git status --porcelain --untracked-files=no).Count -gt 0) {
    Write-Host "ERROR: the working tree has uncommitted changes: commit them, then push again (main is merged then)." -ForegroundColor Red
    exit 1
}

git merge --no-edit -m "Merge origin/main into develop (pre-push: main had moved, R-38)" origin/main
if ($LASTEXITCODE -ne 0) {
    $conflicts = @(git diff --name-only --diff-filter=U)
    git merge --abort 2>$null
    Write-Host "ERROR: merging origin/main into develop conflicts on: $($conflicts -join ', ')." -ForegroundColor Red
    Write-Host "Run git merge origin/main, resolve, commit, then push again." -ForegroundColor Red
    exit 1
}

Write-Host "main merged into develop ($((git rev-parse --short HEAD).Trim())); pushing the merged develop."
git push origin HEAD:develop
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: the push of the merged develop failed (see above); the merge stays local, push again." -ForegroundColor Red
    exit 1
}
Write-Host "Done: origin/develop holds main. The push of the old tip stops here, there is nothing left to push." -ForegroundColor Green
exit 1
