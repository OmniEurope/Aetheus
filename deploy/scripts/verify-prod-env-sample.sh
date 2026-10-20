#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Compare template-rendered production settings with the deterministic reference so keys, order and
# values remain stable; run the real preparation block with a known-secret openssl stand-in in a
# temporary directory. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
set -eu

ROOT="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
PREPARE="$ROOT/deploy/scripts/prod-deploy-prepare.sh"
RENDER="$ROOT/deploy/scripts/render-env-sample.sh"
SAMPLE="$ROOT/deploy/env/prod.env.sample"
TEST_ROOT="$(mktemp -d)"
cleanup() { rm -rf -- "$TEST_ROOT"; }
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

# Deterministic openssl: call k of `openssl rand -hex N` prints 2N copies of hex digit k.
mkdir -p "$TEST_ROOT/bin"
cat > "$TEST_ROOT/bin/openssl" <<'EOF'
#!/bin/sh
set -eu
[ "$1" = rand ] && [ "$2" = -hex ] || { echo "unexpected openssl call: $*" >&2; exit 1; }
counter_file="$OPENSSL_SHIM_COUNTER"
count=$(( $(cat "$counter_file" 2>/dev/null || echo 0) + 1 ))
echo "$count" > "$counter_file"
digit="$(printf '%x' $(( count % 16 )))"
printf "%$(( $3 * 2 ))s\n" '' | tr ' ' "$digit"
EOF
chmod +x "$TEST_ROOT/bin/openssl"
PATH="$TEST_ROOT/bin:$PATH"
OPENSSL_SHIM_COUNTER="$TEST_ROOT/openssl-calls"
export PATH OPENSSL_SHIM_COUNTER

PUBLIC_API_URL="https://api.example.org"
PUBLIC_APP_URL="https://app.example.org"
APP_VERSION="1.1.1842"
export PUBLIC_API_URL PUBLIC_APP_URL APP_VERSION

# Reference environment with fixed secrets for a byte-for-byte rendering comparison. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
rm -f "$OPENSSL_SHIM_COUNTER"
cat > "$TEST_ROOT/expected.env" <<EOF
APPNAME=aetheus
ENV=prod
API_BASE_URL=${PUBLIC_API_URL:?PUBLIC_API_URL is required}
APP_VERSION=${APP_VERSION:?APP_VERSION is required}
DB_USER=aetheus
DB_PASSWORD=$(openssl rand -hex 24)
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=$(openssl rand -hex 16)
ENCRYPTION_KEY=$(openssl rand -hex 32)
ENCRYPTION_SALT=$(openssl rand -hex 16)
DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=$(openssl rand -hex 32)
FRONT_URL=${PUBLIC_APP_URL:?PUBLIC_APP_URL is required}
FRONT_URL_ACCEPT=${PUBLIC_APP_URL}
FRONT_URL_PROD=${PUBLIC_APP_URL}
EOF
[ "$(cat "$OPENSSL_SHIM_COUNTER")" = 6 ] || fail "the reference did not draw six secrets"

# --- The first-deploy block of prod-deploy-prepare.sh, run as extracted ---------------------------
BLOCK="$TEST_ROOT/first-deploy-block.sh"
sed -n '/^  ENV_FILE_TMP="\$ENV_FILE\.first-deploy\.\$\$"$/,/^  chmod 600 "\$ENV_FILE_TMP"$/p' "$PREPARE" > "$BLOCK"
grep -q 'render-env-sample.sh' "$BLOCK" || fail "the first-deploy block of $PREPARE no longer renders the template"
grep -q 'deploy/env/prod.env.sample' "$BLOCK" || fail "the first-deploy block of $PREPARE no longer names the template"
rm -f "$OPENSSL_SHIM_COUNTER"
(
  WORKSPACE="$ROOT"
  ENV_FILE="$TEST_ROOT/state/.env-prod"
  mkdir -p "$TEST_ROOT/state"
  umask 177
  # shellcheck disable=SC1090
  . "$BLOCK"
  mv "$ENV_FILE_TMP" "$ENV_FILE"
)
PRODUCED="$TEST_ROOT/state/.env-prod"
[ "$(cat "$OPENSSL_SHIM_COUNTER")" = 6 ] || fail "the template did not draw six secrets"
cmp "$TEST_ROOT/expected.env" "$PRODUCED" \
  || { diff "$TEST_ROOT/expected.env" "$PRODUCED" >&2 || true; fail "the rendered environment differs from the historical heredoc"; }
