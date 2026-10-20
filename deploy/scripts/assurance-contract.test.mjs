// SPDX-License-Identifier: EUPL-1.2
// The qualification profile decides what a candidate is judged on, so it is the one place where a
// mistake silently lowers the bar. These tests pin both directions: a light candidate is excused
// from exactly three nightly-only tests and from nothing else, and it is still required to prove the
// rollback smoke and every analysis.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const directory = fileURLToPath(new URL(".", import.meta.url));
const generator = join(directory, "generate-candidate-assurance-contract.mjs");
const verifier = join(directory, "verify-candidate-assurance-contract.mjs");
const sourceSha = "a".repeat(40);
const previousSha = "b".repeat(40);

const passingSummary = (scope, runId) => ({
  pipelineRunId: runId,
  status: "Passed",
  grade: { overallGrade: "A", completeness: "Complete", commitHash: sourceSha, scope }
});

const build = root => {
  const quality = join(root, "quality");
  const security = join(root, "security");
  const qa = join(root, "qa");
  for (const [path, name, summary] of [
    [quality, "quality-summary.json", passingSummary("quality", 11)],
    [security, "security-summary.json", passingSummary("security", 12)],
    [qa, "security-summary.json", passingSummary("dynamic-security", 13)]
  ]) {
    mkdirSync(path, { recursive: true });
    writeFileSync(join(path, name), `${JSON.stringify(summary)}\n`);
  }
  const provenance = join(root, "artifact-provenance.json");
  writeFileSync(provenance, `${JSON.stringify({ sourceCommit: sourceSha, categories: {} })}\n`);
  const commitPath = join(root, "source-commit");
  writeFileSync(commitPath, `${sourceSha}\n`);
  return { quality, security, qa, provenance, commitPath };
};

const generate = (root, environment) => {
  const { quality, security, qa, provenance } = build(root);
  const output = join(root, "assurance-contract.json");
  const evidence = join(root, "assurance");
  const result = spawnSync(process.execPath, [
    generator, output, evidence, sourceSha, quality, security, qa, provenance
  ], {
    encoding: "utf8",
    env: {
      ...process.env,
      QA_PREVIOUS_SMOKE_GATE_STATUS: "0",
      UNIT_TEST_GATE_STATUS: "0",
      ANALYZER_TEST_GATE_STATUS: "0",
      QA_CURRENT_INTEGRATION_GATE_STATUS: "0",
      QA_CURRENT_E2E_GATE_STATUS: "0",
      QA_AGENT_COMPATIBILITY_GATE_STATUS: "0",
      QA_PREVIOUS_INTEGRATION_GATE_STATUS: "0",
      QA_PREVIOUS_E2E_GATE_STATUS: "0",
      COMPATIBILITY_MODE: "NMinusOne",
      PREVIOUS_VERSION_TESTED: previousSha,
      AgentCompatibilityMode: "NMinusOne",
      PreviousAgentTested: previousSha,
      AETHEUS_ASSURANCE_PERFORMANCE_ENABLED: "false",
      AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP: "false",
      AETHEUS_ASSURANCE_INAPPLICABLE_TESTS: "",
      ...environment
    }
  });
  return { result, output, root };
};

const verify = (contractPath, commitPath, grade, mode) =>
  spawnSync(process.execPath, [verifier, contractPath, commitPath, grade, mode].filter(Boolean), { encoding: "utf8" });

const reseal = (contractPath, mutate) => {
  const contract = JSON.parse(readFileSync(contractPath, "utf8"));
  mutate(contract);
  delete contract.seal;
  contract.seal = {
    algorithm: "sha256",
    digest: createHash("sha256").update(JSON.stringify(contract)).digest("hex")
  };
  writeFileSync(contractPath, `${JSON.stringify(contract, null, 2)}\n`);
};
const withRoot = body => {
  const root = mkdtempSync(join(tmpdir(), "assurance-"));
  try {
    return body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
};

test("a full candidate requires every compatibility test", () => withRoot(root => {
  const { result, output } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "full" });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.equal(contract.schema, 2);
  assert.equal(contract.profile, "full");
  for (const name of ["previousSmoke", "agentCompatibility", "previousIntegration", "previousE2E"]) {
    assert.equal(contract.tests[name].required, true, `${name} must be required in a full candidate`);
    assert.equal(contract.tests[name].status, "Passed");
  }
}));

