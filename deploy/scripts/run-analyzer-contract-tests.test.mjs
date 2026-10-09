// SPDX-License-Identifier: EUPL-1.2
// run-analyzer-contract-tests.sh turns the analyzer suite into an advisory status, 0 or 1. A test
// host that left no TRX used to be published as an empty status and "Valid TRX evidence recorded"
// (DEP-015). The script runs here for real with the real classifier; only dotnet is a stand-in that
// writes the TRX each case needs, or none.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";

function write(path, content, executable = false) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
  if (executable) chmodSync(path, 0o755);
}

const trx = (outcome, passed, failed) => `<?xml version="1.0" encoding="utf-8"?>
<TestRun><ResultSummary outcome="${outcome}"><Counters total="${passed + failed}" executed="${passed + failed}" passed="${passed}" failed="${failed}" error="0" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>
`;

function setUp(outcome) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-analyzers-"));
  const unix = path => path.replace(/\\/g, "/");
  for (const file of ["ci/run-analyzer-contract-tests.sh", "classify-dotnet-test-result.mjs"]) {
    cpSync(resolve("deploy/scripts", file), join(root, "deploy/scripts", file));
  }
  const dotnet = join(root, "bin", "dotnet");
  write(dotnet, `#!/bin/sh
dir=""; previous=""
for argument in "$@"; do [ "$previous" = "--results-directory" ] && dir="$argument"; previous="$argument"; done
case "$FAKE_OUTCOME" in
  passed) cat "$FAKE_TRX_DIR/passed.trx" > "$dir/analyzers.trx"; exit 0 ;;
  failed) cat "$FAKE_TRX_DIR/failed.trx" > "$dir/analyzers.trx"; exit 2 ;;
  *) echo "Test host crashed before writing any result." >&2; exit 7 ;;
esac
`, true);
  write(join(root, "trx", "passed.trx"), trx("Completed", 3, 0));
  write(join(root, "trx", "failed.trx"), trx("Failed", 2, 1));
  write(join(root, "deploy/scripts/ensure-dotnet-sdk.sh"), `echo "${unix(dotnet)}"\n`);
  write(join(root, "deploy/scripts/ensure-node-runtime.sh"), `echo "${unix(process.execPath)}"\n`);
  const run = () => spawnSync("sh", ["deploy/scripts/ci/run-analyzer-contract-tests.sh"], {
    cwd: root,
    encoding: "utf8",
    env: { ...process.env, WORKSPACE: unix(root), FAKE_OUTCOME: outcome, FAKE_TRX_DIR: unix(join(root, "trx")) }
  });
  return { run, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

test("a passing suite publishes status 0 with its TRX", () => {
  const env = setUp("passed");
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=ANALYZER_TEST_GATE_STATUS\]0\r?\n/);
    assert.match(result.stdout, /Analyzer test findings status: 0\. Valid TRX evidence recorded\./);
  } finally { env.cleanup(); }
});

test("failed assertions stay an advisory status 1, the step succeeds", () => {
  const env = setUp("failed");
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=ANALYZER_TEST_GATE_STATUS\]1\r?\n/);
  } finally { env.cleanup(); }
});

test("a test host that left no TRX fails the step and publishes no status", () => {
  const env = setUp("crashed");
  try {
    const result = env.run();
    assert.notEqual(result.status, 0);
    assert.doesNotMatch(result.stdout, /ANALYZER_TEST_GATE_STATUS/);
    assert.doesNotMatch(result.stdout, /Valid TRX evidence recorded/);
  } finally { env.cleanup(); }
});
