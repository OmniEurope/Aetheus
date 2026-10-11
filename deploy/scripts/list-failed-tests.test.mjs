// SPDX-License-Identifier: EUPL-1.2
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  MaxNames, MaxValueLength, collectFailedTests, failedTestNames, formatFailedTests, sanitizeTestName
} from "./list-failed-tests.mjs";

const script = join(dirname(fileURLToPath(import.meta.url)), "list-failed-tests.mjs");

function trx(results) {
  const rows = results.map(([name, outcome]) =>
    `<UnitTestResult executionId="x" testId="y" testName="${name}" computerName="qa" outcome="${outcome}" duration="00:00:01" />`);
  return `<?xml version="1.0" encoding="utf-8"?><TestRun><Results>${rows.join("")}</Results>`
    + `<ResultSummary outcome="Failed"><Counters total="${results.length}" executed="${results.length}" /></ResultSummary></TestRun>`;
}

function file(name, text) {
  const path = join(mkdtempSync(join(tmpdir(), "aetheus-failed-tests-")), name);
  writeFileSync(path, text);
  return path;
}

function run(...args) {
  return spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });
}

test("lists the failed, errored and timed-out tests of a TRX, in order and once each", () => {
  const names = failedTestNames(trx([
    ["Suite.Passes", "Passed"],
    ["Suite.Fails", "Failed"],
    ["Suite.Skipped", "NotExecuted"],
    ["Suite.Errors", "Error"],
    ["Suite.Fails", "Failed"],
    ["Suite.TimesOut", "Timeout"]
  ]));
  assert.deepEqual(names, ["Suite.Fails", "Suite.Errors", "Suite.TimesOut"]);
});

test("decodes XML entities and keeps a parameterized name on one comma-free line", () => {
  const names = failedTestNames(trx([["Suite.Case(a: &quot;x, y&quot;, b: &lt;1&gt;)&#xA;next", "Failed"]]));
  assert.deepEqual(names, ["Suite.Case(a: \"x; y\"; b: <1>) next"]);
});

test("neutralises shell-active characters", () => {
  assert.equal(sanitizeTestName("Run $(rm -rf) `x`"), "Run _(rm -rf) _x_");
});

test("caps the value to the first names with a +N more tail", () => {
  const names = Array.from({ length: MaxNames + 7 }, (_, index) => `Suite.Test${index}`);
  const value = formatFailedTests(names);
  assert.ok(value.startsWith("Suite.Test0, Suite.Test1"));
  assert.ok(value.endsWith(", +7 more"));
  assert.equal(value.split(", ").length, MaxNames + 1);
});

test("caps the value length even when the names are long", () => {
  const names = Array.from({ length: 15 }, (_, index) => `${"N".repeat(190)}${index}`);
  const value = formatFailedTests(names);
  assert.ok(value.length <= MaxValueLength, `length ${value.length}`);
  assert.match(value, /\+\d+ more$/);
});

test("merges TRX and list sources and skips an absent one", () => {
  const source = file("integration.trx", trx([["A.One", "Failed"], ["A.Two", "Passed"]]));
  const list = file("failed-tests.txt", "Deployed QA probes (current)\nA.One\n");
  assert.deepEqual(collectFailedTests([source, list, join(tmpdir(), "absent-failed-tests.txt")]),
    ["A.One", "Deployed QA probes (current)"]);
});

test("record writes the list and prints the log line", () => {
  const source = file("e2e.trx", trx([["E2E.Login", "Failed"], ["E2E.Home", "Passed"]]));
  const list = join(dirname(source), "failed-tests.txt");
  const result = run("record", source, list);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /^Failed tests \(1\): E2E\.Login\r?\n$/);
  assert.equal(readFileSync(list, "utf8"), "E2E.Login\n");
});

test("record of a clean TRX writes an empty list and prints nothing", () => {
  const source = file("clean.trx", trx([["E2E.Home", "Passed"]]));
  const list = join(dirname(source), "failed-tests.txt");
  const result = run("record", source, list);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, "");
  assert.equal(readFileSync(list, "utf8"), "");
});

test("publish emits a single-line setvariable next to the log line", () => {
  const source = file("integration.trx", trx([["A.One", "Failed"], ["A.Two", "Failed"]]));
  const result = run("publish", "QA_CURRENT_INTEGRATION_FAILED_TESTS", source);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Failed tests \(2\): A\.One, A\.Two\r?\n/);
  assert.match(result.stdout, /##aetheus\[setvariable name=QA_CURRENT_INTEGRATION_FAILED_TESTS\]A\.One, A\.Two\r?\n/);
});

test("publish says nothing when no test failed", () => {
  const source = file("integration.trx", trx([["A.One", "Passed"]]));
  const result = run("publish", "QA_CURRENT_INTEGRATION_FAILED_TESTS", source);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, "");
});

test("publish refuses a variable name the backend would not parse", () => {
  const result = run("publish", "bad-name", file("x.txt", "A\n"));
  assert.notEqual(result.status, 0);
});
