#!/usr/bin/env node
// SPDX-License-Identifier: EUPL-1.2
import { createHash } from "node:crypto";
import { existsSync, lstatSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { relative, resolve, sep } from "node:path";

const [outputArg, sourceCommit] = process.argv.slice(2);
if (!outputArg || !/^[0-9a-f]{40}$/i.test(sourceCommit ?? "")) {
  throw new Error("Usage: generate-artifact-provenance.mjs <output.json> <40-char-source-commit>");
}

const root = resolve(process.cwd());
const output = resolve(outputArg);
if (!(output === root || output.startsWith(`${root}${sep}`))) {
  throw new Error("The provenance output must stay inside the workspace.");
}

function loadCategoryOverride(path) {
  const declared = JSON.parse(readFileSync(resolve(root, path), "utf8"));
  const entries = Object.entries(declared);
  if (entries.length === 0) throw new Error("The provenance category override declares no category.");
  for (const [name, paths] of entries) {
    if (!Array.isArray(paths) || paths.some(entry => typeof entry !== "string" || entry.length === 0)) {
      throw new Error(`Provenance category '${name}' must list workspace-relative paths.`);
    }
    for (const entry of paths) {
      const target = resolve(root, entry);
      if (!(target === root || target.startsWith(`${root}${sep}`))) {
        throw new Error(`Provenance category '${name}' escapes the workspace.`);
      }
    }
  }
  return declared;
}

// An adapting project lays its payload out elsewhere: hashing Aetheus paths against another
// repository would seal a provenance of empty categories, which claims far less than it seems.
// A project may therefore supply its own category map; Aetheus keeps the built-in one below.
const categoryOverridePath = process.env.AETHEUS_PROVENANCE_CATEGORIES;
const categories = categoryOverridePath ? loadCategoryOverride(categoryOverridePath) : {
  dockerImages: [".pipeline-artifacts/aetheus-back.tar.gz", ".pipeline-artifacts/aetheus-front.tar.gz"],
  agentArchives: [".pipeline-artifacts/agent-release"],
  coverage: ["coverage"],
  qaRuntime: ["tests/Aetheus.Back.IntegrationTests/bin/Release/net10.0",
    "tests/Aetheus.Back.IntegrationTests/obj/project.assets.json",
    "tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props",
    "tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.targets",
    ".pipeline-artifacts/aetheus-browser-smoke.tar.gz"],
  buildIntermediates: [],
  optionalObservability: ["src/Aetheus.Telemetry/bin", "src/Aetheus.Telemetry/obj",
    "tests/Aetheus.Telemetry.Tests/bin", "tests/Aetheus.Telemetry.Tests/obj",
    "src/Aetheus.WebAnalytics/bin", "src/Aetheus.WebAnalytics/obj",
    "tests/Aetheus.WebAnalytics.Tests/bin", "tests/Aetheus.WebAnalytics.Tests/obj"],
  contracts: [".pipeline-artifacts/source-commit", ".pipeline-artifacts/qa-rollback-contract",
    ".pipeline-artifacts/agent-protocol-contract", ".pipeline-artifacts/integration-manifest.json",
    ".pipeline-artifacts/delivery-contract.json"]
};

function filesBelow(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) return [];
  const stat = lstatSync(absolute);
  if (stat.isSymbolicLink()) throw new Error(`Artifact provenance refuses symlink: ${path}`);
  if (stat.isFile()) return [absolute];
  if (!stat.isDirectory()) return [];
  return readdirSync(absolute, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name))
    .flatMap(entry => filesBelow(relative(root, resolve(absolute, entry.name))));
}

function summarize(paths) {
  const files = [...new Set(paths.flatMap(filesBelow))].sort();
  const digest = createHash("sha256");
  let totalBytes = 0;
  for (const file of files) {
    const bytes = readFileSync(file);
    const path = relative(root, file).replaceAll("\\", "/");
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    totalBytes += bytes.length;
    digest.update(`${path}\0${bytes.length}\0${sha256}\n`);
  }
  return { fileCount: files.length, totalBytes, sha256: digest.digest("hex") };
}

function summarizeAgentRelease(paths) {
  const summary = summarize(paths);
  const manifestPath = resolve(root, ".pipeline-artifacts/agent-release/agent-release-manifest.json");
  if (!existsSync(manifestPath)) return summary;
  const release = JSON.parse(readFileSync(manifestPath, "utf8"));
  const declaredArtifacts = (release.archives ?? []).map(archive => {
    if (!archive.fileName || !Number.isSafeInteger(archive.sizeBytes) || archive.sizeBytes < 0 ||
        !/^[0-9a-f]{64}$/i.test(archive.sha256 ?? "")) {
      throw new Error("Agent release manifest contains an invalid archive contract.");
    }
    return {
      fileName: archive.fileName,
      sizeBytes: archive.sizeBytes,
      sha256: archive.sha256.toLowerCase()
    };
  }).sort((left, right) => left.fileName.localeCompare(right.fileName));
  return { ...summary, declaredArtifacts };
}

const manifest = {
  schema: 1,
  sourceCommit: sourceCommit.toLowerCase(),
  categories: Object.fromEntries(Object.entries(categories).map(([name, paths]) => [
    name,
    name === "agentArchives" ? summarizeAgentRelease(paths) : summarize(paths)
  ]))
};
writeFileSync(output, `${JSON.stringify(manifest)}\n`);
