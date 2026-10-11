# SPDX-License-Identifier: EUPL-1.2
#
# Runs the V end-to-end suite against the schema V produced and turns its verdict into an advisory
# gate status. Exit code 10 means the suite found something, which downgrades the gate to 1 without
# failing the step; any other non-zero code is a real failure and propagates.
#
# There is deliberately no `set -e` here: the suite's own non-zero exit is the input this script
# classifies, not a reason to abort before classifying it.
#
# This was 11 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged.
#
# Inputs, all read from the environment (none positional):
#   BUILD_PROJECTNAME  the project name the QA stack, database and user are derived from
#   WORKSPACE      the run's checkout, holding .qa-admin-pwd and .qa-current-commit
#   BUILD_BUILDID  the run id the disposable QA project names are derived from
STATUS=0
. deploy/scripts/qa/qa-stack-identity.sh
STACK="$(qa_stack "$BUILD_BUILDID")"
# Explicitly false: V has every feature its suite tests, so an inherited value must never skip one.
E2E_PREVIOUS_PAYLOAD_ONLY=false sh deploy/scripts/run-qa-e2e-suite.sh "$WORKSPACE" \
  "${STACK}-front" "${STACK}-back" \
  "$WORKSPACE/.qa-admin-pwd" "qa-${BUILD_BUILDID}-current" true \
  "aetheus-browser-smoke:$(cat "$WORKSPACE/.qa-current-commit")" \
  "$(cat "$WORKSPACE/.qa-current-commit")" || STATUS=$?
case "$STATUS" in 0) ;; 10) STATUS=1 ;; *) exit "$STATUS" ;; esac
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-current-e2e-gate-status"
echo "##aetheus[setvariable name=QA_CURRENT_E2E_GATE_STATUS]$STATUS"
[ "$STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh QA_CURRENT_E2E_FAILED_TESTS \
  "$WORKSPACE/.qa-test-results/qa-${BUILD_BUILDID}-current/e2e/failed-tests.txt"
echo "Current E2E findings status: $STATUS."
