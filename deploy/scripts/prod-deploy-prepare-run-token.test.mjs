// SPDX-License-Identifier: EUPL-1.2
// Exercise the real key back-fill block on a disposable environment file so the test follows
// production code; the openssl stand-in records arguments and supplies a known key. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const prepare = readFileSync(resolve("deploy/scripts/prod-deploy-prepare.sh"), "utf8");
const begin = prepare.indexOf("# --- GIT_RUN_TOKEN_KEY back-fill (R-464) ---");
const end = prepare.indexOf("# --- end of GIT_RUN_TOKEN_KEY back-fill ---");
const block = prepare.slice(begin, end);
const original = "APPNAME=aetheus\nENV=prod\nENCRYPTION_KEY=00aa11bb\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=cafe\n";
const firstKey = "a".repeat(64);
const secondKey = "b".repeat(64);

function prepareRoot(content) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-run-token-"));
  writeFileSync(join(root, ".env-prod"), content);
  return root;
}

function runBlock(root, opensslOutput) {
  const posix = root.replace(/\\/g, "/");
  const script = [
    "set -eu",
    'fail() { echo "FATAL: $*" >&2; exit 1; }',
    `openssl() { echo "$*" >> "${posix}/openssl-calls"; printf '%s\\n' '${opensslOutput}'; }`,
    `ENV_FILE="${posix}/.env-prod"`,
    `STATE_DIR="${posix}"`,
    'ENV_BACKUP_PREFIX="$(basename "$ENV_FILE")"',
    block,
  ].join("\n");
  return spawnSync("sh", ["-c", script], { encoding: "utf8" });
}

function snapshot(root) {
  const files = readdirSync(root);
  const calls = files.includes("openssl-calls") ? readFileSync(join(root, "openssl-calls"), "utf8") : "";
  return { env: readFileSync(join(root, ".env-prod"), "utf8"), files, calls };
}

test("the back-fill block is present in prod-deploy-prepare.sh", () => {
  assert.ok(begin > 0 && end > begin, "both marker comments must exist, in order");
  assert.match(block, /openssl rand -hex 32/);
});

test("an environment file without the key gets one, after a dated copy, every other line kept", () => {
  const root = prepareRoot(original);
  try {
    const result = runBlock(root, firstKey);
    const after = snapshot(root);

    assert.equal(result.status, 0, result.stderr);
    assert.equal(after.env, `${original}GIT_RUN_TOKEN_KEY=${firstKey}\n`);
    assert.equal(after.calls, "rand -hex 32\n");
    const backups = after.files.filter(name => /^\.env-prod-\d{8}T\d{6}Z$/.test(name));
    assert.equal(backups.length, 1);
    assert.equal(readFileSync(join(root, backups[0]), "utf8"), original);
    assert.deepEqual(after.files.filter(name => name.includes(".run-token.")), [], "no temporary file may remain");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the key is drawn once: the second prepare keeps it and draws nothing", () => {
  const root = prepareRoot(original);
  try {
    assert.equal(runBlock(root, firstKey).status, 0);
    const first = snapshot(root);

    const second = runBlock(root, secondKey);
    const after = snapshot(root);

    assert.equal(second.status, 0, second.stderr);
    assert.equal(after.env, first.env, "an existing key is never rotated");
    assert.equal(after.calls, "rand -hex 32\n", "openssl is not called again");
    assert.deepEqual(after.files.sort(), first.files.sort(), "no new copy when nothing changes");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an empty key line counts as absent and is replaced, not duplicated", () => {
  const root = prepareRoot(`APPNAME=aetheus\nGIT_RUN_TOKEN_KEY=\nENV=prod\n`);
  try {
    const result = runBlock(root, firstKey);

    assert.equal(result.status, 0, result.stderr);
    assert.equal(snapshot(root).env, `APPNAME=aetheus\nENV=prod\nGIT_RUN_TOKEN_KEY=${firstKey}\n`);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a drawn value that is not 32 hexadecimal bytes fails and leaves the file untouched", () => {
  for (const bad of ["", "zz".repeat(32), "ab".repeat(16)]) {
    const root = prepareRoot(original);
    try {
      const result = runBlock(root, bad);

      assert.equal(result.status, 1, `'${bad}' must be refused`);
      assert.match(result.stderr, /FATAL: openssl did not draw/);
      assert.equal(snapshot(root).env, original);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});
