#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Prints the application version: <major>.<minor>.<run number><suffix>.
#
# The major and the minor are the code's, so they live where the code is: the <VersionPrefix> of
# Directory.Build.props, the single place a version bump is made, reviewed and dated in the history.
# They used to be written three times - that file (1.1.0), a VERSION_PREFIX library entry (1.1) and a
# literal "1.1." in the CI packaging script - and nothing kept the three in step. The last number is the
# project's release number (BUILD_RELEASENUMBER, recette R2-110, 2026-10-04): Aetheus advances it by one at
# each candidate or release-fast run, the site and the agents of that release carry the same one, and the
# runs that make no release (nightly, demo) build with the latest number and their suffix. It replaced the
# global run id (R2-076), which advanced with every run of every pipeline. The suffix is the
# environment's (VERSION_SUFFIX: empty in production, "-nightly" for the demo), which is why it alone
# stays in a library.
#
# Inputs:
#   $1 or BUILD_RELEASENUMBER       the release number (digits only)
#   VERSION_SUFFIX                  optional, letters, digits, '.', '_' or '-'
#   WORKSPACE                       the checkout (defaults to the current directory)
set -eu

fail() {
  echo "FATAL: $*" >&2
  exit 1
}

RUN_NUMBER="${1:-${BUILD_RELEASENUMBER:-}}"
SUFFIX="${VERSION_SUFFIX:-}"
PROPS="${WORKSPACE:-$(pwd)}/Directory.Build.props"

[ -f "$PROPS" ] || fail "Directory.Build.props not found at $PROPS."
case "$RUN_NUMBER" in
  ''|*[!0-9]*) fail "The run number must be digits only, got '$RUN_NUMBER'." ;;
esac
case "$SUFFIX" in
  *[!0-9A-Za-z._-]*) fail "VERSION_SUFFIX contains unexpected characters: '$SUFFIX'." ;;
esac

# Exactly one <VersionPrefix>: a second one (a conditional override, say) would make "the" version
# depend on which of them this pattern happens to read.
COUNT="$(grep -c '<VersionPrefix>' "$PROPS" || true)"
[ "$COUNT" = "1" ] || fail "Directory.Build.props must declare exactly one <VersionPrefix>, found $COUNT."
MAJOR_MINOR="$(sed -n 's:.*<VersionPrefix>\([0-9][0-9]*\)\.\([0-9][0-9]*\)\(\.[0-9][0-9]*\)\{0,1\}</VersionPrefix>.*:\1.\2:p' "$PROPS")"
[ -n "$MAJOR_MINOR" ] || fail "The <VersionPrefix> of Directory.Build.props is not <major>.<minor>[.<patch>]."

printf '%s.%s%s\n' "$MAJOR_MINOR" "$RUN_NUMBER" "$SUFFIX"
