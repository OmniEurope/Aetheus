// SPDX-License-Identifier: EUPL-1.2
//
// Names the tests a suite failed, so a gate status of 1 says WHICH tests made it 1. The status alone
// (QA_CURRENT_INTEGRATION_GATE_STATUS=1) left the reader with a 5000-line log to search (run 2638).
//
//   list-failed-tests.mjs record <trx> <list-file|->
//     Prints "Failed tests (N): ..." when the TRX records failures and writes the names, one per
//     line, to <list-file> (an empty file when none failed). "-" prints without writing.
//   list-failed-tests.mjs publish <VARIABLE> <source>...
//     Merges the failed names of every source (a .trx is read, any other file is a list written by
//     `record`; an absent source is skipped) and, when there is at least one, prints the log line
//     and `##aetheus[setvariable name=<VARIABLE>]<names>`. Nothing is published when none failed.
//
// The published value is one line (the backend reads a setvariable up to the end of its line),
// comma-separated, capped to MaxNames names and MaxValueLength characters with a "+N more" tail.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const MaxNames = 20;
export const MaxValueLength = 1500;
const MaxNameLength = 200;
const FailedOutcomes = new Set(["Failed", "Error", "Timeout", "Aborted"]);

function decodeXml(text) {
  return text
    .replace(/&quot;/g, "\"")
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&#x([0-9a-fA-F]+);/g, (_, hex) => String.fromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&#([0-9]+);/g, (_, dec) => String.fromCodePoint(Number(dec)))
    .replace(/&amp;/g, "&");
}

/// A C0 control character or DEL, read by code point: a control range inside a regular expression is
/// what ESLint no-control-regex reports.
function isControl(character) {
  const code = character.codePointAt(0);
  return code < 0x20 || code === 0x7f;
}

/// One display-safe name: no line break or control character (it would end the setvariable line),
/// no comma (the list separator), no shell-active character (`$`, backtick), bounded in length.
export function sanitizeTestName(name) {
  const cleaned = Array.from(name, (character) => (isControl(character) ? " " : character))
    .join("")
    .replace(/,/g, ";")
    .replace(/[$`\\]/g, "_")
    .replace(/\s+/g, " ")
    .trim();
  return cleaned.length > MaxNameLength ? `${cleaned.slice(0, MaxNameLength - 3)}...` : cleaned;
}

/// The failed test names of one TRX, in file order, each once (a retried test appears once).
export function failedTestNames(trx) {
  const names = [];
  for (const match of trx.matchAll(/<UnitTestResult\b([^>]*)>/g)) {
    const attributes = Object.fromEntries([...match[1].matchAll(/([A-Za-z][A-Za-z0-9-]*)="([^"]*)"/g)]
      .map(attribute => [attribute[1], attribute[2]]));
    if (!FailedOutcomes.has(attributes.outcome) || !attributes.testName) continue;
    const name = sanitizeTestName(decodeXml(attributes.testName));
    if (name.length > 0 && !names.includes(name)) names.push(name);
  }
  return names;
}

/// The single-line value: the first names that fit, then "+N more" for the rest.
export function formatFailedTests(names) {
  const shown = [];
  let length = 0;
  for (const name of names) {
    if (shown.length >= MaxNames) break;
    const added = (shown.length === 0 ? 0 : 2) + name.length;
    // Room is kept for the "+N more" tail.
    if (length + added > MaxValueLength - 16) break;
    shown.push(name);
    length += added;
  }
  const rest = names.length - shown.length;
  return rest > 0 ? [...shown, `+${rest} more`].join(", ") : shown.join(", ");
}

export function logLine(names) {
  return `Failed tests (${names.length}): ${formatFailedTests(names)}`;
}

function readSource(path) {
  if (!existsSync(path)) return [];
  const text = readFileSync(path, "utf8");
  if (path.toLowerCase().endsWith(".trx")) return failedTestNames(text);
  return text.split(/\r?\n/).map(sanitizeTestName).filter(name => name.length > 0);
}

export function collectFailedTests(sources) {
  const names = [];
  for (const source of sources)
    for (const name of readSource(source))
      if (!names.includes(name)) names.push(name);
  return names;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [mode, ...args] = process.argv.slice(2);
  if (mode === "record" && args.length === 2) {
    const [trxPath, listPath] = args;
    const names = failedTestNames(readFileSync(trxPath, "utf8"));
    if (listPath !== "-") writeFileSync(listPath, names.map(name => `${name}\n`).join(""));
    if (names.length > 0) console.log(logLine(names));
  } else if (mode === "publish" && args.length >= 2) {
    const [variable, ...sources] = args;
    if (!/^[A-Z][A-Z0-9_]*$/.test(variable)) throw new Error(`Invalid variable name '${variable}'.`);
    const names = collectFailedTests(sources);
    if (names.length > 0) {
      console.log(logLine(names));
      console.log(`##aetheus[setvariable name=${variable}]${formatFailedTests(names)}`);
    }
  } else {
    console.error("Usage: list-failed-tests.mjs record <trx> <list-file|->");
    console.error("       list-failed-tests.mjs publish <VARIABLE> <trx-or-list-file>...");
    process.exit(2);
  }
}
