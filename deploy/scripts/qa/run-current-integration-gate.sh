# SPDX-License-Identifier: EUPL-1.2
#
# Runs the V integration suites, in-tree then against the deployed backend, and turns their verdicts
# into one advisory gate status. Exit code 10 means a suite found something, which downgrades the
# gate to 1 without failing the step; any other non-zero code is a real failure and propagates.
#
# This was 13 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged apart from
# QA_PORT_BACK, noted below.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE     the run's checkout, holding .qa-admin-pwd
#   QA_PORT_BACK  the run-scoped backend port this QA stack listens on. It was written `$(...)` in
#                 the YAML, which the control plane substitutes in a `shell:` block; inside a script
#                 that same text is a shell command substitution, so the caller passes it by name.
set -u
STATUS=0
TEST_EXIT=0
sh deploy/scripts/run-qa-integration-suite.sh "$WORKSPACE" current || TEST_EXIT=$?
case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
TEST_EXIT=0
sh deploy/scripts/run-qa-deployed-integration-suite.sh \
  "http://127.0.0.1:${QA_PORT_BACK}" "$WORKSPACE/.qa-admin-pwd" current || TEST_EXIT=$?
case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-current-integration-gate-status"
echo "##aetheus[setvariable name=QA_CURRENT_INTEGRATION_GATE_STATUS]$STATUS"
[ "$STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh QA_CURRENT_INTEGRATION_FAILED_TESTS \
  "$WORKSPACE/.qa-test-results/current/failed-tests.txt" "$WORKSPACE/.qa-test-results/current/deployed/failed-tests.txt"
echo "Current integration findings status: $STATUS."
