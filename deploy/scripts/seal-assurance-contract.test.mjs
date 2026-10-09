// SPDX-License-Identifier: EUPL-1.2
// seal-assurance-contract.sh reports whether the candidate it sealed is deployable. That verdict is
// shown as a green "cleared for deployment" banner, so it must agree with what aetheus-deploy-prod
// will accept (DEP-013). The script runs here for real in a scratch Git checkout, with the contract
// generator and verifier replaced by stand-ins that write and accept the contract each case needs.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";

function write(path, content) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
}

function contract(overallGrade, tests) {
  return JSON.stringify({ overallGrade, tests });
}

function setUp(sealed) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-seal-"));
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith("GIT_")));
  const git = (...args) => {
    const result = spawnSync("git", ["-c", "user.email=tests@aetheus.invalid", "-c", "user.name=Aetheus Tests", ...args],
      { cwd: root, encoding: "utf8", env });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  cpSync(resolve("deploy/scripts/candidate/seal-assurance-contract.sh"),
    join(root, "deploy/scripts/candidate/seal-assurance-contract.sh"));
  write(join(root, "deploy/scripts/ensure-node-runtime.sh"), `echo "${process.execPath.replace(/\\/g, "/")}"\n`);
  // The generator writes the contract this case is about; the verifier accepts it (the threshold-F
  // verification is not what is under test here, the reported verdict is).
  write(join(root, "deploy/scripts/generate-candidate-assurance-contract.mjs"),
    `import { writeFileSync } from "node:fs";\nwriteFileSync(process.argv[2], ${JSON.stringify(sealed)});\n`);
  write(join(root, "deploy/scripts/verify-candidate-assurance-contract.mjs"), "\n");
  for (const name of ["delivery-contract.json", "artifact-provenance.json"]) {
    write(join(root, ".assurance-input/ci/.pipeline-artifacts", name), "{}\n");
  }
  git("init", "-q");
  git("add", "-A");
  git("commit", "-q", "-m", "fixture");
  const commit = git("rev-parse", "HEAD");
  const run = (extra = {}) => spawnSync("sh", ["deploy/scripts/candidate/seal-assurance-contract.sh"], {
    cwd: root,
    encoding: "utf8",
    env: {
      ...env,
      WORKSPACE: root.replace(/\\/g, "/"),
      BUILD_SOURCEVERSION: commit,
      AETHEUS_PROFILE: "light",
      AETHEUS_PERFORMANCE_ENABLED: "false",
      ALLOW_BOOTSTRAP: "false",
      ...extra
    }
  });
  return { run, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

const passed = { unit: { required: true, status: "Passed" }, performance: { required: false, status: "NotRun" } };

test("passed tests at a grade production accepts: deployable", () => {
  const env = setUp(contract("B", passed));
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=CANDIDATE_DEPLOYABLE\]true/);
    assert.match(result.stdout, /Candidate is deployable: assurance grade B\./);
  } finally { env.cleanup(); }
});

test("passed tests but an analysis graded F: not deployable, and the run stays green", () => {
  const env = setUp(contract("F", passed));
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=CANDIDATE_DEPLOYABLE\]false/);
    assert.match(result.stdout, /setvariable name=CANDIDATE_BLOCKING_TESTS\]\r?\n/);
    assert.doesNotMatch(result.stdout, /Candidate is deployable/);
    assert.match(result.stderr, /Assurance grade F is below the production minimum E/);
  } finally { env.cleanup(); }
});

test("a failed required test is named and makes the candidate not deployable", () => {
  const env = setUp(contract("F", { ...passed, currentE2E: { required: true, status: "Failed" } }));
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=CANDIDATE_DEPLOYABLE\]false/);
    assert.match(result.stdout, /setvariable name=CANDIDATE_BLOCKING_TESTS\]currentE2E=Failed/);
    assert.match(result.stderr, /Blocking required tests: currentE2E=Failed/);
  } finally { env.cleanup(); }
});

test("the production minimum can be read from AETHEUS_DEPLOY_MINIMUM_GRADE", () => {
  const env = setUp(contract("C", passed));
  try {
    const result = env.run({ AETHEUS_DEPLOY_MINIMUM_GRADE: "B" });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /setvariable name=CANDIDATE_DEPLOYABLE\]false/);
    assert.match(result.stderr, /Assurance grade C is below the production minimum B/);
  } finally { env.cleanup(); }
});
