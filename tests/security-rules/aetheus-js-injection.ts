// SPDX-License-Identifier: EUPL-1.2
// TypeScript test corpus for .aetheus/security-rules/opengrep/aetheus-js-injection.yml. The rules
// declare typescript as well as javascript, so the typed and the ES module forms are proven here.
import { exec } from "node:child_process";
import type { Request, Response } from "express";

export function run(folder: string, name: string): void {
  // ruleid: aetheus.javascript.child-process-dynamic-command
  exec(`ls ${folder}`);
  // ruleid: aetheus.javascript.child-process-dynamic-command
  exec("convert " + name);
  // ok: aetheus.javascript.child-process-dynamic-command
  exec("ls -la");
}

export function render(target: HTMLElement, value: string): void {
  // ruleid: aetheus.javascript.html-sink-non-literal
  target.innerHTML = value;
  // ok: aetheus.javascript.html-sink-non-literal
  target.textContent = value;
  // ruleid: aetheus.javascript.timer-string-code
  setTimeout("refresh()", 10);
  // ok: aetheus.javascript.timer-string-code
  setTimeout(() => refresh(), 10);
}

export function load(name: string): unknown {
  // ruleid: aetheus.javascript.dynamic-require
  return require("./plugins/" + name);
}

export function redirect(req: Request, res: Response): void {
  // ruleid: aetheus.javascript.request-open-redirect
  res.redirect(req.query.next as string);
  // ok: aetheus.javascript.request-open-redirect
  res.redirect("/home");
}

export function matcher(): RegExp {
  const term: string = location.search.slice(1);
  // ruleid: aetheus.javascript.regexp-from-input
  return new RegExp(term, "i");
}

export function fixedMatcher(): RegExp {
  // ok: aetheus.javascript.regexp-from-input
  return new RegExp("^[a-z]+$", "i");
}
