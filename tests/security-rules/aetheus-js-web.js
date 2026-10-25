// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-js-web.yml (opengrep scan --test).
const fs = require("fs");

function handlers(app, User, Account, session, cors, helmet) {
  app.post("/users", async (req, res) => {
    // ruleid: aetheus.javascript.request-mass-assignment
    const created = await User.create(req.body);
    res.json(created);
  });
  app.post("/users-safe", async (req, res) => {
    // ok: aetheus.javascript.request-mass-assignment
    const created = await User.create({ name: req.body.name, email: req.body.email });
    res.json(created);
  });
  app.put("/account", async (req, res) => {
    const account = await Account.findById(req.params.id);
    // ruleid: aetheus.javascript.request-mass-assignment
    Object.assign(account, req.body);
    await account.save();
  });

  app.post("/login", async (req, res) => {
    // ruleid: aetheus.javascript.nosql-operator-injection
    const user = await User.findOne({ name: req.body.name, password: req.body.password });
    res.json(user);
  });
  app.post("/login-safe", async (req, res) => {
    // ok: aetheus.javascript.nosql-operator-injection
    const user = await User.findOne({ name: String(req.body.name) });
    res.json(user);
  });
  app.get("/search", async (req, res) => {
    // ruleid: aetheus.javascript.nosql-operator-injection
    const found = await User.find({ $where: "this.name == '" + req.query.name + "'" });
    res.json(found);
  });

  // ruleid: aetheus.javascript.cors-wildcard-credentials
  app.use(cors({ origin: "*", credentials: true }));
  // ok: aetheus.javascript.cors-wildcard-credentials
  app.use(cors({ origin: "https://app.example.org", credentials: true }));

  // ruleid: aetheus.javascript.security-header-relaxed
  app.use(helmet({ contentSecurityPolicy: false }));
  // ok: aetheus.javascript.security-header-relaxed
  app.use(helmet({ contentSecurityPolicy: { directives: { defaultSrc: ["'self'"] } } }));
  app.get("/page", (req, res) => {
    // ruleid: aetheus.javascript.security-header-relaxed
    res.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'");
    // ok: aetheus.javascript.security-header-relaxed
    res.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self' 'nonce-abc'");
  });

  // ruleid: aetheus.javascript.session-secret-hard-coded
  app.use(session({ secret: "keyboard cat", resave: false }));
  // ok: aetheus.javascript.session-secret-hard-coded
  app.use(session({ secret: process.env.SESSION_SECRET, resave: false }));
}

function pageRedirects() {
  const next = new URLSearchParams(location.search).get("next");
  // ruleid: aetheus.javascript.dom-open-redirect
  window.location = next;
  // ok: aetheus.javascript.dom-open-redirect
  window.location = "/home";
}

function pageRedirectChecked() {
  const next = location.search.slice(1);
  // ok: aetheus.javascript.dom-open-redirect
  window.location = new URL(next, location.origin).pathname;
}

// ruleid: aetheus.javascript.hard-coded-credential
const adminPassword = "Sup3rS3cretValue";
const settings = {
  // ruleid: aetheus.javascript.hard-coded-credential
  apiKey: "aaaa1111aaaa1111",
  // ok: aetheus.javascript.hard-coded-credential
  passwordHeaderName: "X-Password-Header1",
  // ok: aetheus.javascript.hard-coded-credential
  secret: process.env.APP_SECRET
};

function modes(path) {
  // ruleid: aetheus.javascript.world-writable-mode
  fs.chmodSync(path, 0o777);
  // ok: aetheus.javascript.world-writable-mode
  fs.chmodSync(path, 0o640);
}

function xml(libxml, text) {
  // ruleid: aetheus.javascript.xml-external-entities
  const unsafe = libxml.parseXml(text, { noent: true });
  // ok: aetheus.javascript.xml-external-entities
  const safe = libxml.parseXml(text, { noent: false });
}

function objects(serialize, text) {
  // ruleid: aetheus.javascript.unsafe-deserialization
  return serialize.unserialize(text);
}

function objectsSafe(text) {
  // ok: aetheus.javascript.unsafe-deserialization
  return JSON.parse(text);
}
