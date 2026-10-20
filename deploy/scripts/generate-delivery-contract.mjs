#!/usr/bin/env node
// SPDX-License-Identifier: EUPL-1.2
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { basename } from "node:path";
import { fingerprint, migrationsFromSource } from "./schema-fingerprint.mjs";

const [
  manifestPath,
  outputPath,
  adapterName,
  adapterVersion,
  candidatePrefix,
  sourceSha,
  migrationsDirectory,
  ...artifactPaths
] = process.argv.slice(2);

if (!manifestPath || !outputPath || !adapterName || !adapterVersion
    || !candidatePrefix || !/^[0-9a-f]{40}$/i.test(sourceSha ?? "")
    || !migrationsDirectory || artifactPaths.length === 0) {
  throw new Error(
    "Usage: generate-delivery-contract.mjs <manifest> <output> <adapter-name> " +
    "<adapter-version> <candidate-prefix> <source-sha> <migrations-directory> <artifact>...");
}

const sha256 = value => createHash("sha256").update(value).digest("hex");
const fileSha256 = async path => sha256(await readFile(path));
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

const manifestBytes = await readFile(manifestPath);
const manifest = JSON.parse(manifestBytes);
const packages = (manifest.packages ?? [])
  .filter(item => item.enabled === true)
  .map(item => {
    if (typeof item.id !== "string" || typeof item.version !== "string"
        || !/^[0-9a-f]{64}$/i.test(item.sha256 ?? ""))
      throw new Error("Every enabled integration requires id, exact version and sha256.");
    return {
      id: item.id,
      version: item.version,
      sha256: item.sha256.toLowerCase()
    };
  })
  .sort((left, right) => left.id.localeCompare(right.id));

const artifacts = {};
for (const path of artifactPaths)
  artifacts[basename(path)] = await fileSha256(path);

// Bind delivery to the schema states exercised by QA so a concurrent release with the same schema
// does not invalidate qualification. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
const bootstrap = process.env.DELIVERY_BASELINE_BOOTSTRAP === "true";
const baselineSchemaHash = process.env.DELIVERY_BASELINE_SCHEMA_HASH ?? "";
// Base64 because a setvariable directive carries one line: the list cannot travel raw.
const baselineSchemaMigrations =
  Buffer.from(process.env.DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64 ?? "", "base64")
    .toString("utf8")
    .split("\n").map(line => line.trim()).filter(line => line.length > 0);
if (!bootstrap && !/^[0-9a-f]{64}$/i.test(baselineSchemaHash))
  throw new Error("A non-bootstrap candidate requires the schema state of the production release.");
if (!bootstrap && fingerprint(baselineSchemaMigrations).hash !== baselineSchemaHash.toLowerCase())
  throw new Error("The production schema list does not match its declared fingerprint.");

// The schema state this delivery migrates to, derived from the migrations carried by this very
// source tree. QA re-reads both states from a live database and fails on any divergence, which is
// what turns this declaration into a proven fact rather than a claim.
// "-" is for a delivery with no database at all, where a schema state has nothing to describe. Any
// other value must yield migrations: an empty directory means they failed to reach the packaging
// step, and sealing an empty state there would silently let the gate match anything.
const schemaAfter = migrationsDirectory === "-"
  ? fingerprint([])
  : fingerprint(await migrationsFromSource(migrationsDirectory));
if (migrationsDirectory !== "-" && schemaAfter.migrations.length === 0)
  throw new Error(
    `No migrations were found in '${migrationsDirectory}'; the schema state cannot be sealed. `
    + "Pass '-' only for a delivery that has no database.");

const identity = canonicalize({
  schema: 2,
  sourceSha: sourceSha.toLowerCase(),
  adapter: {
    name: adapterName,
    version: adapterVersion
  },
  baseline: bootstrap
    ? { bootstrap: true, schemaAfter }
    : {
        bootstrap: false,
        schemaBefore: fingerprint(baselineSchemaMigrations),
        schemaAfter
      },
  injection: {
    manifestSha256: sha256(manifestBytes),
    packages
  },
  artifacts
});
const candidateId = sha256(JSON.stringify(identity));
const candidateVersion = process.env.DELIVERY_CANDIDATE_VERSION_MODE === "source"
  ? candidatePrefix
  : `${candidatePrefix}-${candidateId.slice(0, 12)}`;
await writeFile(
  outputPath,
  `${JSON.stringify(canonicalize({ ...identity, candidateId, candidateVersion }), null, 2)}\n`,
  { mode: 0o644 });
process.stdout.write(`##aetheus[setvariable name=CANDIDATE_ID]${candidateId}\n`);
process.stdout.write(`##aetheus[setvariable name=CANDIDATE_VERSION]${candidateVersion}\n`);
