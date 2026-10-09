// SPDX-License-Identifier: EUPL-1.2
// run-performance-smoke.sh turns five response times of the QA backend into an advisory status. A
// request that failed fast used to count as a fast sample, so a backend refusing every request passed
// the gate (DEP-025). The script runs here for real, with curl replaced by a stand-in that answers the
// way curl does: it prints the time even when it fails, and exits 7 (refused) or 22 (HTTP error).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const script = resolve("deploy/scripts/qa/run-performance-smoke.sh");

function setUp(answer) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-perf-"));
  const bin = join(root, "bin");
  mkdirSync(bin);
  mkdirSync(join(root, ".qa-test-results"));
  writeFileSync(join(bin, "curl"), `#!/bin/sh
case "$FAKE_ANSWER" in
  fast) printf '0.012' ;;
  slow) printf '9.500' ;;
  refused) printf '0.000'; echo "curl: (7) Failed to connect" >&2; exit 7 ;;
  unavailable) printf '0.003'; echo "curl: (22) The requested URL returned error: 503" >&2; exit 22 ;;
  empty) ;;
esac
`);
  chmodSync(join(bin, "curl"), 0o755);
  // The stand-in goes first on PATH from inside the shell: on Windows, Git's sh.exe launcher puts its
  // own /mingw64/bin, which holds the real curl, in front of a PATH handed to it from outside.
  const run = () => spawnSync("sh", [
    "-c", 'PATH="$(cd "$1" && pwd):$PATH"; export PATH; shift; exec sh "$@"', "sh", bin.replace(/\\/g, "/"), script
  ], {
    encoding: "utf8",
    env: {
      ...process.env,
      WORKSPACE: root.replace(/\\/g, "/"),
      QA_PORT_BACK: "18080",
      AETHEUS_PERFORMANCE_MAX_SECONDS: "2",
      FAKE_ANSWER: answer
    }
  });
  const status = () => {
    const path = join(root, ".qa-performance-gate-status");
    return existsSync(path) ? readFileSync(path, "utf8").trim() : null;
  };
  return { run, status, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

test("five fast answers pass the gate", () => {
  const env = setUp("fast");
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(env.status(), "0");
    assert.match(result.stdout, /Performance findings status: 0; five real samples recorded\./);
  } finally { env.cleanup(); }
});

test("slow answers are a finding, status 1, and the step succeeds", () => {
  const env = setUp("slow");
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(env.status(), "1");
  } finally { env.cleanup(); }
});

for (const answer of ["refused", "unavailable", "empty"]) {
  test(`a request that ${answer === "empty" ? "reported no time" : `was ${answer}`} fails the step and publishes no status`, () => {
    const env = setUp(answer);
    try {
      const result = env.run();
      assert.equal(result.status, 1);
      assert.equal(env.status(), null);
      assert.doesNotMatch(result.stdout, /QA_PERFORMANCE_GATE_STATUS/);
    } finally { env.cleanup(); }
  });
}
