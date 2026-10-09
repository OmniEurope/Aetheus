// SPDX-License-Identifier: EUPL-1.2
// ensure-tls-certificates.sh is the only thing standing between production and an expired
// certificate (PLAN-003 2.1). It is run here for real, with sudo and openssl replaced by stand-ins
// that record what was asked and answer from files, so every branch - issue, renew, keep, refuse -
// is observed on the script itself rather than restated.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const script = resolve("deploy/scripts/ensure-tls-certificates.sh");

// A certificate is a file holding its expiry and its names; the fake openssl reads it back in the
// two formats the script parses.
function certificate(days, names) {
  const end = new Date(Date.now() + days * 86400000).toUTCString().replace(/^\w+, /, "").replace(" GMT", " GMT");
  return `END=${end}\nSAN=${names.join(",")}\n`;
}

function setUp({ existing = {}, issue = "ok", renew = "ok" } = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-tls-"));
  const bin = join(root, "bin");
  const live = join(root, "live");
  const helpers = join(root, "helpers");
  mkdirSync(bin);
  mkdirSync(live);
  mkdirSync(helpers);
  for (const [lineage, content] of Object.entries(existing)) {
    mkdirSync(join(live, lineage));
    writeFileSync(join(live, lineage, "fullchain.pem"), content);
  }
  const log = join(root, "sudo.log");
  writeFileSync(join(bin, "sudo"), `#!/bin/sh
shift
echo "$*" >> "${log.replace(/\\/g, "/")}"
case "$1" in
  */aetheus-certbot-issue)
    [ "$FAKE_ISSUE" = ok ] || exit 1
    mkdir -p "$TLS_LIVE_DIR/$2"
    printf 'END=%s\\nSAN=%s\\n' "$(date -u -d '+90 days' '+%b %e %H:%M:%S %Y GMT')" "$AETHEUS_CERTBOT_DOMAINS" > "$TLS_LIVE_DIR/$2/fullchain.pem" ;;
  */aetheus-certbot-manage)
    [ "$FAKE_RENEW" = ok ] || exit 1
    sans="$(sed -n 's/^SAN=//p' "$TLS_LIVE_DIR/$3/fullchain.pem")"
    printf 'END=%s\\nSAN=%s\\n' "$(date -u -d '+90 days' '+%b %e %H:%M:%S %Y GMT')" "$sans" > "$TLS_LIVE_DIR/$3/fullchain.pem" ;;
esac
`);
  writeFileSync(join(bin, "openssl"), `#!/bin/sh
while [ "$#" -gt 0 ]; do case "$1" in -in) file="$2"; shift ;; -enddate) mode=end ;; -ext) mode=san; shift ;; esac; shift; done
case "$mode" in
  end) echo "notAfter=$(sed -n 's/^END=//p' "$file")" ;;
  san) echo "X509v3 Subject Alternative Name:"; echo "    $(sed -n 's/^SAN=//p' "$file" | sed 's/[^,]*/DNS:&/g; s/,/, /g')" ;;
esac
`);
  chmodSync(join(bin, "sudo"), 0o755);
  chmodSync(join(bin, "openssl"), 0o755);

  // The stand-ins are put first on PATH from inside the shell. Prepending them to the PATH handed to
  // sh is not enough on Windows: Git's bin/sh.exe launcher puts its own /mingw64/bin in front, where
  // the real openssl lives, so the script read the synthetic certificates with the real tool and
  // seven of these tests failed (DEP-026). `cd && pwd` turns the directory into the shell's own form.
  const inShell = (...argv) => spawnSync("sh", [
    "-c", 'PATH="$(cd "$1" && pwd):$PATH"; export PATH; shift; exec "$@"', "sh", bin.replace(/\\/g, "/"), ...argv
  ], {
    encoding: "utf8",
    env: {
      ...process.env,
      TLS_LIVE_DIR: live.replace(/\\/g, "/"),
      AGENT_HELPERS_DIR: "/opt/helpers",
      FAKE_ISSUE: issue,
      FAKE_RENEW: renew
    }
  });
  const run = (...groups) => inShell("sh", script, ...groups);
  const resolved = name => inShell("sh", "-c", 'command -v "$1"', "sh", name).stdout.trim();
  const calls = () => (existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").filter(Boolean) : []);
  return { root, run, calls, resolved, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

test("the script resolves openssl and sudo to the stand-ins, not to the host's tools", () => {
  const env = setUp();
  try {
    const standIns = `/${env.root.split(/[\\/]/).pop()}/bin/`;
    assert.ok(env.resolved("openssl").endsWith(`${standIns}openssl`), env.resolved("openssl"));
    assert.ok(env.resolved("sudo").endsWith(`${standIns}sudo`), env.resolved("sudo"));
  } finally { env.cleanup(); }
});

test("a missing certificate is issued, then checked", () => {
  const env = setUp();
  try {
    const result = env.run("app.example.org");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls(), ["/opt/helpers/aetheus-certbot-issue app.example.org"]);
  } finally { env.cleanup(); }
});

test("a certificate expiring within 30 days is renewed", () => {
  const env = setUp({ existing: { "example.org": certificate(25, ["example.org", "app.example.org"]) } });
  try {
    const result = env.run("example.org");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls(), ["/opt/helpers/aetheus-certbot-manage renew example.org"]);
  } finally { env.cleanup(); }
});

test("a valid certificate is left alone", () => {
  const env = setUp({ existing: { "docs.example.org": certificate(80, ["docs.example.org"]) } });
  try {
    const result = env.run("docs.example.org");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});

// R-08: `certbot renew` replays the plugin a certificate was issued with, and the apache one was
// wedged on the production host. A failed renewal is issued again through the web-root helper with
// exactly the names the certificate carries, which renews the same lineage in place.
test("a failed renewal is issued again with the names the certificate carries", () => {
  const env = setUp({ existing: { "example.org": certificate(12, ["example.org", "app.example.org"]) }, renew: "fail" });
  try {
    const result = env.run("example.org");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls(), [
      "/opt/helpers/aetheus-certbot-manage renew example.org",
      "/opt/helpers/aetheus-certbot-issue example.org"
    ]);
  } finally { env.cleanup(); }
});

test("a renewal that did not happen, even issued again, fails the run", () => {
  const env = setUp({ existing: { "example.org": certificate(12, ["example.org"]) }, renew: "fail", issue: "fail" });
  try {
    const result = env.run("example.org");
    assert.equal(result.status, 1);
    assert.match(result.stderr, /example\.org: expires in 1[12] days, fewer than 30/);
  } finally { env.cleanup(); }
});

test("an alias the certificate does not name yet re-issues it with both names", () => {
  const env = setUp({ existing: { "api.example.org": certificate(80, ["api.example.org"]) } });
  try {
    const result = env.run("api.example.org,old-api.example.org");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls(), ["/opt/helpers/aetheus-certbot-issue api.example.org"]);
  } finally { env.cleanup(); }
});

test("one failed issuance does not stop the other hosts, and is named in the verdict", () => {
  const env = setUp({
    existing: { "example.org": certificate(20, ["example.org"]) },
    issue: "fail"
  });
  try {
    const result = env.run("app.example.org", "example.org");
    assert.equal(result.status, 1);
    assert.deepEqual(env.calls(), [
      "/opt/helpers/aetheus-certbot-issue app.example.org",
      "/opt/helpers/aetheus-certbot-manage renew example.org"
    ]);
    assert.match(result.stderr, /app\.example\.org: no certificate/);
    assert.doesNotMatch(result.stderr, /example\.org: expires/);
  } finally { env.cleanup(); }
});

test("a value that is not a hostname is refused before anything is asked", () => {
  const env = setUp();
  try {
    const result = env.run("app.example.org;reboot");
    assert.equal(result.status, 1);
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});
