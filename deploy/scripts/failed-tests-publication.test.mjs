// SPDX-License-Identifier: EUPL-1.2
// A gate status of 1 used to be all a suite published: the candidate's Gate tab read
// "currentIntegration=Failed" and the four failed tests of QA run 2638 were only in its 5253-line log.
// These cases run the real gate scripts, the real suite runner, classifier and list-failed-tests.mjs;
// only dotnet, the Node/.NET resolvers, the progress wrapper and the network-bound deployed suite are
// stand-ins.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";

const unix = path => path.replace(/\\/g, "/");

function write(path, content, executable = false) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
  if (executable) chmodSync(path, 0o755);
}

function trx(results) {
  const failed = results.filter(([, outcome]) => outcome === "Failed").length;
  const passed = results.length - failed;
  const rows = results.map(([name, outcome]) => `<UnitTestResult testName="${name}" outcome="${outcome}" />`).join("");
  return `<?xml version="1.0" encoding="utf-8"?>
<TestRun><Results>${rows}</Results><ResultSummary outcome="${failed ? "Failed" : "Completed"}"><Counters total="${results.length}" executed="${results.length}" passed="${passed}" failed="${failed}" error="0" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>
`;
}

function workspace(scripts) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-failed-tests-"));
  for (const file of ["list-failed-tests.mjs", "publish-failed-tests.sh", "classify-dotnet-test-result.mjs", ...scripts])
    cpSync(resolve("deploy/scripts", file), join(root, "deploy/scripts", file));
  const dotnet = join(root, "bin", "dotnet");
  // Writes the TRX the case asks for under --results-directory (both --report-trx-filename and the
  // analyzer's fixed name), then exits as Microsoft.Testing.Platform does on failed tests.
  write(dotnet, `#!/bin/sh
dir=""; name=""; previous=""
for argument in "$@"; do
  [ "$previous" = "--results-directory" ] && dir="$argument"
  [ "$previous" = "--report-trx-filename" ] && name="$argument"
  previous="$argument"
done
cat "$FAKE_TRX" > "$dir/$name"
grep -q 'outcome="Failed"' "$FAKE_TRX" && exit 2
exit 0
`, true);
  write(join(root, "deploy/scripts/ensure-dotnet-sdk.sh"), `echo "${unix(dotnet)}"\n`);
  write(join(root, "deploy/scripts/ensure-node-runtime.sh"), `echo "${unix(process.execPath)}"\n`);
  write(join(root, "deploy/scripts/run-with-progress.sh"), "shift\n\"$@\"\n");
  write(join(root, "deploy/scripts/rebase-static-web-assets.mjs"), "\n");
  return root;
}

function run(root, script, trxText, extraEnv = {}) {
  write(join(root, "fake.trx"), trxText);
  return spawnSync("sh", [script], {
    cwd: root,
    encoding: "utf8",
    env: { ...process.env, WORKSPACE: unix(root), QA_PORT_BACK: "5999", FAKE_TRX: unix(join(root, "fake.trx")), ...extraEnv }
  });
}

function integrationWorkspace() {
  const root = workspace(["run-qa-integration-suite.sh", "qa/run-current-integration-gate.sh"]);
  // The workspace is its own V source: the project the runner checks, and a runner and restore
  // state that take the Microsoft.Testing.Platform path without restoring.
  write(join(root, "tests/Aetheus.Back.IntegrationTests/Aetheus.Back.IntegrationTests.csproj"), "<Project />\n");
  write(join(root, "tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props"),
    "<NuGetPackageRoot Condition=\"x\">$(UserProfile)/.nuget/packages/</NuGetPackageRoot>\n");
  write(join(root, "global.json"), "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }\n");
  write(join(root, "deploy/scripts/run-qa-deployed-integration-suite.sh"), "exit 0\n");
  return root;
}

test("the current integration gate names the failed tests beside its status", () => {
  const root = integrationWorkspace();
  try {
    const result = run(root, "deploy/scripts/qa/run-current-integration-gate.sh", trx([
      ["Integration.Passes", "Passed"],
      ["Integration.LoginFails", "Failed"],
      ["Integration.RoleFails", "Failed"]
    ]));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=QA_CURRENT_INTEGRATION_GATE_STATUS\]1\r?\n/);
    assert.match(result.stdout, /Failed tests \(2\): Integration\.LoginFails, Integration\.RoleFails/);
    assert.match(result.stdout,
      /##aetheus\[setvariable name=QA_CURRENT_INTEGRATION_FAILED_TESTS\]Integration\.LoginFails, Integration\.RoleFails\r?\n/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("a clean current integration gate publishes no failed-test list", () => {
  const root = integrationWorkspace();
  try {
    const result = run(root, "deploy/scripts/qa/run-current-integration-gate.sh", trx([["Integration.Passes", "Passed"]]));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=QA_CURRENT_INTEGRATION_GATE_STATUS\]0\r?\n/);
    assert.doesNotMatch(result.stdout, /FAILED_TESTS|Failed tests/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("a failed deployed probe is named when the in-tree suite passed", () => {
  const root = integrationWorkspace();
  write(join(root, "deploy/scripts/run-qa-deployed-integration-suite.sh"),
    "mkdir -p \"$WORKSPACE/.qa-test-results/$3/deployed\"\n"
    + "printf '%s\\n' \"Deployed QA probes ($3)\" > \"$WORKSPACE/.qa-test-results/$3/deployed/failed-tests.txt\"\nexit 10\n");
  try {
    const result = run(root, "deploy/scripts/qa/run-current-integration-gate.sh", trx([["Integration.Passes", "Passed"]]));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=QA_CURRENT_INTEGRATION_FAILED_TESTS\]Deployed QA probes \(current\)\r?\n/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the analyzer suite names its failed tests beside ANALYZER_TEST_GATE_STATUS", () => {
  const root = workspace(["ci/run-analyzer-contract-tests.sh"]);
  try {
    const result = run(root, "deploy/scripts/ci/run-analyzer-contract-tests.sh",
      trx([["Analyzers.Prm001", "Failed"], ["Analyzers.Prm002", "Passed"]]));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=ANALYZER_TEST_GATE_STATUS\]1\r?\n/);
    assert.match(result.stdout, /##aetheus\[setvariable name=ANALYZER_TEST_FAILED_TESTS\]Analyzers\.Prm001\r?\n/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
