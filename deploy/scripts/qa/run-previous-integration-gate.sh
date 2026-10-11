# SPDX-License-Identifier: EUPL-1.2
#
# Runs the retained V-1 integration suites, in-tree then against the deployed backend, and turns
# their verdicts into one advisory gate status. In bootstrap mode there is no retained V-1 and it
# attests that instead of reporting a pass nothing produced.
#
# Exit code 10 means a suite found something, which downgrades the gate to 1 without failing the
# step; any other non-zero code is a real failure and propagates.
#
# This was 17 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged apart from
# QA_PORT_BACK, noted below.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE     the run's checkout, holding .qa-bootstrap-mode, .nminus1-source and .qa-admin-pwd
#   QA_PORT_BACK  the run-scoped backend port this QA stack listens on. It was written `$(...)` in
#                 the YAML, which the control plane substitutes in a `shell:` block; inside a script
#                 that same text is a shell command substitution, so the caller passes it by name.
set -u
STATUS=0
if [ "$(cat "$WORKSPACE/.qa-bootstrap-mode")" = 1 ]; then
  echo "Bootstrap attestation: V integration passed; V-1 integration is inapplicable until this first release is retained."
else
  TEST_EXIT=0
  sh deploy/scripts/run-qa-integration-suite.sh "$WORKSPACE/.nminus1-source" previous || TEST_EXIT=$?
  case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
  TEST_EXIT=0
  sh deploy/scripts/run-qa-deployed-integration-suite.sh \
    "http://127.0.0.1:${QA_PORT_BACK}" "$WORKSPACE/.qa-admin-pwd" previous || TEST_EXIT=$?
  case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
fi
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-previous-integration-gate-status"
echo "##aetheus[setvariable name=QA_PREVIOUS_INTEGRATION_GATE_STATUS]$STATUS"
[ "$STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh QA_PREVIOUS_INTEGRATION_FAILED_TESTS \
  "$WORKSPACE/.qa-test-results/previous/failed-tests.txt" "$WORKSPACE/.qa-test-results/previous/deployed/failed-tests.txt"
echo "Previous integration findings status: $STATUS."
