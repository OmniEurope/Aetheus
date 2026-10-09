// SPDX-License-Identifier: EUPL-1.2
// install-agent-linux.sh runs as root on every agent host, so it is never run whole here. Its own
// functions and its own argument parser are cut out of the script, the way
// verify-agent-posture-idempotence.sh does it, and run with stand-ins for every system command
// (id, getent, gpasswd, mkdir, the template renderer): what they decide is observed on the code
// that ships, and nothing touches the machine running the tests.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const installer = readFileSync(resolve("deploy/scripts/install-agent-linux.sh"), "utf8").replace(/\r\n/g, "\n");

function extractFunction(name) {
  const start = installer.indexOf(`\n${name}() {\n`);
  assert.ok(start >= 0, `${name}() is defined in the installer`);
  const end = installer.indexOf("\n}\n", start + 1);
  return installer.slice(start + 1, end + 3);
}

// The flag defaults, the argument parser and the module expansion, as the installer runs them.
function extractParser() {
  const start = installer.indexOf("# Elevation posture - all DISABLED by default");
  const end = installer.indexOf("# R-249: the ACME web root");
  assert.ok(start >= 0 && end > start, "the parser block is found");
  return installer.slice(start, end);
}

const stubs = `
log_info() { printf 'INFO %s\\n' "$1"; }
log_warn() { printf 'WARN %s\\n' "$1"; }
log_error() { printf 'ERROR %s\\n' "$1"; }
show_help() { :; }
`;