# Only where the filesystem keeps POSIX modes (not on a Windows checkout, where chmod is a no-op).
touch "$TEST_ROOT/mode-probe"
chmod 600 "$TEST_ROOT/mode-probe"
if [ "$(stat -c '%a' "$TEST_ROOT/mode-probe")" = 600 ]; then
  [ "$(stat -c '%a' "$PRODUCED")" = 600 ] || fail "the rendered environment is not mode 600"
else
  echo "SKIP: mode 600 not checked, this filesystem does not keep POSIX modes."
fi

# Require template and rendered keys to match in both membership and order. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
grep -v '^#' "$SAMPLE" | grep -v '^$' | cut -d= -f1 > "$TEST_ROOT/sample.keys"
cut -d= -f1 "$PRODUCED" > "$TEST_ROOT/produced.keys"
cmp -s "$TEST_ROOT/sample.keys" "$TEST_ROOT/produced.keys" || fail "template and produced keys differ"

# Every secret of the template is a marker, never a value: the repository holds no secret.
for secret in DB_PASSWORD JWT_KEY ADMIN_PASSWORD ENCRYPTION_KEY ENCRYPTION_SALT DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY; do
  grep -Eq "^$secret=#\{SECRET_HEX_[0-9]+\}#$" "$SAMPLE" || fail "$secret is not a secret marker in $SAMPLE"
done

# --- Refusals: each one fails with a stated reason (the caller then discards the partial output) ---
expect_refusal() {
  label="$1"
  shift
  if "$@" > "$TEST_ROOT/refused.env" 2> "$TEST_ROOT/refused.err"; then
    fail "accepted: $label"
  fi
  grep -q 'FATAL: ' "$TEST_ROOT/refused.err" || fail "no reason given: $label"
}
write_sample() { printf '%s\n' "$@" > "$TEST_ROOT/case.sample"; }

write_sample 'A=1' 'B=#{DB_PASSWORD}#'
expect_refusal "undeclared placeholder" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'A=1' 'A=2'
expect_refusal "duplicate key" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'A=#{SECRET_HEX_8}#'
expect_refusal "secret shorter than 16 bytes" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'A=#{SECRET_HEX_x}#'
expect_refusal "non-numeric secret length" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'NOT A KEY'
expect_refusal "line without =" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'lower=1'
expect_refusal "lower-case key" sh "$RENDER" "$TEST_ROOT/case.sample"
write_sample 'A='
expect_refusal "empty literal" sh "$RENDER" "$TEST_ROOT/case.sample"
printf 'A=1\r\n' > "$TEST_ROOT/case.sample"
expect_refusal "carriage return" sh "$RENDER" "$TEST_ROOT/case.sample"
expect_refusal "missing PUBLIC_API_URL" env -u PUBLIC_API_URL sh "$RENDER" "$SAMPLE"
expect_refusal "empty PUBLIC_APP_URL" env PUBLIC_APP_URL= sh "$RENDER" "$SAMPLE"
expect_refusal "missing template" sh "$RENDER" "$TEST_ROOT/does-not-exist.sample"

# A render that fails leaves no production environment behind.
(
  WORKSPACE="$TEST_ROOT/broken-workspace"
  mkdir -p "$WORKSPACE/deploy/env" "$WORKSPACE/deploy/scripts"
  cp "$RENDER" "$WORKSPACE/deploy/scripts/"
  printf '%s\n' 'A=#{UNKNOWN}#' > "$WORKSPACE/deploy/env/prod.env.sample"
  ENV_FILE="$TEST_ROOT/broken-state/.env-prod"
  mkdir -p "$TEST_ROOT/broken-state"
  fail() { echo "FATAL: $*" >&2; exit 1; }
  # shellcheck disable=SC1090
  . "$BLOCK"
) 2> "$TEST_ROOT/broken.err" && fail "the first-deploy block accepted a broken template"
[ -z "$(find "$TEST_ROOT/broken-state" -mindepth 1 -print -quit)" ] \
  || fail "a failed render left a file in the state directory"
grep -q "Could not render" "$TEST_ROOT/broken.err" || fail "the failed render gave no reason"

printf '%s\n' "PASS: deploy/env/prod.env.sample renders byte for byte the historical first-deploy environment (14 keys, 6 generated secrets)."
