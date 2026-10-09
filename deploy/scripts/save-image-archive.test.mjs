// SPDX-License-Identifier: EUPL-1.2
// save-image-archive.sh exports the backend and frontend images the delivery contract seals. A failed
// `docker save` used to leave an empty but valid gzip archive behind (DEP-014). The script runs here
// for real, with docker replaced by a stand-in that either streams a payload or fails with nothing.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { gunzipSync } from "node:zlib";
import test from "node:test";

const script = resolve("deploy/scripts/ci/save-image-archive.sh");

function setUp() {
  const root = mkdtempSync(join(tmpdir(), "aetheus-save-"));
  const bin = join(root, "bin");
  mkdirSync(bin);
  writeFileSync(join(bin, "docker"), `#!/bin/sh
[ "$1" = save ] || exit 64
[ "$FAKE_SAVE" = ok ] || { echo "Error response from daemon: no such image: $2" >&2; exit 1; }
printf 'image-tar-of-%s' "$2"
`);
  chmodSync(join(bin, "docker"), 0o755);
  const archive = join(root, "aetheus-back.tar.gz");
  // The stand-in goes first on PATH from inside the shell: on Windows, Git's sh.exe launcher puts its
  // own directories in front of a PATH handed to it from outside.
  const run = save => spawnSync("sh", [
    "-c", 'PATH="$(cd "$1" && pwd):$PATH"; export PATH; shift; exec sh "$@"', "sh", bin.replace(/\\/g, "/"),
    script, "aetheus-back:abc", archive.replace(/\\/g, "/")
  ], { encoding: "utf8", env: { ...process.env, FAKE_SAVE: save } });
  return { root, archive, run, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

test("a successful save is written as the gzip of exactly what docker streamed", () => {
  const env = setUp();
  try {
    const result = env.run("ok");
    assert.equal(result.status, 0, result.stderr);
    assert.equal(gunzipSync(readFileSync(env.archive)).toString(), "image-tar-of-aetheus-back:abc");
    assert.equal(existsSync(`${env.archive}.save-status`), false);
  } finally { env.cleanup(); }
});

test("a failed save fails the export and keeps no archive", () => {
  const env = setUp();
  try {
    const result = env.run("fail");
    assert.equal(result.status, 1);
    assert.match(result.stderr, /docker save aetheus-back:abc failed \(exit 1\)/);
    assert.equal(existsSync(env.archive), false);
    assert.equal(existsSync(`${env.archive}.save-status`), false);
  } finally { env.cleanup(); }
});
