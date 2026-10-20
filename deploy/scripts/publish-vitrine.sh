#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

DOMAIN="${1:-}"
WEB_ROOT="${2:-}"
SOURCE_DIR="${3:-site}"
case "$DOMAIN" in ""|*SITE_DOMAIN*) echo "SITE_DOMAIN not provided by the Variable Library"; exit 1;; esac
# Require a direct child of /var/www so publication stays within a bounded web root while its name
# comes from the library. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
case "$WEB_ROOT" in
  /var/www/*/*|*..*) echo "Refusing publication outside a dedicated /var/www child: '$WEB_ROOT'"; exit 1;;
  /var/www/?*) ;;
  *) echo "Refusing publication outside a dedicated /var/www child: '$WEB_ROOT'"; exit 1;;
esac

CANONICAL_WEB_ROOT="$(readlink -m -- "$WEB_ROOT")"
case "$CANONICAL_WEB_ROOT" in
  /var/www/*/*) echo "Refusing publication through an unsafe or escaping web root: '$WEB_ROOT'"; exit 1;;
  /var/www/?*) ;;
  *) echo "Refusing publication through an unsafe or escaping web root: '$WEB_ROOT'"; exit 1;;
esac
if [ -L "$WEB_ROOT" ]; then
  echo "Refusing publication through a symbolic-link web root: '$WEB_ROOT'"; exit 1
fi
WEB_ROOT="$CANONICAL_WEB_ROOT"

CANONICAL_SOURCE_DIR="$(readlink -m -- "$SOURCE_DIR")"
if [ ! -d "$CANONICAL_SOURCE_DIR" ] || [ -L "$SOURCE_DIR" ]; then
  echo "Refusing a missing or symbolic-link vitrine source: '$SOURCE_DIR'"; exit 1
fi
SOURCE_DIR="$CANONICAL_SOURCE_DIR"

# Every text file of the source is rewritten, not a fixed list of four: the documentation site has
# 22 pages across two languages, and a hard-coded list would silently leave placeholders in the
# pages it does not name. The guard below still refuses to publish if any placeholder survives.
find "$SOURCE_DIR" -type f \( -name '*.html' -o -name '*.xml' -o -name '*.txt' -o -name '*.json' \) \
  -exec sed -i "s#aetheus\.example#${DOMAIN}#g" {} +
if grep -R -n -- "aetheus\.example" "$SOURCE_DIR/"; then
  echo "Refusing to publish a vitrine containing unresolved aetheus.example placeholders." >&2
  exit 1
fi
echo "Remaining aetheus.example occurrences: 0"

mkdir -p "$WEB_ROOT"
if [ ! -w "$WEB_ROOT" ]; then
  echo "Web root $WEB_ROOT is not writable by the agent." >&2
  echo "Re-run the agent installer with the deployment module and --web-root $WEB_ROOT." >&2
  exit 1
fi

STAGE="$WEB_ROOT/.aetheus-next.$$"
BACKUP="$WEB_ROOT/.aetheus-previous.$$"
rm -rf "$STAGE" "$BACKUP"
mkdir "$STAGE" "$BACKUP"
cp -a "$SOURCE_DIR"/. "$STAGE"/
chmod -R a+rX "$STAGE"

SWITCHED=no
restore_vitrine() {
  [ "$SWITCHED" = yes ] || return 0
  find "$WEB_ROOT" -mindepth 1 -maxdepth 1 ! -name "$(basename "$BACKUP")" -exec rm -rf {} +
  find "$BACKUP" -mindepth 1 -maxdepth 1 -exec mv {} "$WEB_ROOT"/ \;
}
on_vitrine_exit() {
  rc=$?
  trap - EXIT
  if [ "$rc" -ne 0 ]; then restore_vitrine; fi
  rm -rf "$STAGE" "$BACKUP"
  exit "$rc"
}
trap on_vitrine_exit EXIT

find "$WEB_ROOT" -mindepth 1 -maxdepth 1 ! -name "$(basename "$STAGE")" ! -name "$(basename "$BACKUP")" -exec cp -a -t "$BACKUP" -- {} +
SWITCHED=yes
find "$WEB_ROOT" -mindepth 1 -maxdepth 1 ! -name "$(basename "$STAGE")" ! -name "$(basename "$BACKUP")" -exec rm -rf {} +
find "$STAGE" -mindepth 1 -maxdepth 1 -exec mv {} "$WEB_ROOT"/ \;
rm -rf "$STAGE" "$BACKUP"
SWITCHED=no
echo "Vitrine published to $WEB_ROOT (world-readable for Apache)."
