# SPDX-License-Identifier: EUPL-1.2
#!/bin/sh
set -eu

if [ "$#" -ne 6 ]; then
  echo "Usage: zap-active-automation.sh <target> <report> <proxy-host> <proxy-port> <proxy-user> <proxy-password>" >&2
  exit 64
fi

TARGET_URL="$1"
REPORT_PATH="$2"
PROXY_HOST="$3"
PROXY_PORT="$4"
PROXY_USER="$5"
PROXY_PASSWORD="$6"
PLAN_PATH="/zap/wrk/aetheus-active-plan.yaml"

case "$TARGET_URL" in
  http://*|https://*) ;;
  *) echo "The active DAST target must be an absolute HTTP(S) URL." >&2; exit 64 ;;
esac
case "$TARGET_URL" in
  *"'"*) echo "The active DAST target contains an unsupported character." >&2; exit 64 ;;
esac
case "$PROXY_HOST" in
  *[!0-9a-fA-F.:]*) echo "The scanner proxy host is invalid." >&2; exit 64 ;;
esac
case "$PROXY_PORT" in
  ''|*[!0-9]*) echo "The scanner proxy port is invalid." >&2; exit 64 ;;
esac
case "$PROXY_USER" in
  ''|*[!a-zA-Z0-9_-]*) echo "The scanner proxy user is invalid." >&2; exit 64 ;;
esac
case "$PROXY_PASSWORD" in
  ''|*[!0-9a-f]*) echo "The scanner proxy password is invalid." >&2; exit 64 ;;
esac
[ "$REPORT_PATH" = "/zap/wrk/report.json" ] || {
  echo "The active DAST report path is invalid." >&2
  exit 64
}

trap 'rm -f -- "$PLAN_PATH"' EXIT HUP INT TERM
cat > "$PLAN_PATH" <<EOF
env:
  contexts:
    - name: aetheus-qa
      urls:
        - '$TARGET_URL'
  parameters:
    failOnError: true
    failOnWarning: true
    continueOnFailure: false
    progressToStdout: true
  proxy:
    hostname: '$PROXY_HOST'
    port: $PROXY_PORT
    realm: Aetheus
    username: '$PROXY_USER'
    password: '$PROXY_PASSWORD'
jobs:
  - type: spider
    parameters:
      context: aetheus-qa
      url: '$TARGET_URL'
      maxDuration: 2
      maxDepth: 5
      maxChildren: 20
      threadCount: 1
      parseGit: false
      parseDsStore: false
      parseSVNEntries: false
  - type: passiveScan-wait
    parameters:
      maxDuration: 2
  - type: activeScan
    parameters:
      context: aetheus-qa
      url: '$TARGET_URL'
      maxRuleDurationInMins: 1
      maxScanDurationInMins: 5
      threadPerHost: 1
      maxAlertsPerRule: 50
  - type: passiveScan-wait
    parameters:
      maxDuration: 2
    alwaysRun: true
  - type: report
    parameters:
      template: traditional-json
      reportDir: /zap/wrk
      reportFile: report.json
      reportTitle: Aetheus active DAST
      displayReport: false
    alwaysRun: true
EOF

/zap/zap.sh -cmd -autorun "$PLAN_PATH"
