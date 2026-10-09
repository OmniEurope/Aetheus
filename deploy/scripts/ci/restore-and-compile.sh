# SPDX-License-Identifier: EUPL-1.2
#
# Pins the SDK and the Node runtime, honours the cold/warm cache scenario the run asked for, runs
# every delivery script's own test suite, then compiles the solution and clears the coverage tree.
#
# The `--test` list is the reason this step exists in its current shape: a test file that is in the
# repository but listed nowhere rots silently, which is exactly what happened to
# artifact-provenance.test.mjs (PLAN-006 lot 7).
#
# This was 30 lines inlined in .pipeline/aetheus-ci.yaml. The body is relocated unchanged.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE               the run's checkout, and the root of the run-scoped NuGet cache
#   BUILD_BUILDID           the run id the cold-cache directory is named after
#   AETHEUS_CACHE_SCENARIO  `warm` (default) or `cold`; anything else is refused
set -eu
DOTNET="$(sh deploy/scripts/ensure-dotnet-sdk.sh)"
case "${AETHEUS_CACHE_SCENARIO:-warm}" in
  warm) ;;
  cold)
    export NUGET_PACKAGES="$WORKSPACE/.pipeline-cache/nuget-$BUILD_BUILDID"
    rm -rf "$NUGET_PACKAGES"
    ;;
  *) echo "AETHEUS_CACHE_SCENARIO must be warm or cold." >&2; exit 1 ;;
esac
echo "##aetheus[setvariable name=CACHE_SCENARIO]${AETHEUS_CACHE_SCENARIO:-warm}"
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
# Every delivery script that has a test runs it here, so a test file cannot rot unnoticed
# the way artifact-provenance.test.mjs did: it was in the repository, listed nowhere, and
# had been failing for an unknown number of commits (PLAN-006 lot 7).
"$NODE" --test \
  deploy/scripts/assurance-contract.test.mjs \
  deploy/scripts/delivery-contract.test.mjs \
  deploy/scripts/nightly-evidence.test.mjs \
  deploy/scripts/nightly-qualification.test.mjs \
  deploy/scripts/artifact-provenance.test.mjs \
  deploy/scripts/opengrep-rules.test.mjs \
  deploy/scripts/integration-manifest.test.mjs \
  deploy/scripts/classify-dotnet-test-result.test.mjs \
  deploy/scripts/convert-dotnet-format-report.test.mjs \
  deploy/scripts/rebase-static-web-assets.test.mjs \
  deploy/scripts/rebase-quality-coverage-sources.test.mjs \
  deploy/scripts/verify-release-ancestry.test.mjs \
  deploy/scripts/merge-roslyn-security-sarif.test.mjs \
  deploy/scripts/ensure-tls-certificates.test.mjs \
  deploy/scripts/reconcile-env-urls.test.mjs \
  deploy/scripts/export-public-distribution.test.mjs \
  deploy/scripts/bluegreen-host-reset.test.mjs \
  deploy/scripts/demo-public-source.test.mjs \
  deploy/scripts/nightly-demo-evidence.test.mjs \
  deploy/scripts/record-storage-evidence.test.mjs \
  deploy/scripts/app-version.test.mjs \
  deploy/scripts/agent-host-helpers.test.mjs \
  deploy/scripts/deploy-env-file.test.mjs \
  deploy/scripts/local-docker.test.mjs \
  deploy/scripts/install-agent-linux.test.mjs \
  deploy/scripts/seal-assurance-contract.test.mjs \
  deploy/scripts/save-image-archive.test.mjs \
  deploy/scripts/run-analyzer-contract-tests.test.mjs \
  deploy/scripts/run-performance-smoke.test.mjs
# AetheusRoslynSarif makes every project write what its analyzers found as SARIF 2.1. It is a build
# flag rather than a separate analysis pass because the analyzers already ran: asking for the report
# costs nothing here and would cost a second full compile anywhere else.
rm -rf analysis/roslyn
"$DOTNET" build Aetheus.slnx --configuration Release --verbosity minimal \
  -p:AetheusRoslynSarif=true
# Only the security families reach the published report. A solution-wide build emits over a thousand
# analyzer results, nearly all style and performance advice, and burying the security ones among them
# is how a report stops being read.
"$NODE" deploy/scripts/merge-roslyn-security-sarif.mjs analysis/roslyn analysis/roslyn-security.sarif
rm -rf coverage
mkdir -p coverage/backend coverage/frontend coverage/agent
