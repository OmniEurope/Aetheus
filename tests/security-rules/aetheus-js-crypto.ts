// SPDX-License-Identifier: EUPL-1.2
// TypeScript test corpus for .aetheus/security-rules/opengrep/aetheus-js-crypto.yml. The rules
// declare typescript as well as javascript, so the typed forms are proven here.
import { createHash, randomBytes } from "crypto";
import https from "https";

export function newSessionToken(): string {
  // ruleid: aetheus.javascript.math-random-secret
  return Math.random().toString(36).slice(2);
}

export function newSecureToken(): string {
  // ok: aetheus.javascript.math-random-secret
  return randomBytes(32).toString("hex");
}

export function agent(): https.Agent {
  // ruleid: aetheus.javascript.tls-verification-disabled
  return new https.Agent({ rejectUnauthorized: false });
}

export function digest(input: string): string {
  // ruleid: aetheus.javascript.weak-hash
  const legacy = createHash("md5").update(input).digest("hex");
  // ok: aetheus.javascript.weak-hash
  return createHash("sha256").update(input).digest("hex");
}

// ruleid: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("message", (event: MessageEvent) => {
  handle(event.data);
});

// ok: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("message", (event: MessageEvent) => {
  if (event.origin !== "https://app.example.org") {
    return;
  }
  handle(event.data);
});

export function notify(target: Window, payload: unknown): void {
  // ruleid: aetheus.javascript.postmessage-wildcard-target
  target.postMessage(payload, "*");
  // ok: aetheus.javascript.postmessage-wildcard-target
  target.postMessage(payload, "https://app.example.org");
}
