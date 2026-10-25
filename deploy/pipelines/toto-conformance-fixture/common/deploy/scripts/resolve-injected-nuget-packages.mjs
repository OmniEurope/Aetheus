#!/usr/bin/env node
// SPDX-License-Identifier: EUPL-1.2
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

const [manifestPath, outputDirectory] = process.argv.slice(2);
if (!manifestPath || !outputDirectory)
  throw new Error(
    "Usage: resolve-injected-nuget-packages.mjs <integration-manifest> <output-directory>");

const manifest = JSON.parse(await readFile(manifestPath));
if (manifest.schema !== 1 || !Array.isArray(manifest.packages))
  throw new Error("The integration manifest is invalid.");

const supported = new Map([
  ["Aetheus.Telemetry", {
    enabledProperty: "EnableAetheusTelemetry",
    versionProperty: "AetheusTelemetryVersion",
    constant: "AETHEUS_TELEMETRY"
  }],
  ["Aetheus.WebAnalytics", {
    enabledProperty: "EnableAetheusWebAnalytics",
    versionProperty: "AetheusWebAnalyticsVersion",
    constant: "AETHEUS_WEB_ANALYTICS"
  }]
]);
const enabled = manifest.packages.filter(item => item.enabled === true);
const feedBase = (process.env.AETHEUS_PACKAGE_BASE_URL ?? "").replace(/\/+$/, "");
const token = process.env.AETHEUS_PACKAGE_TOKEN ?? "";
if (enabled.length > 0 && (!feedBase.startsWith("https://") || !token))
  throw new Error(
    "Enabled integrations require AETHEUS_PACKAGE_BASE_URL over HTTPS and AETHEUS_PACKAGE_TOKEN.");

await mkdir(outputDirectory, { recursive: true, mode: 0o755 });
let signerFingerprint = "";
if (enabled.length > 0) {
  signerFingerprint = (
    await readFile(join(outputDirectory, "signer-fingerprint.txt"), "utf8")
  ).trim().toUpperCase();
  if (!/^[0-9A-F]{64}$/.test(signerFingerprint))
    throw new Error("The package signer fingerprint is invalid.");
}
const feedDirectory = join(outputDirectory, "feed");
await mkdir(feedDirectory, { recursive: true, mode: 0o755 });
const resolvedPackages = [];
const propertyLines = [];
const constants = [];

for (const item of manifest.packages) {
  const contract = supported.get(item.id);
  if (!contract)
    throw new Error(`Unsupported injected NuGet package '${item.id}'.`);
  const isEnabled = item.enabled === true;
  propertyLines.push(`    <${contract.enabledProperty}>${isEnabled}</${contract.enabledProperty}>`);
  if (!isEnabled)
    continue;
  if (typeof item.version !== "string" || !/^[0-9A-Za-z.+-]+$/.test(item.version)
      || !/^[0-9a-f]{64}$/i.test(item.sha256 ?? ""))
    throw new Error(`Enabled package '${item.id}' requires an exact version and sha256.`);

  const normalizedId = item.id.toLowerCase();
  const normalizedVersion = item.version.toLowerCase();
  const fileName = `${normalizedId}.${normalizedVersion}.nupkg`;
  const url = `${feedBase}/api/packages/nuget/v3/flatcontainer/`
    + `${encodeURIComponent(normalizedId)}/${encodeURIComponent(normalizedVersion)}/`
    + encodeURIComponent(fileName);
  const response = await fetch(url, {
    headers: { Authorization: `Bearer ${token}` },
    redirect: "error"
  });
  if (!response.ok)
    throw new Error(`Could not resolve ${item.id} ${item.version}: HTTP ${response.status}.`);
  const bytes = Buffer.from(await response.arrayBuffer());
  const actual = createHash("sha256").update(bytes).digest("hex");
  if (actual !== item.sha256.toLowerCase())
    throw new Error(`Digest mismatch for ${item.id} ${item.version}.`);
  const packagePath = join(feedDirectory, fileName);
  await writeFile(packagePath, bytes, { mode: 0o644 });
  propertyLines.push(`    <${contract.versionProperty}>${item.version}</${contract.versionProperty}>`);
  constants.push(contract.constant);
  resolvedPackages.push({
    id: item.id,
    version: item.version,
    sha256: actual,
    enabled: true
  });
}

if (constants.length > 0)
  propertyLines.push(`    <DefineConstants>$(DefineConstants);${constants.join(";")}</DefineConstants>`);
await writeFile(join(outputDirectory, "Directory.Build.injected.props"), [
  "<Project>",
  "  <PropertyGroup>",
  ...propertyLines,
  "  </PropertyGroup>",
  "</Project>",
  ""
].join("\n"), { mode: 0o644 });
const nugetConfig = [
  "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
  "<configuration>",
  "  <packageSources>",
  "    <clear />",
  "    <add key=\"aetheus-injected\" value=\"./feed\" />",
  "    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" />",
  "  </packageSources>",
  "</configuration>",
  ""
];
await writeFile(
  join(outputDirectory, "NuGet.config"),
  nugetConfig.join("\n"),
  { mode: 0o644 });
if (signerFingerprint) {
  const verificationDirectory = join(outputDirectory, "verify");
  await mkdir(verificationDirectory, { recursive: true, mode: 0o755 });
  const verificationConfig = [
    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
    "<configuration>",
    "  <packageSources>",
    "    <clear />",
    "  </packageSources>",
    "  <trustedSigners>",
    "    <author name=\"aetheus-local-observability\">",
    `      <certificate fingerprint="${signerFingerprint}" hashAlgorithm="SHA256" allowUntrustedRoot="true" />`,
    "    </author>",
    "  </trustedSigners>",
    "</configuration>",
    ""
  ];
  await writeFile(
    join(verificationDirectory, "NuGet.config"),
    verificationConfig.join("\n"),
    { mode: 0o644 });
}

const lock = {
  schema: 1,
  packages: resolvedPackages.sort((left, right) => left.id.localeCompare(right.id))
};
await writeFile(
  join(outputDirectory, "manifest.lock.json"),
  `${JSON.stringify(lock, null, 2)}\n`,
  { mode: 0o644 });
