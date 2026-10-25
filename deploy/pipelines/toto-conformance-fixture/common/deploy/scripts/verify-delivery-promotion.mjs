#!/usr/bin/env node
// SPDX-License-Identifier: EUPL-1.2
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { fingerprint, migrationsFromHistory } from "./schema-fingerprint.mjs";

// The fourth argument is the delivery contract of the release production currently runs, or "-" when
// none is known. It is optional so that a caller that never redeploys a previous release keeps working.
const [candidatePath, productionSchemaPath, artifactDirectory, liveContractPath = "-"] = process.argv.slice(2);
if (!candidatePath || !productionSchemaPath || !artifactDirectory)
  throw new Error(
    "Usage: verify-delivery-promotion.mjs <candidate-contract> <production-schema|-> <artifact-directory> "
    + "[<live-contract|->]");

const sha256 = value => createHash("sha256").update(value).digest("hex");
const canonicalize = value => {
  if (Array.isArray(value))
    return value.map(canonicalize);
  if (value && typeof value === "object")
    return Object.fromEntries(
      Object.entries(value)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, item]) => [key, canonicalize(item)]));
  return value;
};

// A contract is only worth what its sealed identity is worth: every field below is read after the
// identity hash has been recomputed over them.
const hasValidIdentity = contract => {
  const { candidateId, candidateVersion, ...identity } = contract;
  return candidateId === sha256(JSON.stringify(canonicalize(identity)));
};

const candidate = JSON.parse(await readFile(candidatePath));
// Schema 1 sealed the identity of the production release instead of the schema states it was proven
// against. Such a contract carries no fingerprints, so it cannot be checked under this gate at all;
// accepting it silently would mean promoting a delivery nothing has verified.
if (candidate.schema === 1)
  throw new Error(
    "This candidate was sealed under the old identity-based contract (schema 1) and carries no "
    + "schema fingerprints. Requalify it to obtain a schema-sealed contract.");
if (candidate.schema !== 2 || !candidate.candidateId)
  throw new Error("The candidate delivery contract is invalid.");
const { candidateId, candidateVersion } = candidate;
if (!hasValidIdentity(candidate))
  throw new Error("The candidate delivery contract identity is invalid.");
const expectedSourceVersion = `c-${candidate.sourceSha}`;
if (candidateVersion !== expectedSourceVersion
    && candidateVersion !== `${expectedSourceVersion}-${candidateId.slice(0, 12)}`)
  throw new Error("The candidate release version does not match its immutable identity.");

for (const [name, expected] of Object.entries(candidate.artifacts ?? {})) {
  const actual = sha256(await readFile(join(artifactDirectory, name)));
  if (actual !== expected)
    throw new Error(`Candidate artifact '${name}' differs from the validated digest.`);
}

// Accept only live schema states proven by this candidate or the current release's sealed rollback
// evidence so deployment never crosses an untested migration path (ADR-042). See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
const { baseline } = candidate;
const describe = state => `${state.hash} (${state.migrations.length} migrations)`;

// The previous-release branch. Returns the live release's version when every link holds, else null,
// so that a refusal still reads as the ordinary "neither state" diagnostic.
const previousReleaseOf = async live => {
  if (liveContractPath === "-" || !baseline?.schemaAfter?.hash)
    return null;
  const liveContract = JSON.parse(await readFile(liveContractPath));
  // A schema-1 live contract carries no schema states, so it proves no predecessor either way.
  if (liveContract.schema !== 2)
    return null;
  if (!liveContract.candidateId || !hasValidIdentity(liveContract))
    throw new Error("The delivery contract of the release production runs is invalid.");
  const liveBaseline = liveContract.baseline;
  if (liveBaseline?.bootstrap !== false)
    return null;
  // N's forward migration must be what separates the candidate's state from the live one...
  if (liveBaseline.schemaBefore?.hash !== baseline.schemaAfter.hash
      || liveBaseline.schemaAfter?.hash !== live.hash)
    return null;
  // ...which also means the candidate's migrations are an exact prefix of the live history: nothing
  // the candidate applied was later rewritten, only appended to.
  const prefix = baseline.schemaAfter.migrations;
  if (prefix.length >= live.migrations.length
      || prefix.some((migration, index) => live.migrations[index] !== migration))
    return null;
  return liveContract.candidateVersion ?? liveContract.candidateId;
};

if (baseline?.bootstrap === true) {
  // A first release has no prior state: either the database is empty, or it already carries exactly
  // what this delivery produces (a retried bootstrap).
  if (productionSchemaPath === "-")
    process.stdout.write("Bootstrap delivery onto a host with no database yet.\n");
  else {
    const live = fingerprint(migrationsFromHistory(await readFile(productionSchemaPath, "utf8")));
    if (live.migrations.length !== 0 && live.hash !== baseline.schemaAfter.hash)
      throw new Error(
        "A bootstrap candidate cannot replace an existing schema.\n"
        + `  live         : ${describe(live)}\n`
        + `  schemaAfter  : ${describe(baseline.schemaAfter)}`);
  }
} else {
  if (productionSchemaPath === "-")
    throw new Error("The production schema state required to verify this candidate is missing.");
  const live = fingerprint(migrationsFromHistory(await readFile(productionSchemaPath, "utf8")));
  let liveVersion;
  if (live.hash === baseline.schemaBefore.hash)
    process.stdout.write(
      `Production stands on the proven pre-migration schema ${describe(live)}; migrating deploy.\n`);
  else if (live.hash === baseline.schemaAfter.hash)
    process.stdout.write(
      `Production already stands on the proven post-migration schema ${describe(live)}; `
      + "the migration is a no-op and only the binaries move.\n");
  else if ((liveVersion = await previousReleaseOf(live)) !== null)
    process.stdout.write(
      `Previously deployed release: production runs ${liveVersion} on ${describe(live)}, whose QA `
      + `exercised the release before it on that schema from ${describe(baseline.schemaAfter)}; `
      + "the migration is a no-op and only the binaries move back.\n");
  else
    throw new Error(
      "The production schema is neither state this candidate was proven against.\n"
      + `  live         : ${describe(live)}\n`
      + `  schemaBefore : ${describe(baseline.schemaBefore)}\n`
      + `  schemaAfter  : ${describe(baseline.schemaAfter)}\n`
      + "Requalify the candidate against the current production schema.");
}

process.stdout.write(
  `Verified immutable candidate ${candidate.candidateVersion} (${candidate.candidateId}).\n`);
