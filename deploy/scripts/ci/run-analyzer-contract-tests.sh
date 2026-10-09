# SPDX-License-Identifier: EUPL-1.2
#
# Runs the analyzer contract suite and classifies its outcome into an advisory gate status.
#
# The classification is what makes the result trustworthy: it reads the TRX, so a run that produced no
# TRX at all cannot be reported as a pass, whatever exit code the runner returned.
#
# This was 17 lines inlined in .pipeline/aetheus-ci.yaml. The body is relocated unchanged.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE  the run's checkout, under which coverage/analyzers is written
set -u
mkdir -p coverage/analyzers
RAW_TEST_EXIT=0
(
  set -eu
  DOTNET="$(sh deploy/scripts/ensure-dotnet-sdk.sh)"
  # Microsoft.Testing.Platform (global.json "test.runner"): the TRX comes from its TrxReport extension.
  "$DOTNET" test --project tests/Aetheus.Analyzers.Tests \
    --configuration Release --no-build --no-progress \
    --results-directory "$WORKSPACE/coverage/analyzers" \
    --report-trx --report-trx-filename analyzers.trx
) || RAW_TEST_EXIT=$?
# From here on a failure is a technical fault, not a test result: the classifier refuses a missing or
# invalid TRX, and that refusal must fail the step instead of being published as an empty status
# followed by "Valid TRX evidence recorded" (DEP-015). Failed assertions stay status 1, advisory.
set -e
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
ANALYZER_STATUS="$("$NODE" deploy/scripts/classify-dotnet-test-result.mjs \
  "$RAW_TEST_EXIT" "$WORKSPACE/coverage/analyzers/analyzers.trx" -)"
echo "##aetheus[setvariable name=ANALYZER_TEST_GATE_STATUS]$ANALYZER_STATUS"
echo "Analyzer test findings status: $ANALYZER_STATUS. Valid TRX evidence recorded."
