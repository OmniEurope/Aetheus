#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Normalize timestamped coverlet.MTP output to the filename evidence readers expect, using separate
# timestamp directories to prevent collisions; leave missing-report classification to the caller.
# See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
set -eu

RESULTS="${1:?results directory is required}"
[ -d "$RESULTS" ] || { echo "Results directory not found: $RESULTS" >&2; exit 2; }

for REPORT in "$RESULTS"/coverage.cobertura.*.xml; do
  [ -f "$REPORT" ] || continue
  STAMP="${REPORT##*/coverage.cobertura.}"
  STAMP="${STAMP%.xml}"
  case "$STAMP" in *[!0-9]*|'') echo "Unexpected coverage report name: $REPORT" >&2; exit 2 ;; esac
  mkdir -p "$RESULTS/$STAMP"
  mv "$REPORT" "$RESULTS/$STAMP/coverage.cobertura.xml"
  echo "Coverage report: $RESULTS/$STAMP/coverage.cobertura.xml"
done
