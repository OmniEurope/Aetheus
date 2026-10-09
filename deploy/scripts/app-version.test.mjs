// SPDX-License-Identifier: EUPL-1.2
// app-version.sh is the one place the application version is composed: the code's major and minor,
// the run's counter, the environment's suffix. Observed on the real script, against a real
// Directory.Build.props and a fabricated one.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const script = resolve("deploy/scripts/app-version.sh");

function run(props, args = [], env = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-version-"));
  if (props !== null) writeFileSync(join(root, "Directory.Build.props"), props);
  const result = spawnSync("sh", [script, ...args], {
    encoding: "utf8",
    env: { ...process.env, BUILD_RELEASENUMBER: "", BUILD_PIPELINE_RUNNUMBER: "", VERSION_SUFFIX: "", WORKSPACE: root.replace(/\\/g, "/"), ...env }
  });
  rmSync(root, { recursive: true, force: true });
  return result;
}

const props = version => `<Project><PropertyGroup>\n    <VersionPrefix>${version}</VersionPrefix>\n  </PropertyGroup></Project>\n`;

test("the repository's own props give its major and minor", () => {
  const repository = readFileSync(resolve("Directory.Build.props"), "utf8");
  const declared = /<VersionPrefix>(\d+)\.(\d+)/.exec(repository);
  const result = run(repository, ["82"]);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), `${declared[1]}.${declared[2]}.82`);
});

test("the release number replaces the patch, the suffix follows it", () => {
  assert.equal(run(props("1.1.0"), [], { BUILD_RELEASENUMBER: "79" }).stdout.trim(), "1.1.79");
  assert.equal(run(props("2.3"), ["5"], { VERSION_SUFFIX: "-nightly" }).stdout.trim(), "2.3.5-nightly");
});

// Recette R2-110: the release number counts from 0, and the per-pipeline counter is no longer read.
test("the first release of a minor is .0, and the pipeline counter is not a release number", () => {
  assert.equal(run(props("1.3.0"), [], { BUILD_RELEASENUMBER: "0" }).stdout.trim(), "1.3.0");
  assert.equal(run(props("1.3.0"), [], { BUILD_RELEASENUMBER: "0", VERSION_SUFFIX: "-nightly" }).stdout.trim(), "1.3.0-nightly");
  assert.notEqual(run(props("1.3.0"), [], { BUILD_PIPELINE_RUNNUMBER: "79" }).status, 0);
});

test("a malformed input is refused, never turned into a version", () => {
  for (const [label, result] of [
    ["no run number", run(props("1.1.0"))],
    ["a run number that is not digits", run(props("1.1.0"), ["7a"])],
    ["a suffix with a space", run(props("1.1.0"), ["7"], { VERSION_SUFFIX: "-night ly" })],
    ["a prefix that is not numeric", run(props("one.two"), ["7"])],
    ["two prefixes", run(props("1.1.0") + props("2.0.0"), ["7"])],
    ["no props at all", run(null, ["7"])]
  ]) {
    assert.notEqual(result.status, 0, `${label} must fail`);
    assert.equal(result.stdout, "", `${label} must print no version`);
  }
});
