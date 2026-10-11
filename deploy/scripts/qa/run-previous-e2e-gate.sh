# SPDX-License-Identifier: EUPL-1.2
#
# Runs the retained V-1 end-to-end suite against the schema V produced, then proves the migration
# history did not move while it ran. A V-1 suite that quietly migrates the database would make the
# next stage compare V against a schema V-1 created, which is the failure this cmp exists to catch.
#
# In bootstrap mode there is no retained V-1, and it attests that against the recorded bootstrap
# schema proof rather than reporting a pass nothing produced.
#
# This was 25 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged.
#
# Inputs, all read from the environment (none positional):
#   BUILD_PROJECTNAME  the project name the QA stack, database and user are derived from
#   WORKSPACE      the run's checkout, holding the schema snapshots, .qa-bootstrap-mode,
#                  .nminus1-source, .qa-admin-pwd and .qa-previous-browser-commit
#   BUILD_BUILDID  the run id the disposable QA project names are derived from
set -u
. deploy/scripts/qa/qa-stack-identity.sh
STACK="$(qa_stack "$BUILD_BUILDID")"
STATUS=0
if [ "$(cat "$WORKSPACE/.qa-bootstrap-mode")" = 1 ]; then
  cmp "$WORKSPACE/.qa-schema-v" "$WORKSPACE/.qa-schema-after-bootstrap" || {
    echo "Bootstrap schema proof is missing or changed." >&2; exit 1;
  }
  echo "Bootstrap attestation: V E2E passed; V-1 E2E is inapplicable until this first release is retained."
else
  TEST_EXIT=0
  # A payload-only V-1 runs V's suite: tests of features newer than V-1 are then reported skipped.
  PREVIOUS_PAYLOAD_ONLY=false
  if [ "$(cat "$WORKSPACE/.qa-previous-payload-only")" = 1 ]; then PREVIOUS_PAYLOAD_ONLY=true; fi
  E2E_PREVIOUS_PAYLOAD_ONLY="$PREVIOUS_PAYLOAD_ONLY" sh deploy/scripts/run-qa-e2e-suite.sh "$WORKSPACE/.nminus1-source" \
    "${STACK}-front" "${STACK}-back" \
    "$WORKSPACE/.qa-admin-pwd" "qa-${BUILD_BUILDID}-previous" true \
    "aetheus-browser-smoke:$(cat "$WORKSPACE/.qa-previous-browser-commit")" \
    "$(cat "$WORKSPACE/.qa-previous-browser-commit")" || TEST_EXIT=$?
  case "$TEST_EXIT" in 0) ;; 10) STATUS=1 ;; *) exit "$TEST_EXIT" ;; esac
  docker exec "${STACK}-database" psql -U "$QA_DB_USER" -d "$QA_DB_NAME" -Atc \
    'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"' > "$WORKSPACE/.qa-schema-after-previous-e2e"
  cmp "$WORKSPACE/.qa-schema-v" "$WORKSPACE/.qa-schema-after-previous-e2e" || {
    echo "Database migration history changed during V-1 E2E." >&2; exit 1;
  }
fi
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-previous-e2e-gate-status"
echo "##aetheus[setvariable name=QA_PREVIOUS_E2E_GATE_STATUS]$STATUS"
[ "$STATUS" = 0 ] || sh deploy/scripts/publish-failed-tests.sh QA_PREVIOUS_E2E_FAILED_TESTS \
  "$WORKSPACE/.qa-test-results/qa-${BUILD_BUILDID}-previous/e2e/failed-tests.txt"
echo "Previous E2E findings status: $STATUS."
