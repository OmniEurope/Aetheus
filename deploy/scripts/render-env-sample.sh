#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Render only the versioned environment template's declared placeholders so the repository owns the
# file's shape and the host owns its secrets; reject malformed or incomplete output before writing
# an environment. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
set -eu

fail() {
  echo "FATAL: $*" >&2
  exit 1
}

[ "$#" -eq 1 ] || { echo "Usage: render-env-sample.sh SAMPLE" >&2; exit 2; }
sample="$1"
[ -f "$sample" ] || fail "Environment template not found: $sample"

carriage_return="$(printf '\r')"
line_number=0
keys_seen=" "
while IFS= read -r line || [ -n "$line" ]; do
  line_number=$((line_number + 1))
  case "$line" in *"$carriage_return"*) fail "$sample:$line_number carries a carriage return." ;; esac
  case "$line" in ''|'#'*) continue ;; esac

  key="${line%%=*}"
  [ "$key" != "$line" ] || fail "$sample:$line_number is not KEY=VALUE."
  value="${line#*=}"
  case "$key" in
    [A-Z]*) ;;
    *) fail "$sample:$line_number: key '$key' must start with an upper-case letter." ;;
  esac
  case "$key" in *[!A-Z0-9_]*) fail "$sample:$line_number: key '$key' must be upper case, digits and underscores." ;; esac
  case "$keys_seen" in *" $key "*) fail "$sample:$line_number: key $key is declared twice." ;; esac
  keys_seen="$keys_seen$key "

  case "$value" in
    '#{PUBLIC_API_URL}#') resolved="${PUBLIC_API_URL:-}"; [ -n "$resolved" ] || fail "PUBLIC_API_URL is required" ;;
    '#{PUBLIC_APP_URL}#') resolved="${PUBLIC_APP_URL:-}"; [ -n "$resolved" ] || fail "PUBLIC_APP_URL is required" ;;
    '#{APP_VERSION}#') resolved="${APP_VERSION:-}"; [ -n "$resolved" ] || fail "APP_VERSION is required" ;;
    '#{SECRET_HEX_'*'}#')
      length="${value#'#{SECRET_HEX_'}"
      length="${length%'}#'}"
      case "$length" in ''|*[!0-9]*) fail "$sample:$line_number: secret length '$length' is not a number." ;; esac
      [ "$length" -ge 16 ] || fail "$sample:$line_number: a secret shorter than 16 bytes is refused."
      resolved="$(openssl rand -hex "$length")"
      [ "${#resolved}" -eq $((length * 2)) ] || fail "openssl produced a secret of the wrong length for $key."
      case "$resolved" in *[!0-9a-f]*) fail "openssl produced a non-hexadecimal secret for $key." ;; esac
      ;;
    *'#{'*|*'}#'*) fail "$sample:$line_number: undeclared placeholder in $key." ;;
    *) resolved="$value"; [ -n "$resolved" ] || fail "$sample:$line_number: $key has an empty value." ;;
  esac
  printf '%s=%s\n' "$key" "$resolved"
done < "$sample"

[ "$keys_seen" != " " ] || fail "Environment template declares no key: $sample"