function run(body) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-installer-")).replace(/\\/g, "/");
  try {
    const script = `${root}/scenario.sh`;
    writeFileSync(script, `ROOT="${root}"\n${body}\n`);
    const result = spawnSync("sh", [script], { encoding: "utf8" });
    return { ...result, root };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const values = output => Object.fromEntries(
  output.split("\n").filter(line => /^[A-Z_]+=/.test(line)).map(line => [line.slice(0, line.indexOf("=")), line.slice(line.indexOf("=") + 1)]));

const capabilities = [
  "SERVICE_CONTROL", "APACHE", "APACHE_MANAGE", "RKHUNTER_MANAGE", "CERTBOT", "CERTBOT_MANAGE", "DOCKER", "TEAMSPEAK",
  "CRON_MANAGE", "PORTSENTRY_MANAGE", "SERVICE_ENABLE", "PACKAGE_MANAGE", "PATCH_MANAGE", "FIREWALL_MANAGE",
  "MAIL_SETUP", "TEAMSPEAK_SETUP", "DEPLOYMENT"
];
const witnesses = {
  SUDOERS_FILE: "SERVICE_CONTROL", APACHE_MANAGE_SUDOERS_FILE: "APACHE_MANAGE", CERTBOT_MANAGE_SUDOERS_FILE: "CERTBOT_MANAGE",
  RKHUNTER_MANAGE_SUDOERS_FILE: "RKHUNTER_MANAGE", CRON_MANAGE_SUDOERS_FILE: "CRON_MANAGE",
  PORTSENTRY_MANAGE_SUDOERS_FILE: "PORTSENTRY_MANAGE", SERVICE_ENABLE_SUDOERS_FILE: "SERVICE_ENABLE",
  PACKAGE_MANAGE_SUDOERS_FILE: "PACKAGE_MANAGE", PATCH_MANAGE_SUDOERS_FILE: "PATCH_MANAGE",
  FIREWALL_MANAGE_SUDOERS_FILE: "FIREWALL_MANAGE", MAIL_MANAGE_SUDOERS_FILE: "MAIL_SETUP",
  TEAMSPEAK_SETUP_SUDOERS_FILE: "TEAMSPEAK_SETUP", DEPLOY_MANAGE_SUDOERS_FILE: "DEPLOYMENT"
};

// ---- DEP-021: a flag on an upgrade changes what it names, the rest of the host's posture stays ----

// The host holds the drop-ins of `present`; the upgrade command passes `flags`.
function upgrade(present, flags) {
  const quoted = flags.map(flag => `'${flag}'`).join(" ");
  const body = `${stubs}
${Object.keys(witnesses).map(name => `${name}="$ROOT/${name}"`).join("\n")}
INSTALL_DIR="$ROOT/opt"; AGENT_USER="aetheus-agent"
id() { return 1; }
${present.map(capability => `printf 'x' > "$ROOT/${Object.keys(witnesses).find(name => witnesses[name] === capability)}"`).join("\n")}
set -- --upgrade ${quoted}
${extractParser()}
${extractFunction("preserve")}
${extractFunction("derive_existing_posture")}
derive_existing_posture
${capabilities.map(capability => `echo "${capability}=$ENABLE_${capability}"`).join("\n")}
`;
  const result = run(body);
  assert.equal(result.status, 0, result.stderr);
  return values(result.stdout);
}

const established = ["SERVICE_CONTROL", "APACHE_MANAGE", "CERTBOT_MANAGE", "CRON_MANAGE", "DEPLOYMENT", "PATCH_MANAGE"];

test("an upgrade without flags keeps every capability the host has", () => {
  const posture = upgrade(established, []);
  for (const capability of [...established, "APACHE", "CERTBOT"]) assert.equal(posture[capability], "1", capability);
  assert.equal(posture.FIREWALL_MANAGE, "0");
});

test("one --disable flag revokes that capability and keeps the others", () => {
  const posture = upgrade(established, ["--disable-cron-manage"]);
  assert.equal(posture.CRON_MANAGE, "0");
  for (const capability of ["SERVICE_CONTROL", "APACHE_MANAGE", "APACHE", "CERTBOT_MANAGE", "CERTBOT", "DEPLOYMENT", "PATCH_MANAGE"]) {
    assert.equal(posture[capability], "1", `${capability} survives --disable-cron-manage`);
  }
});

test("one --enable flag adds that capability and keeps the others", () => {
  const posture = upgrade(established, ["--enable-firewall-manage"]);
  assert.equal(posture.FIREWALL_MANAGE, "1");
  for (const capability of established) assert.equal(posture[capability], "1", capability);
  assert.equal(posture.MAIL_SETUP, "0", "nothing the host does not have is added");
});

test("the server-management module keeps the capabilities outside it", () => {
  const posture = upgrade(["PATCH_MANAGE", "FIREWALL_MANAGE", "DEPLOYMENT", "CERTBOT_MANAGE"], ["--module", "server-management"]);
  for (const capability of ["PATCH_MANAGE", "FIREWALL_MANAGE", "DEPLOYMENT", "CERTBOT_MANAGE", "SERVICE_CONTROL", "CRON_MANAGE"]) {
    assert.equal(posture[capability], "1", capability);
  }
});

test("--disable-all still revokes everything, deployment included", () => {
  const posture = upgrade(Object.values(witnesses), ["--disable-all"]);
  for (const capability of capabilities) assert.equal(posture[capability], "0", capability);
});

test("a later flag on the same command wins over an earlier one", () => {
  const posture = upgrade(established, ["--disable-all", "--enable-cron-manage"]);
  assert.equal(posture.CRON_MANAGE, "1");
  assert.equal(posture.DEPLOYMENT, "0");
  assert.equal(posture.APACHE_MANAGE, "0");
});

// ---- DEP-018: the agent unit's sandbox follows every grant the installer puts in place ------------

function unitFor(enabled) {
  const body = `${stubs}
${capabilities.map(capability => `ENABLE_${capability}=${enabled.includes(capability) ? 1 : 0}`).join("\n")}
INSTALL_DIR="$ROOT/opt"; SYSTEMD_UNIT_PATH="$ROOT/unit"; CERTBOT_STATE_DIR="$ROOT/certbot"; ACME_WEBROOT_OVERRIDE=""
mkdir() { :; }
render_host_config() {
    printf 'NNP=%s\\n' "$(printf '%s' "$NNP_BLOCK" | grep -o 'NoNewPrivileges=[a-z]*')"
    printf 'SUID=%s\\n' "$(printf '%s' "$SANDBOX_PRIV_BLOCK" | grep -o 'RestrictSUIDSGID=[a-z]*')"
    printf 'SYSTEM=%s\\n' "$(printf '%s' "$PROTECT_SYSTEM_BLOCK" | grep -o 'ProtectSystem=[a-z]*')"
    printf 'PATHS=%s\\n' "$READ_WRITE_PATHS_BLOCK"
    printf 'FAMILIES=%s\\n' "$ADDRESS_FAMILIES"
}
${extractFunction("sudo_grant_enabled")}
${extractFunction("write_systemd_unit")}
write_systemd_unit
`;
  const result = run(body);
  assert.equal(result.status, 0, result.stderr);
  return values(result.stdout);
}

test("an agent with no grant keeps the full sandbox", () => {
  const unit = unitFor([]);
  assert.equal(unit.NNP, "NoNewPrivileges=true");
  assert.equal(unit.SUID, "RestrictSUIDSGID=true");
  assert.equal(unit.SYSTEM, "ProtectSystem=strict");
});

for (const capability of ["SERVICE_CONTROL", "APACHE_MANAGE", "CERTBOT_MANAGE", "RKHUNTER_MANAGE", "CRON_MANAGE", "PORTSENTRY_MANAGE",
  "SERVICE_ENABLE", "PACKAGE_MANAGE", "PATCH_MANAGE", "FIREWALL_MANAGE", "MAIL_SETUP", "TEAMSPEAK_SETUP", "DEPLOYMENT"]) {
  test(`the ${capability.toLowerCase()} grant alone lets sudo elevate`, () => {
    const unit = unitFor([capability]);
    assert.equal(unit.NNP, "NoNewPrivileges=false");
    assert.equal(unit.SUID, "RestrictSUIDSGID=false");
  });
}

test("the grants of apply_sudoers and the sandbox list are the same set", () => {
  const apply = extractFunction("apply_sudoers");
  const granted = new Set([...apply.matchAll(/if \[ "\$ENABLE_([A-Z_]+)" -eq 1 \]/g)].map(match => match[1]));
  const relaxed = new Set([...extractFunction("sudo_grant_enabled").matchAll(/"\$ENABLE_([A-Z_]+)"/g)].map(match => match[1]));
  assert.deepEqual([...relaxed].sort(), [...granted].sort());
});

test("cron and rkhunter keep ProtectSystem=strict with only their own paths writable", () => {
  const cron = unitFor(["CRON_MANAGE"]);
  assert.equal(cron.SYSTEM, "ProtectSystem=strict");
  assert.equal(cron.PATHS.trim(), "/etc/cron.d");
  const rkhunter = unitFor(["RKHUNTER_MANAGE"]);
  assert.equal(rkhunter.SYSTEM, "ProtectSystem=strict");
  assert.equal(rkhunter.PATHS.trim(), "-/var/lib/rkhunter -/var/log");
});

test("the portsentry and TeamSpeak setup helpers get the system writes apt-get needs", () => {
  const portsentry = unitFor(["PORTSENTRY_MANAGE"]);
  assert.equal(portsentry.SYSTEM, "ProtectSystem=false");
  assert.match(portsentry.FAMILIES, /\bAF_NETLINK\b/, "iptables-nft talks netlink");
  assert.equal(unitFor(["TEAMSPEAK_SETUP"]).SYSTEM, "ProtectSystem=false");
  assert.doesNotMatch(unitFor(["CRON_MANAGE"]).FAMILIES, /AF_NETLINK/);
});

// ---- DEP-019: a disabled docker capability takes the dedicated account out of the docker group ----

function docker({ flags, passwd = "aetheus-agent:x:998:998::/var/lib/aetheus-agent:/bin/false", gpasswdStatus = 0 }) {
  const quoted = flags.map(flag => `'${flag}'`).join(" ");
  const body = `${stubs}
AGENT_USER="aetheus-agent"; WORK_DIR="/var/lib/aetheus-agent"
id() { [ -f "$ROOT/removed" ] && echo "aetheus-agent" || echo "aetheus-agent docker"; }
getent() { echo "${passwd}"; }
gpasswd() { echo "CALL gpasswd $*" >> "$ROOT/calls"; [ ${gpasswdStatus} -eq 0 ] && : > "$ROOT/removed"; return ${gpasswdStatus}; }
docker() { return 0; }
set -- --upgrade ${quoted}
${extractParser()}
${extractFunction("revoke_docker_group")}
${extractFunction("ensure_docker_group")}
ensure_docker_group
echo "STATUS=$?"; cat "$ROOT/calls" 2>/dev/null || true
`;
  const result = run(body);
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}

for (const flag of ["--disable-all", "--disable-docker"]) {
  test(`${flag} removes the dedicated agent account from the docker group`, () => {
    const output = docker({ flags: [flag] });
    assert.match(output, /CALL gpasswd -d aetheus-agent docker/);
  });
}

test("an account that is not the dedicated agent account keeps its membership", () => {
  const output = docker({ flags: ["--disable-all"], passwd: "aetheus-agent:x:1000:1000:Ops:/home/ops:/bin/bash" });
  assert.doesNotMatch(output, /CALL gpasswd/);
  assert.match(output, /WARN .*not the dedicated agent account.*DECISION REQUIRED/);
});

test("an upgrade that does not name docker leaves the membership alone", () => {
  const output = docker({ flags: ["--disable-cron-manage"] });
  assert.doesNotMatch(output, /CALL gpasswd/);
});

test("a failed removal is reported, not hidden", () => {
  const output = docker({ flags: ["--disable-all"], gpasswdStatus: 3 });
  assert.match(output, /CALL gpasswd -d aetheus-agent docker/);
  assert.match(output, /ERROR .*Could not remove aetheus-agent from the docker group/);
});

// ---- DEP-020: a reinstall clears the agent's state and keeps the deployed applications ------------

test("a reinstall purges agent state and keeps the deployment tree byte for byte", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-workdir-")).replace(/\\/g, "/");
  try {
    const work = `${root}/var/lib/aetheus-agent`;
    const files = {
      "deploy/shop/releases/41/app.dll": "release 41",
      "deploy/shop/releases/42/app.dll": "release 42",
      "deploy/shop/current": "42",
      "deploy/shop/secrets/.env": "DB_PASSWORD='kept'",
      "keys/agent.key": "enrollment key",
      "w/1/s/build.log": "workspace",
      "agent.db": "offline queue",
      ".installed-at": "2026-10-01",
      ".agent-update/staged": "update"
    };
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(join(work, path, ".."), { recursive: true });
      writeFileSync(join(work, path), content);
    }
    const body = `${stubs}
WORK_DIR="${work}"; DEPLOY_BASE_DIR="$WORK_DIR/deploy"
${extractFunction("purge_agent_work_dir")}
purge_agent_work_dir
`;
    const result = spawnSync("sh", ["-c", body], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(readdirSync(work), ["deploy"]);
    for (const [path, content] of Object.entries(files).filter(([path]) => path.startsWith("deploy/"))) {
      assert.equal(readFileSync(join(work, path), "utf8"), content, path);
    }
    assert.ok(lstatSync(work).isDirectory(), "the work dir itself stays");
    assert.ok(!existsSync(join(work, "keys")));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the reinstall calls the purge, not a wipe of the whole work dir", () => {
  const cleanup = installer.slice(installer.indexOf("# --- Clean any previous installation ---"));
  assert.match(cleanup, /\npurge_agent_work_dir\n/);
  assert.doesNotMatch(cleanup.slice(0, cleanup.indexOf("# --- Move files to INSTALL_DIR")), /rm -rf "\$\{WORK_DIR:\?\}"\/\*/);
});
