# SPDX-License-Identifier: EUPL-1.2
#
# Smokes the retained V-1 binary against the schema V produced and turns its verdict into an advisory
# gate status. In bootstrap mode there is no retained V-1 to smoke and it attests that instead of
# reporting a pass nothing produced.
#
# This was 14 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged apart from
# QA_PORT_BACK, noted below.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE     the run's checkout, holding .qa-bootstrap-mode and .qa-admin-pwd
#   QA_PORT_BACK  the run-scoped backend port this QA stack listens on. It was written `$(...)` in
#                 the YAML, which the control plane substitutes in a `shell:` block; inside a script
#                 that same text is a shell command substitution, so the caller passes it by name.
set -u
STATUS=0
if [ "$(cat "$WORKSPACE/.qa-bootstrap-mode")" = 1 ]; then
  echo "Bootstrap attestation: no retained V-1 binary exists, so there is nothing to smoke."
else
  TEST_EXIT=0
  sh deploy/scripts/run-qa-deployed-integration-suite.sh \
    "http://127.0.0.1:${QA_PORT_BACK}" "$WORKSPACE/.qa-admin-pwd" previous-smoke || TEST_EXIT=$?
  case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
fi
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-previous-smoke-gate-status"
echo "##aetheus[setvariable name=QA_PREVIOUS_SMOKE_GATE_STATUS]$STATUS"
[ "$STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh QA_PREVIOUS_SMOKE_FAILED_TESTS \
  "$WORKSPACE/.qa-test-results/previous-smoke/deployed/failed-tests.txt"
echo "Previous smoke findings status: $STATUS."