test("a QA run still on the script's PascalCase attestation names is graded the same", () => withRoot(root => {
  const { result, output } = generate(root, {
    AETHEUS_ASSURANCE_PROFILE: "full",
    COMPATIBILITY_MODE: undefined,
    PREVIOUS_VERSION_TESTED: undefined,
    CompatibilityMode: "NMinusOne",
    PreviousVersionTested: previousSha
  });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  for (const name of ["previousSmoke", "previousIntegration", "previousE2E"]) {
    assert.equal(contract.tests[name].status, "Passed");
    assert.equal(contract.tests[name].mode, "NMinusOne");
    assert.equal(contract.tests[name].tested, previousSha);
  }
}));

test("the upper snake case attestation wins over the script's PascalCase one", () => withRoot(root => {
  const { result, output } = generate(root, {
    AETHEUS_ASSURANCE_PROFILE: "full",
    COMPATIBILITY_MODE: "Bootstrap",
    PREVIOUS_VERSION_TESTED: "false",
    CompatibilityMode: "NMinusOne",
    PreviousVersionTested: previousSha
  });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.equal(contract.tests.previousSmoke.mode, "Bootstrap");
  assert.equal(contract.tests.previousSmoke.tested, "false");
}));

test("a light candidate excuses the retained-agent contract and nothing else", () => withRoot(root => {
  const { result, output } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "light" });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.equal(contract.profile, "light");
  assert.equal(contract.tests.agentCompatibility.required, false);
  assert.equal(contract.tests.agentCompatibility.status, "NotRun");
  assert.equal(contract.tests.agentCompatibility.reason, "nightly-profile");
  assert.equal(contract.tests.agentCompatibility.effectiveGrade, null);
  // The rollback proof stays on the commit being packaged. Excusing these two moved it onto whatever
  // develop happened to be at 2 a.m., which is not what a deployment stands on.
  for (const name of [
    "unit", "analyzers", "currentIntegration", "currentE2E",
    "previousSmoke", "previousIntegration", "previousE2E"
  ]) {
    assert.equal(contract.tests[name].required, true, `${name} must stay required in a light candidate`);
  }
  for (const name of ["quality", "security", "dynamicSecurity"]) {
    assert.equal(contract.analyses[name].status, "Available", `${name} analysis must stay available`);
  }
}));

test("a light candidate whose V-1 suite failed is not deployable", () => withRoot(root => {
  const { result, output, root: base } = generate(root, {
    AETHEUS_ASSURANCE_PROFILE: "light",
    QA_PREVIOUS_E2E_GATE_STATUS: "1"
  });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.equal(contract.tests.previousE2E.status, "Failed");
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.notEqual(readiness.status, 0, "a failed V-1 E2E must refuse deployment readiness");
  assert.match(`${readiness.stderr}`, /previousE2E/);
}));

test("a light candidate whose rollback smoke failed is not deployable", () => withRoot(root => {
  const { result, output, root: base } = generate(root, {
    AETHEUS_ASSURANCE_PROFILE: "light",
    QA_PREVIOUS_SMOKE_GATE_STATUS: "1"
  });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.equal(contract.tests.previousSmoke.status, "Failed");
  assert.equal(contract.overallGrade, "F");
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.notEqual(readiness.status, 0, "a failed rollback smoke must refuse deployment readiness");
  assert.match(`${readiness.stderr}`, /previousSmoke/);
}));

test("a light candidate that ran everything else is deployment-ready", () => withRoot(root => {
  const { result, output, root: base } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "light" });
  assert.equal(result.status, 0, result.stderr);
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.equal(readiness.status, 0, readiness.stderr);
}));

test("the nightly exemption cannot be claimed by a full candidate", () => withRoot(root => {
  const { output, root: base } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "full" });
  reseal(output, contract => {
    contract.tests.previousE2E = {
      required: false, status: "NotRun", effectiveGrade: null, reason: "nightly-profile"
    };
  });
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.notEqual(readiness.status, 0, "a full candidate must not borrow the light exemption");
  assert.match(`${readiness.stderr}`, /nightly profile exemption/);
}));

test("even a light candidate cannot excuse the V-1 suites", () => withRoot(root => {
  // The exemption is narrow on purpose: excusing these is what moved the rollback proof off the
  // commit being packaged, so a contract that claims it is refused rather than accepted.
  const { output, root: base } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "light" });
  reseal(output, contract => {
    contract.tests.previousIntegration = {
      required: false, status: "NotRun", effectiveGrade: null, reason: "nightly-profile"
    };
  });
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.notEqual(readiness.status, 0);
  assert.match(`${readiness.stderr}`, /nightly profile exemption/);
}));

test("an unknown profile is refused rather than defaulted", () => withRoot(root => {
  const { result } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "quick" });
  assert.notEqual(result.status, 0);
  assert.match(`${result.stderr}`, /must be 'light' or 'full'/);
}));

