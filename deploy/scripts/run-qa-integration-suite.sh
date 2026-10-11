#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

SOURCE_ROOT="${1:?source root is required}"
RESULT_LABEL="${2:?result label is required}"
WORKSPACE="${WORKSPACE:?WORKSPACE is required}"
RESULTS_DIR="$WORKSPACE/.qa-test-results/$RESULT_LABEL"
TRX="$RESULTS_DIR/integration.trx"

case "$RESULT_LABEL" in *[!a-zA-Z0-9_-]*|'') echo "Invalid integration result label." >&2; exit 2 ;; esac
test -f "$SOURCE_ROOT/tests/Aetheus.Back.IntegrationTests/Aetheus.Back.IntegrationTests.csproj"

DOTNET="$(sh "$WORKSPACE/deploy/scripts/ensure-dotnet-sdk.sh")"
DOTNET_ROOT="$(dirname "$DOTNET")"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
NODE="$(sh "$WORKSPACE/deploy/scripts/ensure-node-runtime.sh")"

# The prebuilt runtime manifest contains absolute content roots from its CI build
# workspace. Rebase them to the restored V or V-1 source before WebApplicationFactory
# initializes its physical file providers.
"$NODE" "$WORKSPACE/deploy/scripts/rebase-static-web-assets.mjs" "$SOURCE_ROOT"

rm -rf "$RESULTS_DIR"
mkdir -p "$RESULTS_DIR"
cd "$SOURCE_ROOT"
# A cold-cache CI run (aetheus-nightly) restores into a package folder of its own run, which exists
# only in that run's workspace, and the obj/ files restored here still name it. MSBuild then skips
# every package import guarded by Exists(), xunit.v3's Microsoft.Testing.Platform declaration with
# them, and dotnet test refuses the project as VSTest (nightly 2497, QA 2502, recette R2-062). A
# locked restore rebuilds the same graph from the lock files into this host's cache and rewrites
# obj/ only; the binaries under bin/ stay the restored ones (--no-build below).
NUGET_PROPS="tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props"
RESTORED_ROOT="$(sed -n 's:.*<NuGetPackageRoot[^>]*>\(.*\)</NuGetPackageRoot>.*:\1:p' "$NUGET_PROPS" | head -n 1)"
case "$RESTORED_ROOT" in
  *'$('*) ;;
  *)
    if [ -z "$RESTORED_ROOT" ] || [ ! -d "$RESTORED_ROOT" ]; then
      echo "The package folder the restored obj/ names ('$RESTORED_ROOT') is absent here; restoring the locked graph into this host's cache."
      "$DOTNET" restore tests/Aetheus.Back.IntegrationTests -p:Configuration=Release --locked-mode --verbosity minimal
    fi
    ;;
esac
# The tested source decides the runner, not this script: V runs on Microsoft.Testing.Platform
# (global.json "test.runner"), while a V-1 released before that move still carries VSTest and its
# loggers. Both produce the same TRX, which is all the classifier reads.
if grep -q '"Microsoft.Testing.Platform"' global.json; then
  set -- test --project tests/Aetheus.Back.IntegrationTests --configuration Release --no-build --no-restore \
    --no-progress --report-trx --report-trx-filename integration.trx --results-directory "$RESULTS_DIR"
else
  set -- test tests/Aetheus.Back.IntegrationTests --configuration Release --no-build --no-restore \
    --logger "console;verbosity=minimal" --logger "trx;LogFileName=integration.trx" \
    --results-directory "$RESULTS_DIR"
fi
RAW_TEST_EXIT=0
sh "$WORKSPACE/deploy/scripts/run-with-progress.sh" "Integration tests ($RESULT_LABEL)" \
  "$DOTNET" "$@" || RAW_TEST_EXIT=$?

STATUS="$("$NODE" "$WORKSPACE/deploy/scripts/classify-dotnet-test-result.mjs" "$RAW_TEST_EXIT" "$TRX" -)"
echo "Integration suite $RESULT_LABEL produced an executed-test proof with findings status $STATUS."
# The failed names, for the gate script to publish beside its status: a status of 1 alone sent the
# reader into a 5000-line log to find which tests made it (candidate 2634, QA run 2638).
if [ "$STATUS" != 0 ]; then
  "$NODE" "$WORKSPACE/deploy/scripts/list-failed-tests.mjs" record "$TRX" "$RESULTS_DIR/failed-tests.txt" \
    || echo "The failed test names could not be read from $TRX." >&2
fi
[ "$STATUS" = 0 ] || exit 10
