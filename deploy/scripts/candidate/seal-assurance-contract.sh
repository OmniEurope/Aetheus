# SPDX-License-Identifier: EUPL-1.2
#
# Seals the candidate's assurance contract: what was analysed, what was tested, at which profile, and
# the verdict that decides whether this package may be deployed at all. The contract is signed with a
# SHA-256 and read back at the deployment boundary, so this is the step that makes a candidate
# deployable or not.
#
# This was 56 lines inlined in .pipeline/aetheus-candidate.yaml. The body is relocated unchanged apart
# from the three parameter reads, noted below.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE                          the run's checkout (required, used through the working directory)
#   BUILD_SOURCEVERSION                the immutable revision this candidate packages
#   AETHEUS_PROFILE                    light or full; recorded in the contract and checked on reading.
#                                      It was written `${{ parameters.profile }}` in the YAML, which
#                                      the control plane expands before execution and a script cannot
#                                      read: inside a `.sh` that same text is a bad substitution and
#                                      aborts the step. The pipeline already publishes the parameter
#                                      under this name, so the value is read from it here.
#   AETHEUS_PERFORMANCE_ENABLED        whether performance qualification is required. Same trap: it
#                                      was `${{ parameters.performance }}`, and the pipeline already
#                                      publishes the parameter under this name.
#   ALLOW_BOOTSTRAP                    the run's allowBootstrap parameter. Same trap, but no pipeline
#                                      variable carries it, so the calling step passes it by name.
#   AETHEUS_RESUME_SOURCE_RUN_ID       the run a checkpoint resume reused, when there was one
#   AETHEUS_CHECKPOINT_*_RUN_ID        the CI, Quality and Security runs a resume reused
#   AETHEUS_DEPLOY_MINIMUM_GRADE       optional; the production minimum the reported verdict is
#                                      measured against, E (aetheus-deploy-prod's value) by default.
#                                      It changes the reported verdict, not the gate; an invalid value fails.
#
# Publishes CANDIDATE_ASSURANCE_GRADE, CANDIDATE_DEPLOYABLE and CANDIDATE_BLOCKING_TESTS.
set -eu
COMMIT="$(git rev-parse HEAD)"
test "$COMMIT" = "${BUILD_SOURCEVERSION:-}" || {
  echo "The candidate workspace does not match the candidate commit." >&2
  exit 1
}
mkdir -p .pipeline-artifacts
printf '%s\n' "$COMMIT" > .pipeline-artifacts/source-commit
# aetheus-deploy-prod restores this seal at a concrete candidate version, which is not one of the
# bootstrap selectors, so restore-artifacts demands the same three provenance files it demands of
# every other artifact. The seal wrote source-commit and nothing else, so that restore would be
# refused on "delivery-contract.json is missing or empty". The other two travel with the CI manifest
# already restored beside us, which the contract generated below reads from the same directory.
for provenance in delivery-contract.json artifact-provenance.json; do
  cp ".assurance-input/ci/.pipeline-artifacts/$provenance" ".pipeline-artifacts/$provenance"
done
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
# A full qualification is the nightly (PLAN-007 lot 2): it also seals the grades of its two
# extension pipelines, restored beside the others. A light candidate has no such directories and the
# generator refuses them outside `full`, so the profile alone decides and nothing is optional here.
set --
if [ "${AETHEUS_PROFILE}" = full ]; then
  set -- .assurance-input/security-history .assurance-input/qa-extended
