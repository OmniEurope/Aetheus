#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Prove that the deployed QA binary can authenticate and read through the migrated QA database.
set -eu

BACKEND_URL="${1:?backend URL is required}"
ADMIN_PASSWORD_FILE="${2:?admin password file is required}"
RESULT_LABEL="${3:?result label is required}"
WORKSPACE="${WORKSPACE:?WORKSPACE is required}"

case "$BACKEND_URL" in
  http://127.0.0.1:*) PORT="${BACKEND_URL#http://127.0.0.1:}" ;;
  http://localhost:*) PORT="${BACKEND_URL#http://localhost:}" ;;
  *) echo "QA backend URL must be loopback HTTP." >&2; exit 2 ;;
esac
case "$PORT" in ''|*[!0-9]*) echo "QA backend URL must contain only a numeric port." >&2; exit 2 ;; esac
case "$RESULT_LABEL" in *[!a-zA-Z0-9_-]*|'') echo "Invalid deployed integration result label." >&2; exit 2 ;; esac
test -s "$ADMIN_PASSWORD_FILE"
command -v curl >/dev/null
command -v sed >/dev/null

RESULTS_DIR="$WORKSPACE/.qa-test-results/$RESULT_LABEL/deployed"
LOGIN_RESPONSE="$RESULTS_DIR/login.json"
PERMISSIONS_RESPONSE="$RESULTS_DIR/permissions.json"
PROJECTS_RESPONSE="$RESULTS_DIR/projects.json"
rm -rf "$RESULTS_DIR"
mkdir -p "$RESULTS_DIR"

curl -fsS "$BACKEND_URL/health/live" >/dev/null
curl -fsS "$BACKEND_URL/health/ready" >/dev/null

PROBE_STATUS=0
(
  set -eu
  ADMIN_PASSWORD="$(cat "$ADMIN_PASSWORD_FILE")"
  printf '{"username":"admin","password":"%s"}' "$ADMIN_PASSWORD" |
    curl -fsS -H 'Content-Type: application/json' --data-binary @- \
      "$BACKEND_URL/api/auth/login" > "$LOGIN_RESPONSE"
  TOKEN="$(sed -n 's/.*"token":"\([^"]*\)".*/\1/p' "$LOGIN_RESPONSE")"
  test -n "$TOKEN" || { echo "QA login did not return a bearer token." >&2; exit 1; }

  curl -fsS -H "Authorization: Bearer $TOKEN" \
    "$BACKEND_URL/api/users/me/permissions" > "$PERMISSIONS_RESPONSE"
  curl -fsS -H "Authorization: Bearer $TOKEN" \
    "$BACKEND_URL/api/projects?page=1&pageSize=1" > "$PROJECTS_RESPONSE"

  grep -Eq '"username"[[:space:]]*:[[:space:]]*"admin"' "$PERMISSIONS_RESPONSE"
  grep -Eq '"roles"[[:space:]]*:[[:space:]]*\[[^]]*"Admin"' "$PERMISSIONS_RESPONSE"
  grep -Eq '"items"[[:space:]]*:[[:space:]]*\[[[:space:]]*\{' "$PROJECTS_RESPONSE"
) || PROBE_STATUS=$?

NODE="$(sh "$WORKSPACE/deploy/scripts/ensure-node-runtime.sh")"
"$NODE" - "$RESULTS_DIR/deployed-proof.json" "$RESULT_LABEL" "$PROBE_STATUS" <<'NODE'
const fs = require("node:fs");
const [output, label, status] = process.argv.slice(2);
fs.writeFileSync(output, `${JSON.stringify({
  schema: 1,
  suite: label,
  executedProbes: 5,
  status: status === "0" ? "Passed" : "Failed"
})}\n`);
NODE
if [ "$PROBE_STATUS" = 0 ]; then
  echo "Deployed QA integration $RESULT_LABEL passed 5/5 probes against the migrated stack."
else
  echo "Deployed QA integration $RESULT_LABEL recorded failed application probes." >&2
  # No TRX here: the probes are named as one failed entry, for the gate script to publish.
  printf '%s\n' "Deployed QA probes ($RESULT_LABEL)" > "$RESULTS_DIR/failed-tests.txt"
  exit 10
fi
