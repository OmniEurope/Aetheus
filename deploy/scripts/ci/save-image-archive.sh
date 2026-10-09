# SPDX-License-Identifier: EUPL-1.2
#
# Exports one Docker image as a gzip archive: `docker save <image> | gzip -1 > <archive>`, the exact
# command package-application.sh ran inline, with the one difference that a failed `docker save`
# fails here (DEP-014).
#
# Inline, the pipe returned gzip's status: a save that failed with nothing on stdout left a valid,
# empty gzip archive that passed `gzip -t` and `test -s`, and the delivery contract sealed its hash.
# POSIX sh has no portable pipefail, so the save's own status is carried out of the pipe in a file.
#
# Usage: save-image-archive.sh <image> <archive>
set -eu
IMAGE="${1:?Usage: save-image-archive.sh <image> <archive>}"
ARCHIVE="${2:?Usage: save-image-archive.sh <image> <archive>}"
SAVE_STATUS="$ARCHIVE.save-status"
rm -f "$SAVE_STATUS"
if ! { status=0; docker save "$IMAGE" || status=$?; echo "$status" > "$SAVE_STATUS"; } | gzip -1 > "$ARCHIVE"; then
  rm -f "$SAVE_STATUS" "$ARCHIVE"
  echo "Compressing the archive of $IMAGE failed." >&2
  exit 1
fi
SAVED="$(cat "$SAVE_STATUS" 2>/dev/null || echo missing)"
rm -f "$SAVE_STATUS"
if [ "$SAVED" != 0 ]; then
  rm -f "$ARCHIVE"
  echo "docker save $IMAGE failed (exit $SAVED); no archive is kept." >&2
  exit 1
fi