fi
AETHEUS_ASSURANCE_PERFORMANCE_ENABLED="${AETHEUS_PERFORMANCE_ENABLED}" \
AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP="${ALLOW_BOOTSTRAP}" \
AETHEUS_ASSURANCE_PROFILE="${AETHEUS_PROFILE}" \
AETHEUS_RESUME_SOURCE_RUN_ID="${AETHEUS_RESUME_SOURCE_RUN_ID:-}" \
AETHEUS_CHECKPOINT_CI_RUN_ID="${AETHEUS_CHECKPOINT_CI_RUN_ID:-}" \
AETHEUS_CHECKPOINT_QUALITY_RUN_ID="${AETHEUS_CHECKPOINT_QUALITY_RUN_ID:-}" \
AETHEUS_CHECKPOINT_SECURITY_RUN_ID="${AETHEUS_CHECKPOINT_SECURITY_RUN_ID:-}" \
  "$NODE" deploy/scripts/generate-candidate-assurance-contract.mjs \
    .pipeline-artifacts/assurance-contract.json \
    .pipeline-artifacts/assurance \
    "$COMMIT" \
    .assurance-input/quality \
    .assurance-input/security \
    .assurance-input/qa \
    .assurance-input/ci/.pipeline-artifacts/artifact-provenance.json \
    "$@"
# Candidate qualifies, it does not deploy (the release below is published undeployed).
# --require-evidence keeps the honest part of the contract: every analysis and every
# required test must have produced real evidence, so a technical fault still fails here.
# A test that ran and failed is graded F and sealed into the contract instead of killing a
# two-hour qualification: the candidate records what the code is worth, it does not judge
# whether it may ship. The threshold therefore stays F here, deliberately permissive. The
# refusal belongs to the boundary that deploys: aetheus-deploy-prod runs this same
# verification with AETHEUS_DEPLOY_MINIMUM_GRADE=E and rejects an F candidate outright.
"$NODE" deploy/scripts/verify-candidate-assurance-contract.mjs \
  .pipeline-artifacts/assurance-contract.json \
  .pipeline-artifacts/source-commit \
  F \
  --require-evidence
# The permissive threshold above means a candidate carrying a failing required test is
# published green and only refused hours later, by aetheus-deploy-prod. Say it here, on
# the run that produced it, and name the tests responsible: the grade already decided the
# candidate cannot ship, so discovering that at deployment time is a reporting gap.
# "Deployable" also requires the overall grade to reach the production minimum: with every
# required test Passed, an analysis graded F still makes the overall grade F, which
# aetheus-deploy-prod refuses (DEP-013). The run stays green either way; only the verdict it
# reports is at stake. AETHEUS_DEPLOY_MINIMUM_GRADE defaults to aetheus-deploy-prod's own value.
"$NODE" -e '
  const contract = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  const minimum = process.argv[2];
  const scale = ["A", "B", "C", "D", "E", "F"];
  if (!scale.includes(minimum)) throw new Error(`Invalid production minimum grade: ${minimum}.`);
  const blocking = Object.entries(contract.tests ?? {})
    .filter(([, t]) => t.required === true && t.status !== "Passed")
    .map(([name, t]) => `${name}=${t.status}`);
  const gradeReached = scale.indexOf(contract.overallGrade) !== -1
    && scale.indexOf(contract.overallGrade) <= scale.indexOf(minimum);
  const deployable = blocking.length === 0 && gradeReached;
  console.log(`##aetheus[setvariable name=CANDIDATE_DEPLOYABLE]${deployable}`);
  console.log(`##aetheus[setvariable name=CANDIDATE_BLOCKING_TESTS]${blocking.join(",")}`);
  if (deployable) {
    console.log(`Candidate is deployable: assurance grade ${contract.overallGrade}.`);
  } else if (blocking.length > 0) {
    // stderr: the run stays green on purpose, so the one line that says the candidate
    // cannot ship has to stand out in the log instead of scrolling past as ordinary output.
    console.error(`Candidate is NOT deployable. Assurance grade ${contract.overallGrade}. Blocking required tests: ${blocking.join(", ")}. aetheus-deploy-prod will refuse this candidate.`);
  } else {
    console.error(`Candidate is NOT deployable. Assurance grade ${contract.overallGrade} is below the production minimum ${minimum}. aetheus-deploy-prod will refuse this candidate.`);
  }
' .pipeline-artifacts/assurance-contract.json "${AETHEUS_DEPLOY_MINIMUM_GRADE:-E}"
