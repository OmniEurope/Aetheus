// SPDX-License-Identifier: EUPL-1.2
// Checks the shape of the in-house OpenGrep rule files before a scanner ever loads them.
//
// A malformed rule file does not produce a bad finding, it makes the whole SAST step fail, and it
// fails on the runner, twenty minutes into a chain. This reads the same files the scanner mounts and
// refuses the obvious breakages: a duplicate id, a missing message, a severity that is not one of
// the three, a language nobody declared, a rule with no pattern at all.
//
// It does NOT evaluate the patterns. Only OpenGrep can say whether a pattern matches what its author
// meant, and pretending otherwise here would be the fake green this exists to avoid.
//
// Usage: node validate-opengrep-rules.mjs [directory]
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const DEFAULT_DIRECTORY = ".aetheus/security-rules/opengrep";
const SEVERITIES = new Set(["ERROR", "WARNING", "INFO"]);
const LANGUAGES = new Set([
  "csharp", "javascript", "typescript", "python", "java", "go", "ruby", "php",
  "yaml", "json", "bash", "generic", "regex"
]);
const PATTERN_KEYS = [
  "pattern", "patterns", "pattern-either", "pattern-regex", "pattern-not", "pattern-inside"
];

/// Reads the `id:`, `languages:`, `severity:` and pattern keys without a YAML parser. The files are
/// written by hand in one shape; a dependency to read six keys would cost more than it explains.
export function parseRules(text) {
  const rules = [];
  let current = null;
  for (const rawLine of text.split("\n")) {
    const line = rawLine.replace(/\r$/, "");
    const idMatch = /^\s*-\s+id:\s*(\S+)\s*$/.exec(line);
    if (idMatch) {
      if (current) rules.push(current);
      current = { id: idMatch[1], keys: new Set(), languages: [], severity: null, message: false };
      continue;
    }
    if (!current) continue;

    const keyMatch = /^\s{4,}([a-z-]+):/.exec(line);
    if (keyMatch) current.keys.add(keyMatch[1]);
    const languages = /^\s*languages:\s*\[([^\]]*)\]/.exec(line);
    if (languages)
      current.languages = languages[1].split(",").map(value => value.trim()).filter(Boolean);
    const severity = /^\s*severity:\s*(\S+)/.exec(line);
    if (severity) current.severity = severity[1];
    if (/^\s*message:/.test(line)) current.message = true;
  }
  if (current) rules.push(current);
  return rules;
}

export function findProblems(rulesByFile) {
  const problems = [];
  const seen = new Map();

  for (const [file, rules] of rulesByFile) {
    if (rules.length === 0) problems.push(`${file}: contains no rule.`);
    for (const rule of rules) {
      if (seen.has(rule.id))
        problems.push(`${rule.id}: declared twice (${seen.get(rule.id)} and ${file}).`);
      else seen.set(rule.id, file);

      if (!rule.message) problems.push(`${rule.id}: has no message, so a finding would say nothing.`);
      if (!SEVERITIES.has(rule.severity ?? ""))
        problems.push(`${rule.id}: severity '${rule.severity}' is not ERROR, WARNING or INFO.`);
      if (rule.languages.length === 0) problems.push(`${rule.id}: declares no language.`);
      for (const language of rule.languages)
        if (!LANGUAGES.has(language)) problems.push(`${rule.id}: unknown language '${language}'.`);
      // A taint rule matches through its sources and sinks; it needs both, and no plain pattern.
      const taint = rule.keys.has("pattern-sources") || rule.keys.has("pattern-sinks");
      if (taint && !(rule.keys.has("pattern-sources") && rule.keys.has("pattern-sinks")))
        problems.push(`${rule.id}: a taint rule needs both pattern-sources and pattern-sinks.`);
      else if (!taint && !PATTERN_KEYS.some(key => rule.keys.has(key)))
        problems.push(`${rule.id}: has no pattern, so it can never match.`);
    }
  }
  return problems;
}

export function validateDirectory(directory) {
  const files = readdirSync(directory)
    .filter(name => name.endsWith(".yml") || name.endsWith(".yaml"))
    .sort();
  if (files.length === 0) return [`${directory}: holds no rule file.`];

  const rulesByFile = files.map(name => [name, parseRules(readFileSync(join(directory, name), "utf8"))]);
  return findProblems(rulesByFile);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const directory = process.argv[2] ?? DEFAULT_DIRECTORY;
  const problems = validateDirectory(directory);
  if (problems.length > 0) {
    for (const problem of problems) console.error(problem);
    process.exit(1);
  }
  console.log(`OpenGrep rules under ${directory} are well formed.`);
}
