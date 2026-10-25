// SPDX-License-Identifier: EUPL-1.2
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, normalize, relative, resolve } from "node:path";

const [contractPath, sourceCommitPath, minimumGrade, readinessMode] = process.argv.slice(2);
if (!contractPath || !sourceCommitPath || !minimumGrade) {
  throw new Error("Usage: verify-candidate-assurance-contract.mjs <contract> <source-commit> <minimum-grade> [--require-evidence|--require-deployment-readiness]");
}
if (readinessMode && readinessMode !== "--require-evidence"
    && readinessMode !== "--require-deployment-readiness") {
  throw new Error(`Unsupported assurance verification mode '${readinessMode}'.`);
}
const requireDeploymentReadiness = readinessMode === "--require-deployment-readiness";
const requireEvidence = readinessMode === "--require-evidence" || requireDeploymentReadiness;

const gradeRank = new Map(["A", "B", "C", "D", "E", "F"].map((grade, index) => [grade, index]));
if (!gradeRank.has(minimumGrade)) throw new Error(`Invalid deployment grade threshold '${minimumGrade}'.`);

let contract;
try {
  contract = JSON.parse(readFileSync(contractPath, "utf8"));
} catch (error) {
  throw new Error(`Assurance contract is missing or invalid: ${error.message}`, { cause: error });
}
const sourceSha = readFileSync(sourceCommitPath, "utf8").trim().toLowerCase();
if (!/^[0-9a-f]{40}$|^[0-9a-f]{64}$/.test(sourceSha)) throw new Error("Artifact source commit is invalid.");
// Accept schema 1 for retained releases because its unconditional V-1 suites provide the evidence
// schema 2 records under full qualification. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if (contract.schema !== 1 && contract.schema !== 2) throw new Error("Unsupported assurance contract schema.");
const profile = contract.schema === 1 ? "full" : contract.profile;
if (profile !== "light" && profile !== "full") {
  throw new Error(`Assurance contract qualification profile is missing or invalid ('${contract.profile}').`);
}
if (contract.sourceSha?.toLowerCase() !== sourceSha) throw new Error("Assurance contract source SHA does not match the candidate artifact.");
if (JSON.stringify(contract.gradingScale) !== JSON.stringify([...gradeRank.keys()])) throw new Error("Unsupported assurance grading scale.");

const seal = contract.seal;
if (seal?.algorithm !== "sha256" || !/^[0-9a-f]{64}$/.test(seal.digest ?? "")) {
  throw new Error("Assurance contract seal is missing or invalid.");
}
const contractWithoutSeal = { ...contract };
delete contractWithoutSeal.seal;
const actualSeal = createHash("sha256").update(JSON.stringify(contractWithoutSeal)).digest("hex");
if (actualSeal !== seal.digest) throw new Error("Assurance contract seal does not match its contents.");

const resume = contract.checkpointResume;
if (!resume || (resume.sourceRunId !== null
    && (!Number.isSafeInteger(resume.sourceRunId) || resume.sourceRunId <= 0))) {
  throw new Error("Checkpoint resume source identity is invalid.");
}
for (const [pipeline, runId] of Object.entries(resume.reusedRuns ?? {})) {
  if (!new Set(["ci", "quality", "security"]).has(pipeline)
      || !Number.isSafeInteger(runId) || runId <= 0
      || resume.sourceRunId === null) {
    throw new Error("Checkpoint resume provenance is invalid.");
  }
}

const contractRoot = resolve(dirname(contractPath));
const effectiveGrades = [];
for (const [name, analysis] of Object.entries(contract.analyses ?? {})) {
  if (analysis?.status !== "Available" && analysis?.status !== "Unavailable") throw new Error(`${name} grade status is invalid.`);
  const expectedGrade = analysis.status === "Available"
    && /^[A-F]$/.test(analysis.reportedGrade ?? "")
    && analysis.completeness === "Complete" ? analysis.reportedGrade : "F";
  if (analysis.effectiveGrade !== expectedGrade) throw new Error(`${name} effective grade is inconsistent.`);
  if (requireEvidence && analysis.status !== "Available") {
    const boundary = requireDeploymentReadiness ? "Deployment readiness" : "Candidate qualification";
    throw new Error(`${boundary} requires available ${name} evidence.`);
  }
  if (gradeRank.get(analysis.effectiveGrade) > gradeRank.get(minimumGrade)) {
    throw new Error(`${name} grade ${analysis.effectiveGrade} is below deployment threshold ${minimumGrade}.`);
  }
  if (analysis.pipelineRunId !== null
      && (!Number.isSafeInteger(analysis.pipelineRunId) || analysis.pipelineRunId <= 0)) {
    throw new Error(`${name} pipeline run identity is invalid.`);
  }
  if (analysis.status === "Available" && analysis.pipelineRunId === null) {
    throw new Error(`${name} available evidence requires a pipeline run identity.`);
  }
  if (analysis.sourceSha?.toLowerCase() !== sourceSha) throw new Error(`${name} evidence belongs to another commit.`);
  if (!/^[0-9a-f]{64}$/.test(analysis.sha256 ?? "")) throw new Error(`${name} evidence hash is invalid.`);
  if (typeof analysis.evidence !== "string" || analysis.evidence.length === 0) throw new Error(`${name} evidence path is invalid.`);

  const evidencePath = resolve(contractRoot, analysis.evidence);
  const pathFromRoot = relative(contractRoot, evidencePath);
  if (pathFromRoot.startsWith("..") || normalize(pathFromRoot) === "") throw new Error(`${name} evidence path escapes the artifact.`);
  const actualHash = createHash("sha256").update(readFileSync(evidencePath)).digest("hex");
  if (actualHash !== analysis.sha256) throw new Error(`${name} evidence hash does not match the contract.`);
  effectiveGrades.push(analysis.effectiveGrade);
}
// Light qualification carries only the three base analyses; full qualification may add both
// extension grades together so every included grade contributes to the overall verdict. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
const analysisKeys = Object.keys(contract.analyses ?? {}).sort().join(",");
const extendedAnalysisKeys = "dynamicSecurity,extendedDynamicSecurity,quality,security,securityHistory";
if (analysisKeys !== "dynamicSecurity,quality,security"
    && !(profile === "full" && analysisKeys === extendedAnalysisKeys)) {
  throw new Error("Assurance contract does not contain the three required analysis grades.");
}

