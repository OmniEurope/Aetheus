#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
#
# Publishes, beside a gate status, the names of the tests that made it non-zero:
# `##aetheus[setvariable name=<VARIABLE>]A, B, +N more` and a "Failed tests (N): ..." log line.
# The sources are the failed-tests.txt lists the suite runners write (an absent one is skipped).
#
# Naming the tests is a diagnostic, never the verdict: a failure here is said in the log and does
# not change the gate status or the step's outcome, which the caller already decided.
#
#   $1    the variable, <GATE>_FAILED_TESTS (QA_CURRENT_INTEGRATION_GATE_STATUS -> QA_CURRENT_INTEGRATION_FAILED_TESTS)
#   $2..  the failed-tests.txt lists (or TRX files) to merge
set -u
VARIABLE="${1:?variable name is required}"
shift
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)" \
  && "$NODE" deploy/scripts/list-failed-tests.mjs publish "$VARIABLE" "$@" \
  || echo "The failed test names for $VARIABLE could not be published." >&2
exit 0
