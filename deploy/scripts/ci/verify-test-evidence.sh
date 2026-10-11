# SPDX-License-Identifier: EUPL-1.2
#
# Verifies that each suite actually left the evidence it claimed, and publishes the unit-test
# aggregate the assurance contract records.
#
# What this keeps from the old publish-blocking-test-gate.sh is only what is specific to this
# repository: where each suite writes its TRX and its Cobertura reports. Deciding what the statuses
# add up to, and blocking on that, is now the `type: gate-status` step that follows (PLAN-006 lot
# 11.3) - the rule that a status nobody published counts as a failure is exactly the one a project
# rewriting this in shell gets backwards, so it does not belong in shell.
#
# UNIT_TEST_GATE_STATUS is still published here, and still means the THREE unit suites only. The
# analyzer suite is graded as its own entry of the assurance contract, so folding it in would
# silently change what that contract asserts.
#
# Inputs, all read from the environment (none positional):
#   BACK_TEST_STATUS, FRONT_TEST_STATUS, AGENT_TEST_STATUS
#     the unit suites' statuses; each defaults to failing when absent, because a status nobody
#     published must not read as a pass
set -eu
BACK_STATUS="${BACK_TEST_STATUS:-1}"
FRONT_STATUS="${FRONT_TEST_STATUS:-1}"
AGENT_STATUS="${AGENT_TEST_STATUS:-1}"
BACK_COVERAGE_REPORTS="$(find coverage/backend -name coverage.cobertura.xml -type f | wc -l | tr -d ' ')"
FRONT_COVERAGE_REPORTS="$(find coverage/frontend -name coverage.cobertura.xml -type f | wc -l | tr -d ' ')"
AGENT_COVERAGE_REPORTS="$(find coverage/agent -name coverage.cobertura.xml -type f | wc -l | tr -d ' ')"
for STATUS_ENTRY in "$BACK_STATUS" "$FRONT_STATUS" "$AGENT_STATUS"
do
  case "$STATUS_ENTRY" in 0|1) ;; *) echo "A test evidence status is missing or invalid: $STATUS_ENTRY" >&2; exit 1 ;; esac
done
test -s coverage/backend/backend.trx
test -s coverage/frontend/frontend.trx
test -s coverage/agent/agent.trx
test -s coverage/analyzers/analyzers.trx
UNIT_STATUS=0
if test "$BACK_STATUS" != 0 || test "$FRONT_STATUS" != 0 || test "$AGENT_STATUS" != 0
then
  UNIT_STATUS=1
fi
if test "$BACK_COVERAGE_REPORTS" -lt 1 \
  || test "$FRONT_COVERAGE_REPORTS" -lt 1 \
  || test "$AGENT_COVERAGE_REPORTS" -lt 1
then
  echo "Expected at least one Cobertura report per product suite; found backend=$BACK_COVERAGE_REPORTS frontend=$FRONT_COVERAGE_REPORTS agent=$AGENT_COVERAGE_REPORTS."
  exit 1
fi
echo "Backend unit-test evidence status: ${BACK_STATUS}."
echo "Frontend unit-test evidence status: ${FRONT_STATUS}."
echo "Agent unit-test evidence status: ${AGENT_STATUS}."
echo "Product coverage reports: backend=${BACK_COVERAGE_REPORTS} frontend=${FRONT_COVERAGE_REPORTS} agent=${AGENT_COVERAGE_REPORTS}."
echo "Unit-test evidence status: ${UNIT_STATUS} (1 means failed or unavailable)."
echo "##aetheus[setvariable name=UNIT_TEST_GATE_STATUS]$UNIT_STATUS"
# The failed names beside the status, read from the three TRX the status is made of.
[ "$UNIT_STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh UNIT_TEST_FAILED_TESTS coverage/backend/backend.trx coverage/frontend/frontend.trx coverage/agent/agent.trx
echo "Promotion evidence recorded; the gate step decides what it adds up to."