test("a schema 1 contract is still verified as a full qualification", () => withRoot(root => {
  const { output, root: base } = generate(root, { AETHEUS_ASSURANCE_PROFILE: "full" });
  reseal(output, contract => {
    contract.schema = 1;
    delete contract.profile;
    delete contract.tests.previousSmoke;
  });
  const readiness = verify(output, join(base, "source-commit"), "E", "--require-deployment-readiness");
  assert.equal(readiness.status, 0, readiness.stderr);
}));

// Only full qualification carries the two nightly extension grades; both contribute to the overall
// grade. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
const generateNightly = (root, environment, historySummary = passingSummary("security-history", 14)) => {
  const history = join(root, "security-history");
  const extended = join(root, "qa-extended");
  mkdirSync(history, { recursive: true });
  mkdirSync(extended, { recursive: true });
  writeFileSync(join(history, "security-summary.json"), `${JSON.stringify(historySummary)}\n`);
  writeFileSync(join(extended, "security-summary.json"),
    `${JSON.stringify(passingSummary("extended-dynamic-security", 15))}\n`);
  const { quality, security, qa, provenance } = build(root);
  const output = join(root, "assurance-contract.json");
  const result = spawnSync(process.execPath, [
    generator, output, join(root, "assurance"), sourceSha, quality, security, qa, provenance, history, extended
  ], {
    encoding: "utf8",
    env: {
      ...process.env,
      QA_PREVIOUS_SMOKE_GATE_STATUS: "0",
      UNIT_TEST_GATE_STATUS: "0",
      ANALYZER_TEST_GATE_STATUS: "0",
      QA_CURRENT_INTEGRATION_GATE_STATUS: "0",
      QA_CURRENT_E2E_GATE_STATUS: "0",
      QA_AGENT_COMPATIBILITY_GATE_STATUS: "0",
      QA_PREVIOUS_INTEGRATION_GATE_STATUS: "0",
      QA_PREVIOUS_E2E_GATE_STATUS: "0",
      QA_PERFORMANCE_GATE_STATUS: "0",
      COMPATIBILITY_MODE: "NMinusOne",
      PREVIOUS_VERSION_TESTED: previousSha,
      AgentCompatibilityMode: "NMinusOne",
      PreviousAgentTested: previousSha,
      AETHEUS_ASSURANCE_PERFORMANCE_ENABLED: "true",
      AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP: "false",
      AETHEUS_ASSURANCE_INAPPLICABLE_TESTS: "",
      ...environment
    }
  });
  return { result, output, root };
};

test("a full nightly seals both extension analyses and verifies", () => withRoot(root => {
  const { result, output, root: base } = generateNightly(root, { AETHEUS_ASSURANCE_PROFILE: "full" });
  assert.equal(result.status, 0, result.stderr);
  const contract = JSON.parse(readFileSync(output, "utf8"));
  assert.deepEqual(Object.keys(contract.analyses).sort(),
    ["dynamicSecurity", "extendedDynamicSecurity", "quality", "security", "securityHistory"]);
  assert.equal(contract.analyses.securityHistory.status, "Available");
  assert.equal(contract.tests.performance.required, true);
  assert.equal(contract.tests.performance.status, "Passed");
  assert.equal(contract.tests.agentCompatibility.status, "Passed");
  const verified = verify(output, join(base, "source-commit"), "F", "--require-evidence");
  assert.equal(verified.status, 0, verified.stderr);
}));

test("an extension grade weighs in the nightly overall grade", () => withRoot(root => {
  const failing = { ...passingSummary("security-history", 14), grade: {
    overallGrade: "D", completeness: "Complete", commitHash: sourceSha, scope: "security-history" } };
  const { result, output } = generateNightly(root, { AETHEUS_ASSURANCE_PROFILE: "full" }, failing);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(readFileSync(output, "utf8")).overallGrade, "D");
}));

test("the extension analyses are refused in a light qualification", () => withRoot(root => {
  const { result } = generateNightly(root, { AETHEUS_ASSURANCE_PROFILE: "light" });
  assert.notEqual(result.status, 0);
  assert.match(`${result.stderr}`, /full qualification only/);
  const { output, root: base } = generate(join(root, "light"), { AETHEUS_ASSURANCE_PROFILE: "light" });
  reseal(output, contract => {
    contract.analyses.securityHistory = { ...contract.analyses.security };
    contract.analyses.extendedDynamicSecurity = { ...contract.analyses.dynamicSecurity };
  });
  const verified = verify(output, join(base, "source-commit"), "F", "--require-evidence");
  assert.notEqual(verified.status, 0);
  assert.match(`${verified.stderr}`, /three required analysis grades/);
}));
