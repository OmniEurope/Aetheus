// SPDX-License-Identifier: EUPL-1.2
// The in-house SAST rules are mounted into the scanner container as a config directory. A malformed
// file there does not produce a bad finding, it fails the whole security stage on the runner, twenty
// minutes into a chain. These tests check the shape here instead, and check the real rule files.
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

import { findProblems, parseRules, validateDirectory } from "../../../scripts/validate-opengrep-rules.mjs";

const wellFormed = `
rules:
  - id: aetheus.example.one
    languages: [csharp]
    severity: ERROR
    message: Something specific.
    pattern: Foo($X)
`;

test("the repository's own rules are well formed", () => {
  // The point of the whole file: these are the rules the scanner will actually load.
  assert.deepEqual(validateDirectory(resolve(".aetheus/security-rules/opengrep")), []);
});

test("a rule's id, languages, severity and pattern are read", () => {
  const rule = parseRules(wellFormed)[0];

  assert.equal(rule.id, "aetheus.example.one");
  assert.deepEqual(rule.languages, ["csharp"]);
  assert.equal(rule.severity, "ERROR");
  assert.ok(rule.message);
  assert.ok(rule.keys.has("pattern"));
});

test("two rules sharing an id are refused, naming both files", () => {
  const problems = findProblems([
    ["a.yml", parseRules(wellFormed)],
    ["b.yml", parseRules(wellFormed)]
  ]);

  assert.equal(problems.length, 1);
  assert.match(problems[0], /declared twice \(a\.yml and b\.yml\)/);
});

test("a rule with no pattern is refused, because it can never match", () => {
  const problems = findProblems([["a.yml", parseRules(`
rules:
  - id: aetheus.example.two
    languages: [csharp]
    severity: ERROR
    message: Something.
`)]]);

  assert.equal(problems.length, 1);
  assert.match(problems[0], /no pattern/);
});

test("an unknown severity and an unknown language are both named", () => {
  const problems = findProblems([["a.yml", parseRules(`
rules:
  - id: aetheus.example.three
    languages: [cobol]
    severity: CRITICAL
    message: Something.
    pattern: Foo()
`)]]);

  assert.equal(problems.length, 2);
  assert.ok(problems.some(problem => /severity 'CRITICAL'/.test(problem)));
  assert.ok(problems.some(problem => /unknown language 'cobol'/.test(problem)));
});

test("a rule with no message is refused, because its finding would say nothing", () => {
  const problems = findProblems([["a.yml", parseRules(`
rules:
  - id: aetheus.example.four
    languages: [csharp]
    severity: ERROR
    pattern: Foo()
`)]]);

  assert.equal(problems.length, 1);
  assert.match(problems[0], /no message/);
});

test("a directory holding no rule file is refused rather than reported as clean", () => {
  const empty = mkdtempSync(join(tmpdir(), "aetheus-rules-"));
  try {
    const problems = validateDirectory(empty);
    assert.equal(problems.length, 1);
    assert.match(problems[0], /no rule file/);
  } finally {
    rmSync(empty, { recursive: true, force: true });
  }
});

test("a file holding no rule is refused", () => {
  const directory = mkdtempSync(join(tmpdir(), "aetheus-rules-"));
  try {
    writeFileSync(join(directory, "empty.yml"), "rules: []\n");
    assert.match(validateDirectory(directory)[0], /contains no rule/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("a taint rule counts as a pattern only with both its sources and its sinks", () => {
  const taintRule = (sinks) => `
rules:
  - id: aetheus.example.taint
    languages: [javascript]
    severity: ERROR
    message: Something.
    mode: taint
    pattern-sources:
      - pattern: location.hash
${sinks}`;
  const complete = findProblems([["t.yml", parseRules(taintRule("    pattern-sinks:\n      - pattern: eval($X)\n"))]]);
  const sinkless = findProblems([["t.yml", parseRules(taintRule(""))]]);

  assert.deepEqual(complete, []);
  assert.equal(sinkless.length, 1);
  assert.match(sinkless[0], /both pattern-sources and pattern-sinks/);
});