const declaredTests = [
  "unit", "analyzers", "currentIntegration", "currentE2E",
  "agentCompatibility", "previousIntegration", "previousE2E", "performance",
  // Schema 2 only: the rollback proof both profiles pay for. Required in a light candidate exactly
  // as in a full one, so lightening the profile can never remove the proof that V-1 still works on
  // schema V - it only removes the depth of that proof.
  ...(contract.schema >= 2 ? ["previousSmoke"] : [])
];
for (const name of declaredTests) {
  const test = contract.tests?.[name];
  if (!test) throw new Error(`${name} test assurance is missing.`);
  if (name === "previousSmoke" && test.required !== true) {
    throw new Error("previousSmoke must be required in every qualification profile.");
  }
  if (test.required === true) {
    if (["agentCompatibility", "previousIntegration", "previousE2E", "previousSmoke"].includes(name)) {
      if (test.mode !== "NMinusOne" && test.mode !== "Bootstrap") {
        throw new Error(`${name} compatibility mode is missing or invalid.`);
      }
      if (test.mode === "Bootstrap" && contract.bootstrapAuthorized !== true) {
        throw new Error(`${name} used Bootstrap without explicit candidate authorization.`);
      }
      if (test.mode === "NMinusOne" && !/^[0-9a-f]{40}$|^[0-9a-f]{64}$/i.test(test.tested ?? "")) {
        throw new Error(`${name} N-1 tested revision is missing or invalid.`);
      }
    }
    if (!["Passed", "Failed", "Unavailable"].includes(test.status)) {
      throw new Error(`${name} required test assurance status is invalid.`);
    }
    const expectedGrade = test.status === "Passed" ? "A" : "F";
    if (test.effectiveGrade !== expectedGrade) throw new Error(`${name} effective grade is inconsistent.`);
    if (requireEvidence && test.status === "Unavailable") {
      const boundary = requireDeploymentReadiness ? "Deployment readiness" : "Candidate qualification";
      throw new Error(`${boundary} requires available ${name} evidence.`);
    }
    if (requireDeploymentReadiness && test.status !== "Passed") {
      throw new Error(`Deployment readiness requires ${name} to be Passed, not ${test.status}.`);
    }
    if (gradeRank.get(test.effectiveGrade) > gradeRank.get(minimumGrade)) {
      throw new Error(`${name} grade ${test.effectiveGrade} is below deployment threshold ${minimumGrade}.`);
    }
    effectiveGrades.push(test.effectiveGrade);
  }
  if (test.required === false && (test.status !== "NotRun" || test.effectiveGrade !== null)) {
    throw new Error(`${name} optional test assurance has an invalid status.`);
  }
  // A light candidate may leave exactly ONE test unrequired, and only for that declared reason.
  // previousIntegration and previousE2E were briefly on this list, which moved the rollback proof
  // off the commit being packaged; they are required in every profile again, and a contract that
  // claims the exemption for them is refused here rather than accepted quietly.
  if (test.required === false
      && test.reason === "nightly-profile"
      && (profile !== "light" || name !== "agentCompatibility")) {
    throw new Error(`${name} claims the nightly profile exemption outside a light qualification.`);
  }
}
if (profile === "light") {
  const agent = contract.tests?.agentCompatibility;
  if (agent?.required === false
      && agent.reason !== "nightly-profile"
      && agent.reason !== "not-applicable-to-project") {
    throw new Error("agentCompatibility is unrequired in a light candidate without a declared reason.");
  }
}

const overallGrade = effectiveGrades.reduce(
  (worst, grade) => gradeRank.get(grade) > gradeRank.get(worst) ? grade : worst,
  "A");
if (contract.overallGrade !== overallGrade) throw new Error("Assurance contract overall grade is inconsistent.");
if (gradeRank.get(overallGrade) > gradeRank.get(minimumGrade)) {
  throw new Error(`Candidate grade ${overallGrade} is below deployment threshold ${minimumGrade}.`);
}

if (requireDeploymentReadiness) {
  console.log(`Candidate deployment readiness verified with all required evidence Passed or Available at grade ${overallGrade}.`);
} else {
  console.log(`Candidate assurance grade ${overallGrade} verified at deployment threshold ${minimumGrade}.`);
}
