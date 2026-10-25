// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-js-crypto.yml (opengrep scan --test).
const crypto = require("crypto");
const jwt = require("jsonwebtoken");
const https = require("https");
const bcrypt = require("bcrypt");

function createSessionToken() {
  // ruleid: aetheus.javascript.math-random-secret
  return Math.random().toString(36).slice(2);
}

function pickColor() {
  // ok: aetheus.javascript.math-random-secret
  return Math.floor(Math.random() * 360);
}

function secureToken() {
  // ok: aetheus.javascript.math-random-secret
  return crypto.randomBytes(32).toString("hex");
}

// ruleid: aetheus.javascript.math-random-secret
const resetOtp =Math.floor(Math.random() * 1000000);

function transport() {
  // ruleid: aetheus.javascript.tls-verification-disabled
  const insecure = new https.Agent({ rejectUnauthorized: false });
  // ruleid: aetheus.javascript.tls-verification-disabled
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
  // ok: aetheus.javascript.tls-verification-disabled
  const strict = new https.Agent({ rejectUnauthorized: true });
}

function hashes(input, password) {
  // ruleid: aetheus.javascript.weak-hash
  const md5 = crypto.createHash("md5").update(input).digest("hex");
  // ruleid: aetheus.javascript.weak-hash
  const sha1 = crypto.createHash("sha1").update(input).digest("hex");
  // ok: aetheus.javascript.weak-hash
  const sha256 = crypto.createHash("sha256").update(input).digest("hex");

  // ruleid: aetheus.javascript.weak-password-hash-cost
  const cheap = bcrypt.hashSync(password, 4);
  // ruleid: aetheus.javascript.weak-password-hash-cost
  const quick = crypto.pbkdf2Sync(password, "salt", 1000, 32, "sha256");
  // ok: aetheus.javascript.weak-password-hash-cost
  const costly = bcrypt.hashSync(password, 12);
  // ok: aetheus.javascript.weak-password-hash-cost
  const strong = crypto.pbkdf2Sync(password, "salt", 600000, 32, "sha256");
}

function ciphers(key, iv) {
  // ruleid: aetheus.javascript.weak-cipher
  const des = crypto.createCipheriv("des-ede3-cbc", key, iv);
  // ruleid: aetheus.javascript.weak-cipher
  const ecb = crypto.createCipheriv("aes-128-ecb", key, null);
  // ok: aetheus.javascript.weak-cipher
  const gcm = crypto.createCipheriv("aes-256-gcm", key, iv);

  // ruleid: aetheus.javascript.crypto-create-cipher
  const old = crypto.createCipher("aes192", "password");
}

function tokens(token, secretFromEnvironment) {
  // ruleid: aetheus.javascript.jwt-weak-verification
  jwt.verify(token, secretFromEnvironment, { ignoreExpiration: true });
  // ruleid: aetheus.javascript.jwt-weak-verification
  jwt.verify(token, secretFromEnvironment, { algorithms: ["none", "HS256"] });
  // ok: aetheus.javascript.jwt-weak-verification
  jwt.verify(token, secretFromEnvironment, { algorithms: ["HS256"] });

  // ruleid: aetheus.javascript.jwt-hard-coded-secret
  jwt.sign({ user: "admin" }, "super-secret-value");
  // ok: aetheus.javascript.jwt-hard-coded-secret
  jwt.sign({ user: "admin" }, secretFromEnvironment);
}

// ruleid: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("message", (event) => {
  handle(event.data);
});

// ruleid: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("message", function (event) {
  handle(event.data);
});

// ok: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("message", (event) => {
  if (event.origin !== "https://app.example.org") {
    return;
  }
  handle(event.data);
});

// ok: aetheus.javascript.postmessage-no-origin-check
window.addEventListener("resize", () => {
  relayout();
});

function messages(frame, payload) {
  // ruleid: aetheus.javascript.postmessage-wildcard-target
  frame.contentWindow.postMessage(payload, "*");
  // ok: aetheus.javascript.postmessage-wildcard-target
  frame.contentWindow.postMessage(payload, "https://app.example.org");
}

function cookies(res, value) {
  // ruleid: aetheus.javascript.cookie-without-secure
  document.cookie = "theme=" + value + "; path=/";
  // ok: aetheus.javascript.cookie-without-secure
  document.cookie = "theme=" + value + "; path=/; Secure; SameSite=Lax";
  // ok: aetheus.javascript.cookie-without-secure
  document.cookie = "theme=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";

  // ruleid: aetheus.javascript.cookie-flags-disabled
  res.cookie("session", value, { httpOnly: false, secure: true });
  // ruleid: aetheus.javascript.cookie-flags-disabled
  res.cookie("session", value, { httpOnly: true, secure: false });
  // ok: aetheus.javascript.cookie-flags-disabled
  res.cookie("session", value, { httpOnly: true, secure: true });
}

function storage(value) {
  // ruleid: aetheus.javascript.web-storage-secret
  localStorage.setItem("access_token", value);
  // ok: aetheus.javascript.web-storage-secret
  localStorage.setItem("theme", value);
}

function sockets() {
  // ruleid: aetheus.javascript.cleartext-websocket
  const remote = new WebSocket("ws://events.example.org/stream");
  // ok: aetheus.javascript.cleartext-websocket
  const secure = new WebSocket("wss://events.example.org/stream");
  // ok: aetheus.javascript.cleartext-websocket
  const local = new WebSocket("ws://localhost:5000/stream");
}
