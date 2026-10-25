// SPDX-License-Identifier: EUPL-1.2
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync
} from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { fileURLToPath } from "node:url";

const directory = fileURLToPath(new URL("../../../deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/", import.meta.url));
const generator = join(directory, "generate-delivery-contract.mjs");
const verifier = join(directory, "verify-delivery-promotion.mjs");
const sha256 = value => createHash("sha256").update(value).digest("hex");

const generate = ({
  root,
  source,
  manifest,
  artifact = "application.tar",
  baseline = { bootstrap: true },
  migrations = ["20260101000000_InitialCreate"],
  mode = "identity"
}) => {
  const manifestPath = join(root, `manifest-${source[0]}.json`);
  const artifactPath = join(root, artifact);
  const outputPath = join(root, `contract-${source[0]}-${Date.now()}-${Math.random()}.json`);
  writeFileSync(manifestPath, `${JSON.stringify(manifest)}\n`);
  if (!readFileIfPresent(artifactPath))
    writeFileSync(artifactPath, `artifact-${source}\n`);
  // The contract now seals schema states, so the generator needs a migrations directory to derive
  // schemaAfter from, and the state production stands on to inherit as schemaBefore.
  const migrationsDirectory = join(root, `migrations-${source[0]}`);
  mkdirSync(migrationsDirectory, { recursive: true });
  for (const id of migrations)
    writeFileSync(join(migrationsDirectory, `${id}.cs`), "// migration\n");
  const environment = {
    ...process.env,
    DELIVERY_CANDIDATE_VERSION_MODE: mode,
    DELIVERY_BASELINE_BOOTSTRAP: String(baseline.bootstrap)
  };
  if (!baseline.bootstrap) {
    const list = baseline.schemaBefore ?? [];
    environment.DELIVERY_BASELINE_SCHEMA_HASH = sha256(list.join("\n"));
    environment.DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64 =
      Buffer.from(list.join("\n"), "utf8").toString("base64");
  }
  const result = spawnSync(
    process.execPath,
    [
      generator,
      manifestPath,
      outputPath,
      "test-adapter",
      "1",
      `c-${source}`,
      source,
      migrationsDirectory,
      artifactPath
    ],
    { env: environment, encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /CANDIDATE_ID/);
  assert.match(result.stdout, /CANDIDATE_VERSION/);
  return { path: outputPath, contract: JSON.parse(readFileSync(outputPath, "utf8")) };
};

const readFileIfPresent = path => {
  try {
    return readFileSync(path);
  } catch (error) {
    if (error.code === "ENOENT")
      return null;
    throw error;
  }
};

test("bootstrap contract verifies exact candidate bytes", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-contract-"));
  try {
    const source = "a".repeat(40);
    const generated = generate({
      root,
      source,
      manifest: { schema: 1, packages: [] }
    });
    const result = spawnSync(
      process.execPath,
      [verifier, generated.path, "-", root],
      { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Verified immutable candidate/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("same source with a different injection manifest creates a different candidate", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-contract-"));
  try {
    const source = "b".repeat(40);
    const first = generate({
      root,
      source,
      manifest: { schema: 1, packages: [] },
      artifact: "first.tar"
    }).contract;
    const second = generate({
      root,
      source,
      manifest: {
        schema: 1,
        packages: [
          {
            id: "Aetheus.Telemetry",
            version: "1.2.3",
            enabled: true,
            sha256: "c".repeat(64)
          }
        ]
      },
      artifact: "second.tar"
    }).contract;
    assert.notEqual(first.candidateId, second.candidateId);
    assert.notEqual(first.candidateVersion, second.candidateVersion);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("promotion follows the schema state, not the identity of the deployed release", () => {
  // This replaces the old three identity checks. A concurrent deploy that changes no schema used
  // to invalidate every candidate in flight and made redeploying a release impossible; the gate now
  // accepts either proven state and refuses anything else.
  const root = mkdtempSync(join(tmpdir(), "aetheus-contract-"));
  try {
    const before = ["20260101000000_InitialCreate"];
    const after = [...before, "20260202000000_AddThing"];
    writeFileSync(join(root, "candidate.tar"), "candidate\n");
    const candidate = generate({
      root,
      source: "e".repeat(40),
      manifest: { schema: 1, packages: [] },
      artifact: "candidate.tar",
      migrations: after,
      baseline: { bootstrap: false, schemaBefore: before }
    });

    const schemaFile = (name, list) => {
      const path = join(root, name);
      writeFileSync(path, list.join("\n") + "\n");
      return path;
    };
    const gate = schemaPath => spawnSync(
      process.execPath, [verifier, candidate.path, schemaPath, root], { encoding: "utf8" });

    let result = gate(schemaFile("schema-before", before));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /proven pre-migration schema/);

    result = gate(schemaFile("schema-after", after));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /already stands on the proven post-migration schema/);

    result = gate(schemaFile("schema-rogue", [...after, "20260303000000_Unqualified"]));
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /neither state this candidate was proven against/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
