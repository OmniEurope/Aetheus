# SPDX-License-Identifier: EUPL-1.2
#
# Takes five real response-time samples off the deployed QA backend and turns them into an advisory
# gate status. It records every sample and refuses unless five were written, so a gate that passed
# because the loop never ran is not a possible outcome.
#
# This was 22 lines inlined in .pipeline/aetheus-qa.yaml. The body is relocated unchanged apart from
# the two variables noted below.
#
# Inputs, all read from the environment (none positional). The two marked `$(...)` were written that
# way in the YAML, which the control plane substitutes in a `shell:` block; inside a script that same
# text is a shell command substitution, so the caller passes them by name.
#   WORKSPACE                        the run's checkout, holding the .qa-test-results tree
#   QA_PORT_BACK                     the run-scoped backend port this QA stack listens on
#   AETHEUS_PERFORMANCE_MAX_SECONDS  the per-sample ceiling, in seconds
set -u
STATUS=0
LIMIT="${AETHEUS_PERFORMANCE_MAX_SECONDS}"
case "$LIMIT" in ''|*[!0-9.]*|.*|*.) echo "Invalid performance limit." >&2; exit 1 ;; esac
RESULTS="$WORKSPACE/.qa-test-results/performance-samples.txt"
: > "$RESULTS"
for INDEX in 1 2 3 4 5
do
  # A request that failed (refused, timed out, answered an HTTP error) is no sample: its time is how
  # fast it failed, and five fast refusals used to pass the gate (DEP-025). It fails the step instead.
  if ! ACTUAL="$(curl -fsS -o /dev/null -w '%{time_total}' \
    "http://127.0.0.1:${QA_PORT_BACK}/health/ready")"; then
    echo "Performance smoke ${INDEX}: the readiness request failed; no sample was taken." >&2
    exit 1
  fi
  case "$ACTUAL" in
    ''|*[!0-9.]*|.*|*.) echo "Performance smoke ${INDEX}: curl reported no usable time ('${ACTUAL}')." >&2; exit 1 ;;
  esac
  printf '%s\n' "$ACTUAL" >> "$RESULTS"
  if awk -v actual="$ACTUAL" -v limit="$LIMIT" 'BEGIN { exit !(actual > limit) }'
  then
    echo "Performance smoke ${INDEX} exceeded ${LIMIT}s (${ACTUAL}s)." >&2
    STATUS=1
  fi
done
test "$(wc -l < "$RESULTS" | tr -d ' ')" = 5 || {
  echo "Performance smoke recorded $(wc -l < "$RESULTS" | tr -d ' ') samples, not 5." >&2
  exit 1
}
printf '%s\n' "$STATUS" > "$WORKSPACE/.qa-performance-gate-status"
echo "##aetheus[setvariable name=QA_PERFORMANCE_GATE_STATUS]$STATUS"
echo "Performance findings status: $STATUS; five real samples recorded."
