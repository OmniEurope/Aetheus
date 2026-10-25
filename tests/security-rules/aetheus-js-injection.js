// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-js-injection.yml (opengrep scan --test).
const childProcess = require("child_process");
const vm = require("vm");

function timers(delay, code, fn) {
  // ruleid: aetheus.javascript.timer-string-code
  setTimeout("refresh()", delay);
  // ruleid: aetheus.javascript.timer-string-code
  setInterval("poll(" + code + ")", delay);
  // ok: aetheus.javascript.timer-string-code
  setTimeout(fn, delay);
  // ok: aetheus.javascript.timer-string-code
  setTimeout(() => refresh(), delay);
}

function indirectEval(code) {
  // ruleid: aetheus.javascript.eval-indirect
  window.eval(code);
  // ruleid: aetheus.javascript.eval-indirect
  const make = Function("a", "return a + 1");
  // ok: aetheus.javascript.eval-indirect
  const parsed = JSON.parse(code);
}

function scripts(code) {
  // ruleid: aetheus.javascript.vm-dynamic-code
  vm.runInNewContext(code, {});
  // ruleid: aetheus.javascript.vm-dynamic-code
  const script = new vm.Script(code);
  // ok: aetheus.javascript.vm-dynamic-code
  vm.runInNewContext("1 + 1", {});
}

function markup(target, value, safe) {
  // ruleid: aetheus.javascript.html-sink-non-literal
  target.innerHTML = value;
  // ruleid: aetheus.javascript.html-sink-non-literal
  target.insertAdjacentHTML("beforeend", "<li>" + value + "</li>");
  // ruleid: aetheus.javascript.html-sink-non-literal
  document.write(value);
  // ruleid: aetheus.javascript.html-sink-non-literal
  $("#out").html(value);
  // ok: aetheus.javascript.html-sink-non-literal
  target.innerHTML = "<b>static</b>";
  // ok: aetheus.javascript.html-sink-non-literal
  target.innerHTML = DOMPurify.sanitize(value);
  // ok: aetheus.javascript.html-sink-non-literal
  target.textContent = value;
}

function commands(name, folder) {
  // ruleid: aetheus.javascript.child-process-dynamic-command
  childProcess.exec(`ls ${folder}`);
  // ruleid: aetheus.javascript.child-process-dynamic-command
  childProcess.execSync("convert " + name + " out.png");
  // ok: aetheus.javascript.child-process-dynamic-command
  childProcess.exec("ls -la");
  // ok: aetheus.javascript.child-process-dynamic-command
  childProcess.execFileSync("convert", [name, "out.png"]);
}

function shellOption(name) {
  // ruleid: aetheus.javascript.child-process-shell-option
  childProcess.spawn("convert", [name], { shell: true });
  // ok: aetheus.javascript.child-process-shell-option
  childProcess.spawn("convert", [name], { shell: false });
  // ok: aetheus.javascript.child-process-shell-option
  childProcess.spawn("convert", [name]);
}

function expressions(location, escaped) {
  const term = location.search.slice(1);
  // ruleid: aetheus.javascript.regexp-from-input
  const matcher = new RegExp(term, "i");
  // ok: aetheus.javascript.regexp-from-input
  const literal = new RegExp(escapeRegExp(term), "i");
  // ok: aetheus.javascript.regexp-from-input
  const fixed = new RegExp("^[a-z]+$");
}

function modules(name) {
  // ruleid: aetheus.javascript.dynamic-require
  const plugin = require("./plugins/" + name);
  // ruleid: aetheus.javascript.dynamic-require
  const lazy = import(name);
  // ok: aetheus.javascript.dynamic-require
  const fixed = require("./plugins/known");
  // ok: aetheus.javascript.dynamic-require
  const known = import("./known.js");
}

function pollution(key, value, source, target) {
  // ruleid: aetheus.javascript.prototype-chain-write
  target.__proto__.polluted = value;
  // ruleid: aetheus.javascript.prototype-chain-write
  target.constructor.prototype[key] = value;
  // ruleid: aetheus.javascript.prototype-chain-write
  Object.prototype.isAdmin = true;
  // ok: aetheus.javascript.prototype-chain-write
  target.polluted = value;
}

function merge(target, source) {
  // ruleid: aetheus.javascript.prototype-pollution-merge
  for (const key in source) {
    target[key] = source[key];
  }
}

function mergeGuarded(target, source) {
  // ok: aetheus.javascript.prototype-pollution-merge
  for (const key in source) {
    if (key === "__proto__") {
      continue;
    }
    target[key] = source[key];
  }
}

function handlers(app, db, fs) {
  app.get("/go", (req, res) => {
    // ruleid: aetheus.javascript.request-open-redirect
    res.redirect(req.query.next);
  });
  app.get("/home", (req, res) => {
    // ok: aetheus.javascript.request-open-redirect
    res.redirect("/home");
  });
  app.get("/file", (req, res) => {
    // ruleid: aetheus.javascript.request-path-traversal
    fs.readFile("/srv/files/" + req.query.name, (error, data) => res.end(data));
  });
  app.get("/safe-file", (req, res) => {
    // ok: aetheus.javascript.request-path-traversal
    fs.readFile("/srv/files/" + path.basename(req.query.name), (error, data) => res.end(data));
  });
  app.get("/proxy", async (req, res) => {
    // ruleid: aetheus.javascript.request-ssrf
    const body = await fetch(req.query.url);
    res.json(await body.json());
  });
  app.get("/status", async (req, res) => {
    // ok: aetheus.javascript.request-ssrf
    const body = await fetch("https://api.example.org/status");
    res.json(await body.json());
  });
  app.get("/user", (req, res) => {
    // ruleid: aetheus.javascript.request-sql-injection
    db.query("SELECT * FROM users WHERE name = '" + req.query.name + "'");
  });
  app.get("/user-safe", (req, res) => {
    // ok: aetheus.javascript.request-sql-injection
    db.query("SELECT * FROM users WHERE name = $1", [req.query.name]);
  });
  app.get("/echo", (req, res) => {
    // ruleid: aetheus.javascript.request-reflected-xss
    res.send("<h1>" + req.query.title + "</h1>");
  });
  app.get("/echo-safe", (req, res) => {
    // ok: aetheus.javascript.request-reflected-xss
    res.send("<h1>" + escapeHtml(req.query.title) + "</h1>");
  });
}
