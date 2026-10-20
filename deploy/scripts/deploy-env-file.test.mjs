// SPDX-License-Identifier: EUPL-1.2
// deploy.sh keeps the deployment's secrets in .env-<env>, next to where it runs. It is run here for
// real in a temporary directory, with docker, git and stty replaced by stand-ins: docker records the
// values deploy.sh exported to it, which are the values the containers would get.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const script = resolve("deploy/scripts/deploy.sh");
// The keys load_env_file reads from the env file (deploy.sh).
const DEPLOY_ENV_KEYS = new Set(["DB_PASSWORD", "ADMIN_PASSWORD", "JWT_KEY", "ENCRYPTION_KEY", "ENCRYPTION_SALT", "APP_VERSION"]);
const separator = process.platform === "win32" ? ";" : ":";

function setUp() {
  const root = mkdtempSync(join(tmpdir(), "aetheus-deploy-")).replace(/\\/g, "/");
  const bin = `${root}/bin`;
  mkdirSync(bin);
  mkdirSync(`${root}/aetheus`);
  const stub = (name, body) => {
    writeFileSync(`${bin}/${name}`, `#!/bin/sh\n${body}\n`);
    chmodSync(`${bin}/${name}`, 0o755);
  };
  stub("docker", `echo "$*" >> "${root}/docker.log"
case "$1" in
  compose) printf '%s\\n' "$DB_PASSWORD" > "${root}/seen-db"; printf '%s\\n' "$ADMIN_PASSWORD" > "${root}/seen-admin" ;;
  ps) if [ -f "${root}/ps-fail" ]; then echo "Cannot connect to the Docker daemon at unix:///var/run/docker.sock" >&2; exit 1; fi
      [ -f "${root}/ps-out" ] && cat "${root}/ps-out" ;;
esac
exit 0`);
  stub("git", "exit 0");
  stub("stty", "exit 0");
  const run = (args, input = "") => spawnSync("sh", [script, ...args], {
    cwd: root,
    input,
    encoding: "utf8",
    env: {
      // The values deploy.sh reads from its env file are dropped from the inherited environment: a
      // pipeline exports some of them (APP_VERSION=…-nightly in the nightly made increment_patch
      // fail with "Illegal number"), and the test is about the file, not the caller's shell.
      ...Object.fromEntries(Object.entries(process.env).filter(([key]) => !DEPLOY_ENV_KEYS.has(key))),
      PATH: `${bin}${separator}${process.env.PATH}`,
      SERVERNAME: "example.com",
      GITREMOTE: "https://git.example.com/aetheus.git"
    }
  });
  const envFile = `${root}/.env-prod`;
  const seen = name => readFileSync(`${root}/seen-${name}`, "utf8").replace(/\n$/, "");
  const containers = lines => writeFileSync(`${root}/ps-out`, lines.map(line => `${line}\n`).join(""));
  return { root, envFile, run, seen, containers, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

const healthy = ["aetheus-prod-database\tUp 2 hours (healthy)", "aetheus-prod-back\tUp 2 hours (healthy)", "aetheus-prod-front\tUp 2 hours"];

// Treat environment files as data and preserve values byte for byte without executing them. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

test("passwords with shell syntax round-trip exactly and run nothing", () => {
  const env = setUp();
  try {
    // One holds a single quote (written "..." with escapes), the other does not (written '...').
    const dbPassword = String.raw`p$a` + "`b'c\"d\\e $(touch pwned) ;x\\";
    const adminPassword = String.raw`ad$HOME` + "`id`\"\\q $(touch pwned2)";
    const created = env.run(["-d"], `${dbPassword}\n${adminPassword}\n1.0.0\n`);
    assert.equal(created.status, 0, created.stderr);
    assert.equal(env.seen("db"), dbPassword);
    assert.equal(env.seen("admin"), adminPassword);

    env.containers(healthy);
    const diagnosed = env.run(["-x"]);
    assert.equal(diagnosed.status, 0, diagnosed.stdout + diagnosed.stderr);
    assert.equal(env.seen("db"), dbPassword, "read back from the file");
    assert.equal(env.seen("admin"), adminPassword, "read back from the file");
    const listing = [...readdirSync(env.root), ...readdirSync(`${env.root}/aetheus`)];
    assert.ok(!listing.some(name => name.startsWith("pwned")), listing.join(", "));
    assert.match(readFileSync(env.envFile, "utf8"), /^ADMIN_PASSWORD='ad\$HOME`id`"\\q \$\(touch pwned2\)'$/m);
  } finally { env.cleanup(); }
});

test("a file an older deploy.sh wrote is read as it was and left unchanged", () => {
  const env = setUp();
  try {
    const legacy = 'DB_PASSWORD="legacy+/=Pass"\nADMIN_PASSWORD="ad\\\\min\\q"\nJWT_KEY="abc"\nENCRYPTION_KEY="def"\nENCRYPTION_SALT="gh+/="\nAPP_VERSION="1.2.3"\n';
    writeFileSync(env.envFile, legacy);
    env.containers(healthy);
    const result = env.run(["-x"]);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.equal(env.seen("db"), "legacy+/=Pass");
    // The shell's own reading of "ad\\min\q": \\ is one backslash, \q stays as written.
    assert.equal(env.seen("admin"), "ad\\min\\q");
    assert.equal(readFileSync(env.envFile, "utf8"), legacy);
  } finally { env.cleanup(); }
});

for (const [label, line] of [
  ["an unescaped dollar", 'DB_PASSWORD="pa$word"'],
  ["a command substitution", 'DB_PASSWORD="x$(touch pwned)"'],
  ["a backtick", 'DB_PASSWORD="x`touch pwned`"'],
  ["an unquoted value with shell syntax", "DB_PASSWORD=a;touch pwned"]
]) {
  test(`a value holding ${label} is refused, not guessed, and the file is kept`, () => {
    const env = setUp();
    try {
      const content = `${line}\nADMIN_PASSWORD="admin"\nAPP_VERSION="1.0.0"\n`;
      writeFileSync(env.envFile, content);
      const result = env.run(["-d"]);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /DB_PASSWORD in .*cannot be read as a value/);
      assert.equal(readFileSync(env.envFile, "utf8"), content);
      assert.ok(!existsSync(`${env.root}/docker.log`), "docker was not called");
      assert.ok(!readdirSync(env.root).some(name => name.startsWith("pwned")));
    } finally { env.cleanup(); }
  });
}

test("a missing secret is generated and appended, the existing values stay byte for byte", () => {
  const env = setUp();
  try {
    const existing = "DB_PASSWORD='kept$value'\nADMIN_PASSWORD='admin'\nJWT_KEY='jwt'\nENCRYPTION_KEY='key'\n";
    writeFileSync(env.envFile, existing);
    const result = env.run(["-d"]);
    assert.equal(result.status, 0, result.stderr);
    const after = readFileSync(env.envFile, "utf8");
    assert.ok(after.startsWith(existing), after);
    assert.match(after.slice(existing.length), /^ENCRYPTION_SALT='[A-Za-z0-9+/=]+'\n$/);
    assert.equal(env.seen("db"), "kept$value");
  } finally { env.cleanup(); }
});

// Report healthy only when Docker answers and every required stack container is healthy. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

for (const [label, prepare, status, verdict] of [
  ["docker cannot be reached", env => writeFileSync(`${env.root}/ps-fail`, ""), 2, /HEALTH: UNKNOWN - docker ps failed[\s\S]*Cannot connect/],
  ["the stack has no container", env => env.containers([]), 1, /HEALTH: FAIL - container\(s\) of aetheus-prod not found: aetheus-prod-database aetheus-prod-back aetheus-prod-front/],
  ["one container is missing", env => env.containers(healthy.slice(0, 2)), 1, /not found: aetheus-prod-front$/m],
  ["one container has exited", env => env.containers([...healthy.slice(0, 2), "aetheus-prod-front\tExited (1) 3 minutes ago"]), 1, /HEALTH: FAIL - unhealthy container/],
  ["the whole stack runs", env => env.containers(healthy), 0, /HEALTH: OK/]
]) {
  test(`diagnostics when ${label}`, () => {
    const env = setUp();
    try {
      writeFileSync(env.envFile, "DB_PASSWORD='db'\nADMIN_PASSWORD='admin'\nJWT_KEY='j'\nENCRYPTION_KEY='k'\nENCRYPTION_SALT='s'\nAPP_VERSION='1.0.0'\n");
      prepare(env);
      const result = env.run(["-x"]);
      assert.equal(result.status, status, result.stdout + result.stderr);
      assert.match(result.stdout, verdict);
      if (status !== 0) assert.doesNotMatch(result.stdout, /HEALTH: OK/);
    } finally { env.cleanup(); }
  });
}
