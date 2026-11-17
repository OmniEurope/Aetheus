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

const installer = readFileSync(resolve("deploy/agent-installer/install-agent-linux.sh"), "utf8").replace(/\r\n/g, "\n");

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

// Change only the capability named by an upgrade flag so the rest of the installed posture remains
// intact. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

// The host holds the drop-ins of `present`; the upgrade command passes `flags`.
function upgrade(present, flags) {
  const quoted = flags.map(flag => `'${flag}'`).join(" ");
  const body = `${stubs}
${Object.keys(witnesses).map(name => `${name}="$ROOT/${name}"`).join("\n")}
INSTALL_DIR="$ROOT/opt"; AGENT_USER="aetheus-agent"
id() { return 1; }
getfacl() { return 1; }
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

// R3-58 / ADR-051 / ADR-055: the agent update supervisor runs `--upgrade --yes --enable-<component>`, one of the
// eleven grants the UI may add. Each must add that capability (plus what the installer itself implies for
// it) and widen nothing else the host has not got.
const grantFlags = [
  ["patch-manage", "PATCH_MANAGE", []], ["package-manage", "PACKAGE_MANAGE", []], ["service-enable", "SERVICE_ENABLE", []],
  ["cron-manage", "CRON_MANAGE", []], ["rkhunter-manage", "RKHUNTER_MANAGE", []], ["apache-manage", "APACHE_MANAGE", ["APACHE"]],
  ["certbot-manage", "CERTBOT_MANAGE", ["CERTBOT", "APACHE_MANAGE", "APACHE"]], ["portsentry-manage", "PORTSENTRY_MANAGE", []],
  ["firewall-manage", "FIREWALL_MANAGE", []], ["mail-setup", "MAIL_SETUP", []], ["teamspeak-setup", "TEAMSPEAK_SETUP", ["TEAMSPEAK"]]
];
for (const [flag, capability, implied] of grantFlags) {
  test(`--upgrade --enable-${flag} adds ${flag} alone`, () => {
    const posture = upgrade(["SERVICE_CONTROL", "DEPLOYMENT"], ["--yes", `--enable-${flag}`]);
    assert.equal(posture[capability], "1");
    for (const kept of ["SERVICE_CONTROL", "DEPLOYMENT"]) assert.equal(posture[kept], "1", kept);
    for (const other of capabilities.filter(name => ![capability, ...implied, "SERVICE_CONTROL", "DEPLOYMENT"].includes(name))) {
      assert.equal(posture[other], "0", `${other} stays off`);
    }
  });
}

// The supervisor's sixth request field, run as shipped: only the exact whitelisted token maps to the
// installer flag; anything else ends the update with exit 20 before the host is touched, and the value is
// only ever data (the field is passed through the environment, never into the script text).
const supervisor = readFileSync(resolve("deploy/agent-host-config/agent-update/agent-posture-upgrade"), "utf8").replace(/\r\n/g, "\n");

// ADR-055: the ten tokens added to patch-manage, each with the one installer flag the supervisor may append.
const acceptedTokens = [
  "patch-manage", "package-manage", "service-enable", "cron-manage", "rkhunter-manage", "apache-manage",
  "certbot-manage", "portsentry-manage", "firewall-manage", "mail-setup", "teamspeak-setup"
];

function capabilityField(value) {
  const start = supervisor.indexOf("# Optional sixth field");
  const end = supervisor.indexOf("# Let the running agent publish the handoff result");
  assert.ok(start >= 0 && end > start, "the capability block is found in the supervisor");
  const root = mkdtempSync(join(tmpdir(), "aetheus-supervisor-")).replace(/\\/g, "/");
  try {
    const script = `${root}/scenario.sh`;
    // The installer argv block, run as shipped up to (not including) the installer call itself.
    const argvStart = supervisor.indexOf("# Each accepted token adds its one literal installer flag");
    const argvEnd = supervisor.indexOf('sh "$payload/install-agent-linux.sh" "$@"');
    assert.ok(argvStart > end && argvEnd > argvStart, "the installer argv block is found in the supervisor");
    const variables = acceptedTokens.map(token => `enable_${token.replace(/-/g, "_")}=0`).join("\n");
    writeFileSync(script, `set -eu\ncd "${root}"\nphase="claim"\n${variables}\nenable_capabilities="$CAPS"\ntrap 'echo "PHASE=$phase"' EXIT\n${supervisor.slice(start, end)}\n${supervisor.slice(argvStart, argvEnd)}\nfor arg in "$@"; do echo "ARG=$arg"; done\n`);
    const result = spawnSync("sh", [script], { encoding: "utf8", env: { ...process.env, CAPS: value } });
    const argv = result.stdout.split("\n").filter(line => line.startsWith("ARG=")).map(line => line.slice(4));
    return { status: result.status, ...values(result.stdout), argv, sideEffects: readdirSync(root).filter(name => name !== "scenario.sh") };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

for (const token of acceptedTokens) {
  test(`the supervisor maps ${token} to its one installer flag and nothing else`, () => {
    const result = capabilityField(token);
    assert.equal(result.status, 0);
    assert.deepEqual(result.argv, ["--upgrade", "--yes", `--enable-${token}`]);
    assert.deepEqual(result.sideEffects, [], "nothing was executed or created");
  });
}

test("the supervisor appends one flag per token of a multi-token request, in the order of the supervisor", () => {
  const result = capabilityField("mail-setup,patch-manage,cron-manage");
  assert.equal(result.status, 0);
  assert.deepEqual(result.argv, ["--upgrade", "--yes", "--enable-patch-manage", "--enable-cron-manage", "--enable-mail-setup"]);
});

test("the supervisor takes no capability when the field is empty", () => {
  const result = capabilityField("");
  assert.equal(result.status, 0);
  assert.deepEqual(result.argv, ["--upgrade", "--yes"]);
});

for (const hostile of ["docker", "service-control", "deployment", "enable-all", "FIREWALL-MANAGE", "patch-manage,docker", "docker,patch-manage", "firewall-manage,docker", "patch-manage;touch pwned", "$(touch pwned)", "`touch pwned`", "patch-manage touch", "patch-manage,", "--enable-all", "--module", "--disable-patch-manage", "patch-manage --enable-docker"]) {
  test(`the supervisor refuses ${JSON.stringify(hostile)} before touching the host`, () => {
    const result = capabilityField(hostile);
    assert.equal(result.status, 20);
    assert.equal(result.PHASE, "invalid-capabilities");
    assert.deepEqual(result.argv, [], "no installer argument is ever built from a refused request");
    assert.deepEqual(result.sideEffects, [], "nothing was executed or created");
  });
}

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

// Derive the service sandbox from every installed grant so intended elevation is usable without
// granting unrelated privileges. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

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

// The homes rule of the unit as write_systemd_unit renders it, with a systemctl that reports `version`
// (any text, as `systemctl --version` prints it) and a read-grant helper that lists `dirs`.
function homeRuleFor(version, { dirs = ["/root/.ssh", "/home/alice/.ssh"], certbot = false } = {}) {
  const body = `${stubs}
${capabilities.map(capability => `ENABLE_${capability}=${certbot && capability === "CERTBOT_MANAGE" ? 1 : 0}`).join("\n")}
INSTALL_DIR="$ROOT/opt"; SYSTEMD_UNIT_PATH="$ROOT/unit"; CERTBOT_STATE_DIR="$ROOT/certbot"; ACME_WEBROOT_OVERRIDE=""
AGENT_READ_GRANT_HELPER_PATH="$ROOT/helper"; AGENT_READ_GRANT_SERVICE_PATH="$ROOT/grant.service"
printf '#!/bin/sh\\n%s\\n' '${dirs.map(dir => `echo ${dir}`).join("; ")}' > "$ROOT/helper"; chmod +x "$ROOT/helper"
mkdir() { :; }
chown() { :; }
getent() { return 1; }
ensure_helper_dir() { :; }
systemctl() { echo '${version}'; }
render_host_config() {
    case "$1" in agent/aetheus-agent.service) printf 'HOME=%s\\n' "$(printf '%s' "$PROTECT_HOME_BLOCK" | tr '\\n' '|')" ;; esac
}
${extractFunction("sudo_grant_enabled")}
${extractFunction("systemd_major_version")}
${extractFunction("write_systemd_unit")}
write_systemd_unit
`;
  const result = run(body);
  assert.equal(result.status, 0, result.stderr);
  return { home: values(result.stdout).HOME, output: result.stdout };
}

test("systemd 242 and later turn the homes into tmpfs and bind the .ssh directories read-only", () => {
  for (const version of ["systemd 242 (242.0)", "systemd 245 (245.4-4ubuntu3)", "systemd 255 (255.4-1ubuntu8.18)"]) {
    assert.equal(homeRuleFor(version).home, "ProtectHome=tmpfs|BindReadOnlyPaths=-/root/.ssh -/home/alice/.ssh", version);
  }
});

test("an older or unreadable systemd keeps ProtectHome=true and binds nothing", () => {
  // tmpfs is unknown below 242: systemd would ignore the line and leave every home readable.
  for (const version of ["systemd 241 (241)", "systemd 219", "systemd 237 (237)", "garbage", ""]) {
    const rule = homeRuleFor(version);
    assert.equal(rule.home, "ProtectHome=true", `[${version}]`);
    assert.match(rule.output, /WARN systemd older than 242/);
  }
});

test("the certbot homes keep their snap bind and gain the .ssh binds only from systemd 242", () => {
  assert.equal(homeRuleFor("systemd 255", { certbot: true }).home,
    "ProtectHome=tmpfs|BindPaths=-/root/snap|BindReadOnlyPaths=-/root/.ssh -/home/alice/.ssh");
  assert.doesNotMatch(homeRuleFor("systemd 241", { certbot: true }).home, /BindReadOnlyPaths/);
});

test("certbot homes need systemd 242 for the tmpfs and the snap bind", () => {
  const recent = homeRuleFor("systemd 255 (255.4-1ubuntu8.18)", { certbot: true, dirs: [] });
  assert.equal(recent.home, "ProtectHome=tmpfs|BindPaths=-/root/snap");
  for (const version of ["systemd 241 (241)", "garbage", ""]) {
    const old = homeRuleFor(version, { certbot: true });
    assert.equal(old.home, "ProtectHome=true", `[${version}]`);
    assert.match(old.output, /WARN systemd older than 242 .*snap certbot/);
  }
});

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

// Require a root-owned installer marker before revoking Docker membership on a shared account so
// operator-owned membership remains untouched. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

const sharedAccount = "aetheus-agent:x:1000:1000:Ops:/home/ops:/bin/bash";

function docker({ flags, passwd = "aetheus-agent:x:998:998::/var/lib/aetheus-agent:/bin/false", gpasswdStatus = 0, member = true, marker = null }) {
  const quoted = flags.map(flag => `'${flag}'`).join(" ");
  const body = `${stubs}
AGENT_USER="aetheus-agent"; WORK_DIR="/var/lib/aetheus-agent"
DOCKER_GROUP_MARKER_FILE="$ROOT/docker-group-added"
${marker === null ? "" : `printf '%s\n' '${marker}' > "$DOCKER_GROUP_MARKER_FILE"`}
${member ? "" : ': > "$ROOT/removed"'}
id() { [ -f "$ROOT/removed" ] && echo "aetheus-agent" || echo "aetheus-agent docker"; }
getent() { echo "${passwd}"; }
gpasswd() { echo "CALL gpasswd $*" >> "$ROOT/calls"; [ ${gpasswdStatus} -eq 0 ] && : > "$ROOT/removed"; return ${gpasswdStatus}; }
usermod() { echo "CALL usermod $*" >> "$ROOT/calls"; rm -f "$ROOT/removed"; }
chown() { :; }
docker() { return 0; }
set -- --upgrade ${quoted}
${extractParser()}
${extractFunction("docker_group_added_by_installer")}
${extractFunction("record_docker_group_added")}
${extractFunction("revoke_docker_group")}
${extractFunction("ensure_docker_group")}
ensure_docker_group
echo "STATUS=$?"; cat "$ROOT/calls" 2>/dev/null || true
if [ -f "$DOCKER_GROUP_MARKER_FILE" ]; then echo "MARKER=$(cat "$DOCKER_GROUP_MARKER_FILE")"; else echo "MARKER="; fi
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

test("an account that is not the dedicated agent account keeps a membership the installer did not add", () => {
  const output = docker({ flags: ["--disable-all"], passwd: sharedAccount });
  assert.doesNotMatch(output, /CALL gpasswd/);
  assert.match(output, /WARN .*which this installer did not add.*root-equivalent/);
});

test("a membership the installer recorded is removed even from a shared account, and the marker goes", () => {
  const output = docker({ flags: ["--disable-docker"], passwd: sharedAccount, marker: "aetheus-agent" });
  assert.match(output, /CALL gpasswd -d aetheus-agent docker/);
  assert.equal(values(output).MARKER, "");
});

test("a marker written for another account does not authorize the removal", () => {
  const output = docker({ flags: ["--disable-docker"], passwd: sharedAccount, marker: "someone-else" });
  assert.doesNotMatch(output, /CALL gpasswd/);
});

test("adding the membership records the marker", () => {
  const output = docker({ flags: ["--enable-docker"], member: false });
  assert.match(output, /CALL usermod -aG docker aetheus-agent/);
  assert.equal(values(output).MARKER, "aetheus-agent");
});

test("a membership found in place is never claimed by the installer", () => {
  const output = docker({ flags: ["--enable-docker"], passwd: sharedAccount });
  assert.doesNotMatch(output, /CALL usermod/);
  assert.equal(values(output).MARKER, "");
});

test("an upgrade that does not name docker leaves the membership alone", () => {
  const output = docker({ flags: ["--disable-cron-manage"] });
  assert.doesNotMatch(output, /CALL gpasswd/);
});

test("a failed removal is reported, not hidden, and keeps the marker", () => {
  const output = docker({ flags: ["--disable-all"], gpasswdStatus: 3, marker: "aetheus-agent" });
  assert.match(output, /CALL gpasswd -d aetheus-agent docker/);
  assert.match(output, /ERROR .*Could not remove aetheus-agent from the docker group/);
  assert.equal(values(output).MARKER, "aetheus-agent");
});

test("the purge withdraws only a docker membership the installer recorded", () => {
  const purge = installer.slice(installer.indexOf('if [ "$MODE" = "purge" ]; then'), installer.indexOf("# Mode: UPGRADE"));
  assert.match(purge, /if docker_group_added_by_installer; then\n\s+gpasswd -d "\$AGENT_USER" docker/);
  assert.doesNotMatch(purge.replace(/if docker_group_added_by_installer; then\n\s+gpasswd -d "\$AGENT_USER" docker/, ""), /gpasswd -d "\$AGENT_USER" docker/);
  assert.match(purge, /rm -f "\$DOCKER_GROUP_MARKER_FILE"/);
});

// Clear agent state on reinstall while preserving deployed applications and their secrets. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

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

test("a reinstall keeps the project state directories byte for byte (R-97)", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-workdir-")).replace(/\\/g, "/");
  try {
    const work = `${root}/var/lib/aetheus-agent`;
    const kept = {
      "projects/w9-production/live-color": "green",
      "projects/w9-production/source-commit": "abc123",
      "projects/w9-production/deployment-transaction/state": "committed",
      "projects/w9-production/reserve/blue.tar": "reserve",
      "projects/shop-production/live-color": "blue"
    };
    const files = { ...kept, "keys/agent.key": "enrollment key", "w/1/s/build.log": "workspace", "projectsx/stray": "agent state" };
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
    assert.deepEqual(readdirSync(work), ["projects"]);
    for (const [path, content] of Object.entries(kept)) {
      assert.equal(readFileSync(join(work, path), "utf8"), content, path);
    }
    assert.match(result.stdout, /Keeping the project state directories/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the reinstall calls the purge, not a wipe of the whole work dir", () => {
  const cleanup = installer.slice(installer.indexOf("# --- Clean any previous installation ---"));
  assert.match(cleanup, /\npurge_agent_work_dir\n/);
  assert.doesNotMatch(cleanup.slice(0, cleanup.indexOf("# --- Move files to INSTALL_DIR")), /rm -rf "\$\{WORK_DIR:\?\}"\/\*/);
});

// aetheus-fn (PLAN-002 lot 1.0g) is bind-mounted read-only into a step container that runs as a
// non-root user: each per-C-library binary must end up 755, on install and on upgrade alike.

test("the installer gives every aetheus-fn binary mode 755 and keeps the agent apphost executable", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-modes-")).replace(/\\/g, "/");
  try {
    const install = `${root}/opt/aetheus-agent`;
    for (const path of ["Aetheus.Agent.Linux", "aetheus-fn/linux-x64/aetheus-fn", "aetheus-fn/linux-musl-x64/aetheus-fn"]) {
      mkdirSync(join(install, path, ".."), { recursive: true });
      writeFileSync(join(install, path), "binary");
    }
    const body = `${stubs}
chmod() { printf 'CHMOD %s %s\\n' "$1" "$2"; }
INSTALL_DIR="${install}"
${extractFunction("restore_executable_modes")}
restore_executable_modes
`;
    const result = spawnSync("sh", ["-c", body], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    const calls = result.stdout.split("\n").filter(line => line.startsWith("CHMOD ")).sort();
    assert.deepEqual(calls, [
      `CHMOD +x ${install}/Aetheus.Agent.Linux`,
      `CHMOD 755 ${install}/aetheus-fn/linux-musl-x64/aetheus-fn`,
      `CHMOD 755 ${install}/aetheus-fn/linux-x64/aetheus-fn`
    ].sort());
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an archive without aetheus-fn installs without a chmod on a missing file", () => {
  const root = mkdtempSync(join(tmpdir(), "aetheus-modes-")).replace(/\\/g, "/");
  try {
    const body = `${stubs}
chmod() { printf 'CHMOD %s %s\\n' "$1" "$2"; }
INSTALL_DIR="${root}"
${extractFunction("restore_executable_modes")}
restore_executable_modes
`;
    const result = spawnSync("sh", ["-c", body], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(result.stdout, /CHMOD/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("both the install and the upgrade path restore the executable modes", () => {
  const upgrade = installer.slice(installer.indexOf("# Mode: UPGRADE"), installer.indexOf("# --- Create directories ---"));
  const install = installer.slice(installer.indexOf("# --- Create directories ---"), installer.indexOf("# --- Write configuration ---"));
  assert.match(upgrade, /\n\s+restore_executable_modes\n/);
  assert.match(install, /\nrestore_executable_modes\n/);
});

// Rollback trap: a failed upgrade restores the previous agent binaries, and the posture version file the
// failed install wrote must go back with them, or the older agent (which expects the old version) would
// refuse every later UI update. The shipped restore_previous_agent runs on a scratch tree with stand-ins
// for the three commands that need root (chown, systemctl, the service itself).
function restoreScenario({ previousPosture, writtenPosture }) {
  const start = supervisor.indexOf("restore_previous_agent() {\n");
  const end = supervisor.indexOf("\n}\n", start) + 3;
  assert.ok(start >= 0 && end > start, "restore_previous_agent() is defined in the supervisor");
  const root = mkdtempSync(join(tmpdir(), "aetheus-rollback-")).replace(/[\\]/g, "/");
  try {
    for (const dir of ["install", "work", "upgrade", "upgrade/rollback"]) mkdirSync(`${root}/${dir}`, { recursive: true });
    writeFileSync(`${root}/install/Aetheus.Agent.Linux`, "new binary");
    writeFileSync(`${root}/upgrade/rollback/Aetheus.Agent.Linux`, "old binary");
    writeFileSync(`${root}/etc-posture`, writtenPosture);
    if (previousPosture === null) writeFileSync(`${root}/upgrade/posture.absent`, "");
    else writeFileSync(`${root}/upgrade/posture.previous`, previousPosture);
    const script = `${root}/scenario.sh`;
    writeFileSync(script, `set -eu
chown() { :; }
systemctl() { :; }
install_dir="${root}/install"
work_dir="${root}/work"
upgrade_dir="${root}/upgrade"
rollback="${root}/upgrade/rollback"
posture_file="${root}/etc-posture"
rollback_ready=1
${supervisor.slice(start, end)}
restore_previous_agent
`);
    const result = spawnSync("sh", [script], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr);
    return {
      binary: readFileSync(`${root}/install/Aetheus.Agent.Linux`, "utf8"),
      posture: existsSync(`${root}/etc-posture`) ? readFileSync(`${root}/etc-posture`, "utf8") : null
    };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a rollback puts the previous posture version back with the previous agent", () => {
  const result = restoreScenario({ previousPosture: "2\n", writtenPosture: "3\n" });
  assert.equal(result.binary, "old binary");
  assert.equal(result.posture, "2\n");
});

test("a rollback removes a posture file the failed install created on a host that had none", () => {
  const result = restoreScenario({ previousPosture: null, writtenPosture: "3\n" });
  assert.equal(result.binary, "old binary");
  assert.equal(result.posture, null);
});

// R-19: the Apache introspection grants read access to the Apache logs by the same read ACL as /etc/apache2,
// on the install and the upgrade path (both call apply_read_acls), and nothing wider (no write, no adm group).
function readAclCalls({ apache }) {
  const functions = (extractFunction("grant_read_acl") + extractFunction("revoke_agent_acl") + extractFunction("apply_read_acls"))
    .replace(/ \/(etc|var)\//g, ' "$ROOT"/$1/');
  const result = run(`${stubs}
AGENT_USER="aetheus-agent"; ENABLE_APACHE=${apache}; ENABLE_APACHE_MANAGE=0; ENABLE_CERTBOT=0
mkdir -p "$ROOT/var/log/apache2" "$ROOT/etc/apache2"
setfacl() { printf 'SETFACL %s\\n' "$*" | sed "s#$ROOT##g"; }
usermod() { printf 'USERMOD %s\\n' "$*"; }
gpasswd() { printf 'GPASSWD %s\\n' "$*"; }
${functions}
apply_read_acls
`);
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.split("\n").filter(line => /^(SETFACL|USERMOD|GPASSWD) /.test(line));
}

test("the Apache introspection grants read on the Apache log directory, files and future files", () => {
  const calls = readAclCalls({ apache: 1 });
  assert.ok(calls.includes("SETFACL -R -m u:aetheus-agent:rX /var/log/apache2"), calls.join("\n"));
  assert.ok(calls.includes("SETFACL -R -d -m u:aetheus-agent:rX /var/log/apache2"), calls.join("\n"));
  assert.ok(calls.includes("SETFACL -R -m u:aetheus-agent:rX /etc/apache2"), calls.join("\n"));
  for (const call of calls) {
    assert.match(call, /^SETFACL /, "no group membership is changed");
    assert.doesNotMatch(call, /:[rwxX]*w[rwxX]*( |$)/, `no write permission: ${call}`);
  }
  assert.ok(!calls.some(call => call.includes("/var/log/httpd")), "a missing directory is skipped");
});

test("without the Apache introspection the agent gets no Apache log access", () => {
  assert.deepEqual(readAclCalls({ apache: 0 }), []);
});

test("both the install and the upgrade path apply the read ACLs", () => {
  const calls = installer.split("\n").filter(line => /^\s*apply_read_acls\s*$/.test(line));
  assert.equal(calls.length, 2);
});

// R-19 review: disabling an introspection option, and the purge, take back exactly the agent user's entries
// the installer granted (access and default, recursively), never another user's, never a mode or an owner.
// The host ACLs live in "$ROOT/acls" as "<path> <entry>" lines that the getfacl/setfacl stand-ins read and
// edit with the setfacl semantics the installer relies on (-x names one entry, -R covers the subtree, -d
// targets the default entries).
const aclHost = `
getfacl() { _p=""; for _a in "$@"; do _p="$_a"; done; awk -v p="$_p" '$1 == p { print $2 }' "$ROOT/acls"; }
setfacl() {
    printf 'SETFACL %s\\n' "$*" | sed "s#$ROOT##g"
    _def=""; _op=""; _spec=""
    while [ $# -gt 1 ]; do
        case "$1" in -d) _def="default:" ;; -x|-m) _op="$1"; _spec="$2"; shift ;; esac
        shift
    done
    [ "$_op" = "-x" ] || return 0
    _name=$(printf '%s' "$_spec" | cut -d: -f2)
    awk -v p="$1" -v e="\${_def}user:$_name:" '!(index($1, p) == 1 && index($2, e) == 1)' "$ROOT/acls" > "$ROOT/acls.new"
    mv "$ROOT/acls.new" "$ROOT/acls"
}
chmod() { printf 'CHMOD %s\\n' "$*"; }
chown() { printf 'CHOWN %s\\n' "$*"; }
`;

function aclScenario({ apache = 0, certbot = 0, granted = [], dirs = [], files = [], call = "apply_read_acls", derive = false }) {
  const functions = ["grant_read_acl", "revoke_agent_acl", "revoke_agent_acls", "apply_read_acls"]
    .map(extractFunction).join("").replace(/ \/(etc|var)\//g, ' "$ROOT"/$1/');
  // A file carries access entries only; a directory also carries the default ones.
  const entriesOf = (path, user, perms) => files.includes(path)
    ? [`"$ROOT${path}" user:${user}:${perms}`]
    : [`"$ROOT${path}" user:${user}:${perms}`, `"$ROOT${path}" default:user:${user}:${perms}`];
  const entries = [
    ...granted.flatMap(path => entriesOf(path, "aetheus-agent", "r-x")),
    ...[...dirs, ...files].flatMap(path => entriesOf(path, "www-data", "r--")),
  ];
  const derivation = derive ? `
${Object.keys(witnesses).map(name => `${name}="$ROOT/${name}"`).join("\n")}
INSTALL_DIR="$ROOT/opt"
id() { return 1; }
is_explicit() { return 1; }
ENABLE_APACHE=0; ENABLE_CERTBOT=0
${extractFunction("preserve")}
${extractFunction("derive_existing_posture").replace(/ \/(etc|var)\//g, ' "$ROOT"/$1/')}
derive_existing_posture
echo "DERIVED_APACHE=$ENABLE_APACHE"; echo "DERIVED_CERTBOT=$ENABLE_CERTBOT"` : "";
  const result = run(`${stubs}${aclHost}
AGENT_USER="aetheus-agent"; DEPLOY_WEB_ROOT="$ROOT/var/www"
ENABLE_APACHE=${apache}; ENABLE_APACHE_MANAGE=0; ENABLE_CERTBOT=${certbot}; ENABLE_CERTBOT_MANAGE=0
: > "$ROOT/acls"
${dirs.map(path => `mkdir -p "$ROOT${path}"`).join("\n")}
${files.map(path => `mkdir -p "$(dirname "$ROOT${path}")"; : > "$ROOT${path}"`).join("\n")}
${entries.map(line => `printf '%s %s\\n' ${line} >> "$ROOT/acls"`).join("\n")}
${functions}
${call}
echo "ACLS_BEGIN"; sed "s#$ROOT##g" "$ROOT/acls"; echo "ACLS_END"${derivation}
`);
  assert.equal(result.status, 0, result.stderr);
  const lines = result.stdout.split("\n");
  const acls = lines.slice(lines.indexOf("ACLS_BEGIN") + 1, lines.indexOf("ACLS_END"));
  return {
    calls: lines.filter(line => /^(SETFACL|CHMOD|CHOWN) /.test(line)),
    acls, agentEntries: acls.filter(line => line.includes("user:aetheus-agent:")),
    ...values(result.stdout),
  };
}

const apachePaths = ["/etc/apache2", "/etc/httpd", "/var/log/apache2", "/var/log/httpd"];

test("disabling the Apache introspection removes the agent entries on every path it granted", () => {
  const result = aclScenario({ granted: apachePaths, dirs: apachePaths });
  assert.deepEqual(result.agentEntries, [], result.calls.join("\n"));
  for (const path of apachePaths) {
    assert.ok(result.calls.includes(`SETFACL -R -x u:aetheus-agent ${path}`), `${path}: ${result.calls.join("\n")}`);
    assert.ok(result.calls.includes(`SETFACL -R -d -x u:aetheus-agent ${path}`), `${path}: ${result.calls.join("\n")}`);
  }
});

test("disabling the Certbot introspection removes the agent entries on /etc/letsencrypt", () => {
  const result = aclScenario({ granted: ["/etc/letsencrypt"], dirs: ["/etc/letsencrypt"] });
  assert.deepEqual(result.agentEntries, []);
  assert.ok(result.calls.includes("SETFACL -R -x u:aetheus-agent /etc/letsencrypt"), result.calls.join("\n"));
  assert.ok(result.calls.includes("SETFACL -R -d -x u:aetheus-agent /etc/letsencrypt"), result.calls.join("\n"));
});

test("the removal names only the agent user and changes no mode or owner", () => {
  const paths = [...apachePaths, "/etc/letsencrypt"];
  const result = aclScenario({ granted: paths, dirs: paths });
  for (const path of paths) {
    assert.ok(result.acls.includes(`${path} user:www-data:r--`), `${path} keeps the other user's entry`);
    assert.ok(result.acls.includes(`${path} default:user:www-data:r--`), `${path} keeps the other user's default entry`);
  }
  assert.ok(result.calls.length > 0);
  for (const call of result.calls) assert.match(call, /^SETFACL -R (-d )?-x u:aetheus-agent \//, `only the agent entries: ${call}`);
});

test("nothing is removed when the introspection was never enabled, and a missing directory is skipped", () => {
  const result = aclScenario({ dirs: ["/etc/apache2", "/var/log/apache2", "/etc/letsencrypt"] });
  assert.deepEqual(result.calls, []);
  assert.equal(result.acls.length, 6, "the other user's entries are untouched");
});

test("a second run after the removal changes nothing", () => {
  const first = aclScenario({ granted: ["/etc/apache2"], dirs: ["/etc/apache2"] });
  assert.ok(first.calls.length > 0);
  assert.deepEqual(first.agentEntries, []);
  const second = aclScenario({ dirs: ["/etc/apache2"] });
  assert.deepEqual(second.calls, []);
});

test("the next upgrade derives a disabled introspection once its ACL is removed", () => {
  const result = aclScenario({ granted: ["/etc/apache2", "/etc/letsencrypt"], dirs: ["/etc/apache2", "/etc/letsencrypt"], derive: true });
  assert.equal(result.DERIVED_APACHE, "0");
  assert.equal(result.DERIVED_CERTBOT, "0");
});

test("the purge removes the agent entries on every path the installer may have granted", () => {
  const paths = [...apachePaths, "/etc/letsencrypt", "/var/www"];
  const result = aclScenario({ apache: 1, certbot: 1, granted: [...paths, "/var/log/rkhunter.log"], dirs: paths, files: ["/var/log/rkhunter.log"], call: "revoke_agent_acls" });
  assert.deepEqual(result.agentEntries, [], result.calls.join("\n"));
  assert.ok(result.calls.includes("SETFACL -R -x u:aetheus-agent /var/log/rkhunter.log"), result.calls.join("\n"));
  assert.ok(!result.calls.includes("SETFACL -R -d -x u:aetheus-agent /var/log/rkhunter.log"), "a file has no default entries");
  for (const path of paths) assert.ok(result.acls.includes(`${path} user:www-data:r--`), `${path} keeps the other user's entry`);
});

test("the purge takes the installer's ACLs back before the agent account is deleted", () => {
  const purge = installer.slice(installer.indexOf('if [ "$MODE" = "purge" ]; then'), installer.indexOf("# Mode: UPGRADE"));
  const revoke = purge.indexOf("\n        revoke_agent_acls\n");
  assert.ok(revoke > 0, "the purge calls revoke_agent_acls");
  assert.ok(revoke < purge.indexOf('userdel "$AGENT_USER"'), "before userdel");
});

test("a --disable-apache-introspection that keeps the Apache management keeps the read ACL", () => {
  const posture = upgrade(["APACHE_MANAGE"], ["--disable-apache-introspection"]);
  assert.equal(posture.APACHE, "1");
  assert.equal(posture.APACHE_MANAGE, "1");
  assert.equal(upgrade(["APACHE_MANAGE"], ["--disable-apache-manage", "--disable-apache-introspection"]).APACHE, "0");
});

// --disable-apache-manage and --disable-rkhunter-manage run through apply_sudoers itself: each takes back what its
// --enable counterpart set up beyond the sudoers drop-in, and nothing an administrator set. The host keeps, besides
// the ACLs of aclHost, the sticky directories in "$ROOT/sticky" and the owners in "$ROOT/owners" ("<path> <user>"),
// read and edited by the chmod/chown/stat stand-ins; setfacl -m replaces one entry on one path.
const manageHost = `
setfacl() {
    printf 'SETFACL %s\\n' "$*" | sed "s#$ROOT##g"
    _def=""; _op=""; _spec=""
    while [ $# -gt 1 ]; do
        case "$1" in -d) _def="default:" ;; -x|-m) _op="$1"; _spec="$2"; shift ;; esac
        shift
    done
    _name=$(printf '%s' "$_spec" | cut -d: -f2)
    if [ "$_op" = "-x" ]; then
        awk -v p="$1" -v e="\${_def}user:$_name:" '!(index($1, p) == 1 && index($2, e) == 1)' "$ROOT/acls" > "$ROOT/acls.new"
    else
        _perm=$(printf '%s' "$_spec" | cut -d: -f3 | awk '{ printf "%s%s%s", index($0, "r") ? "r" : "-", index($0, "w") ? "w" : "-", (index($0, "x") || index($0, "X")) ? "x" : "-" }')
        { awk -v p="$1" -v e="\${_def}user:$_name:" '!($1 == p && index($2, e) == 1)' "$ROOT/acls"; printf '%s %suser:%s:%s\\n' "$1" "$_def" "$_name" "$_perm"; } > "$ROOT/acls.new"
    fi
    mv "$ROOT/acls.new" "$ROOT/acls"
}
dir_is_sticky() { grep -qxF "$1" "$ROOT/sticky"; }
is_link() { grep -qxF "$1" "$ROOT/links"; }
chmod() {
    printf 'CHMOD %s\\n' "$*" | sed "s#$ROOT##g"
    case "$1" in
        +t) grep -qxF "$2" "$ROOT/sticky" || printf '%s\\n' "$2" >> "$ROOT/sticky" ;;
        -t) grep -vxF "$2" "$ROOT/sticky" > "$ROOT/sticky.new"; mv "$ROOT/sticky.new" "$ROOT/sticky" ;;
    esac
}
stat() { _p=""; for _a in "$@"; do _p="$_a"; done; awk -v p="$_p" '$1 == p { print $2 }' "$ROOT/owners"; }
chown() {
    printf 'CHOWN %s\\n' "$*" | sed "s#$ROOT##g"
    _o=""; _p=""
    for _a in "$@"; do case "$_a" in -h) ;; *) if [ -z "$_o" ]; then _o="$_a"; else _p="$_a"; fi ;; esac; done
    awk -v p="$_p" -v o="\${_o%%:*}" '$1 == p { $2 = o } { print }' "$ROOT/owners" > "$ROOT/owners.new"
    mv "$ROOT/owners.new" "$ROOT/owners"
}
systemctl() { :; }
remove_certbot_conventions() { :; }
grant_sudoers_read_acls() { :; }
`;

const sitesDirs = ["/etc/apache2/sites-available", "/etc/apache2/sites-enabled"];
const rootPaths = text => text.replace(/([ "])\/(etc|var)\//g, (_, before, top) => (before === " " ? ` "$ROOT"/${top}/` : `"$ROOT/${top}/`));

function manageScenario({ apache = 1, writeAcl = sitesDirs, sticky = [], marker = [], vhosts = [], rkhunterAcl = false, derive = false }) {
  const sudoers = extractFunction("apply_sudoers");
  const revoke = extractFunction("revoke_apache_manage");
  assert.ok(revoke.includes('[ -L "$_ram_link" ]'), "the sites-enabled link test is the one replaced below");
  const functions = rootPaths([
    extractFunction("revoke_agent_acl"), revoke.replace('[ -L "$_ram_link" ]', 'is_link "$_ram_link"'), sudoers,
  ].join(""));
  const pathVars = ["SUDOERS_FILE", "APACHE_MANAGE_SUDOERS_FILE", "APACHE_CONFIGTEST_HELPER_PATH", "APACHE_RELOAD_HELPER_PATH",
    "APACHE_MODULE_HELPER_PATH", "APACHE_READ_SITE_HELPER_PATH", "RKHUNTER_MANAGE_SUDOERS_FILE", "RKHUNTER_HELPER_PATH"];
  const flags = capabilities.map(capability => `ENABLE_${capability}=${capability === "APACHE" ? apache : 0}`).join("; ");
  const entries = [
    ...sitesDirs.map(path => `"$ROOT${path}" user:www-data:r-x`),
    ...writeAcl.map(path => `"$ROOT${path}" user:aetheus-agent:rwx`),
    ...(apache ? [`"$ROOT/etc/apache2" user:aetheus-agent:r-x`] : []),
    ...(rkhunterAcl ? [`"$ROOT/var/log/rkhunter.log" user:aetheus-agent:r--`] : []),
    `"$ROOT/var/log/rkhunter.log" user:www-data:r--`,
  ];
  const derivation = derive ? `
${Object.keys(witnesses).map(name => `${name}="$ROOT/${name}"`).join("\n")}
INSTALL_DIR="$ROOT/opt"
id() { return 1; }
is_explicit() { return 1; }
${capabilities.map(capability => `ENABLE_${capability}=0`).join("; ")}
${extractFunction("preserve")}
${rootPaths(extractFunction("derive_existing_posture"))}
derive_existing_posture
echo "DERIVED_APACHE=$ENABLE_APACHE"; echo "DERIVED_APACHE_MANAGE=$ENABLE_APACHE_MANAGE"; echo "DERIVED_RKHUNTER_MANAGE=$ENABLE_RKHUNTER_MANAGE"` : "";
  const result = run(`${stubs}${aclHost}${manageHost}
AGENT_USER="aetheus-agent"; AGENT_GROUP="aetheus-agent"; APACHE_STICKY_MARKER_FILE="$ROOT/sticky-marker"
${pathVars.map(name => `${name}="$ROOT/${name}"; : > "$${name}"`).join("\n")}
${flags}
mkdir -p "$ROOT/etc/apache2" "$ROOT/var/log"
${sitesDirs.map(path => `mkdir -p "$ROOT${path}"`).join("\n")}
: > "$ROOT/var/log/rkhunter.log"; : > "$ROOT/acls"; : > "$ROOT/sticky"; : > "$ROOT/owners"; : > "$ROOT/links"
${entries.map(line => `printf '%s %s\\n' ${line} >> "$ROOT/acls"`).join("\n")}
${sticky.map(path => `printf '%s\\n' "$ROOT${path}" >> "$ROOT/sticky"`).join("\n")}
${marker.map(path => `printf '%s\\n' "$ROOT${path}" >> "$APACHE_STICKY_MARKER_FILE"`).join("\n")}
${vhosts.map(({ name, marked = true, owner = "aetheus-agent", linkOwner = null }) => `
printf '%s\\n' '${marked ? "# Managed by Aetheus" : "# Admin site"}' '<VirtualHost *:80>' > "$ROOT/etc/apache2/sites-available/${name}"
printf '%s %s\\n' "$ROOT/etc/apache2/sites-available/${name}" '${owner}' >> "$ROOT/owners"
${linkOwner === null ? "" : `: > "$ROOT/etc/apache2/sites-enabled/${name}"; printf '%s\\n' "$ROOT/etc/apache2/sites-enabled/${name}" >> "$ROOT/links"
printf '%s %s\\n' "$ROOT/etc/apache2/sites-enabled/${name}" '${linkOwner}' >> "$ROOT/owners"`}`).join("\n")}
${functions}
apply_sudoers
echo "ACLS_BEGIN"; sed "s#$ROOT##g" "$ROOT/acls"; echo "ACLS_END"
echo "STICKY=$(sed "s#$ROOT##g" "$ROOT/sticky" | tr '\\n' ' ')"
if [ -f "$APACHE_STICKY_MARKER_FILE" ]; then echo "MARKER=$(sed "s#$ROOT##g" "$APACHE_STICKY_MARKER_FILE" | tr '\\n' ' ')"; else echo "MARKER=absent"; fi
echo "OWNERS=$(sed "s#$ROOT##g" "$ROOT/owners" | tr '\\n' ',')"
for _v in ${pathVars.map(name => `"$${name}"`).join(" ")}; do if [ -e "$_v" ]; then echo "LEFT $_v" | sed "s#$ROOT/##"; fi; done${derivation}
`);
  assert.equal(result.status, 0, result.stderr);
  const lines = result.stdout.split("\n");
  const acls = lines.slice(lines.indexOf("ACLS_BEGIN") + 1, lines.indexOf("ACLS_END"));
  return {
    calls: lines.filter(line => /^(SETFACL|CHMOD|CHOWN) /.test(line)),
    warnings: lines.filter(line => line.startsWith("WARN ")),
    left: lines.filter(line => line.startsWith("LEFT ")).map(line => line.slice(5)),
    acls, agentEntries: acls.filter(line => line.includes("user:aetheus-agent:")),
    ...values(result.stdout),
  };
}

test("disabling the Apache management removes its drop-in and every helper it installed, the reload helper included", () => {
  const result = manageScenario({});
  for (const name of ["APACHE_MANAGE_SUDOERS_FILE", "APACHE_CONFIGTEST_HELPER_PATH", "APACHE_RELOAD_HELPER_PATH", "APACHE_MODULE_HELPER_PATH", "APACHE_READ_SITE_HELPER_PATH"]) {
    assert.ok(!result.left.includes(name), `${name} is removed: ${result.left.join(", ")}`);
  }
});

test("disabling the Apache management lowers the agent write ACL to read while the introspection stays", () => {
  const result = manageScenario({ apache: 1 });
  for (const path of sitesDirs) {
    assert.ok(result.acls.includes(`${path} user:aetheus-agent:r-x`), `${path}: ${result.acls.join("\n")}`);
    assert.ok(!result.acls.includes(`${path} user:aetheus-agent:rwx`), `${path} keeps no write entry`);
    assert.ok(result.acls.includes(`${path} user:www-data:r-x`), `${path} keeps the other user's entry`);
  }
  assert.ok(result.acls.includes("/etc/apache2 user:aetheus-agent:r-x"), "the introspection read ACL stays");
});

test("disabling the Apache management with the introspection removes the agent entry on the sites directories", () => {
  const result = manageScenario({ apache: 0 });
  for (const path of sitesDirs) {
    assert.ok(!result.acls.some(line => line.startsWith(`${path} user:aetheus-agent:`)), `${path}: ${result.acls.join("\n")}`);
    assert.ok(result.acls.includes(`${path} user:www-data:r-x`), `${path} keeps the other user's entry`);
  }
});

test("disabling the Apache management clears only the sticky bit the installer recorded", () => {
  const result = manageScenario({ sticky: sitesDirs, marker: ["/etc/apache2/sites-available"] });
  assert.equal(result.STICKY.trim(), "/etc/apache2/sites-enabled", "the unrecorded sticky bit stays");
  assert.ok(result.calls.includes("CHMOD -t /etc/apache2/sites-available"), result.calls.join("\n"));
  assert.ok(!result.calls.includes("CHMOD -t /etc/apache2/sites-enabled"), "no -t on a bit Aetheus did not set");
  assert.ok(result.warnings.some(line => line.includes("Keeping the sticky bit on") && line.includes("/etc/apache2/sites-enabled")), result.warnings.join("\n"));
  assert.equal(result.MARKER, "absent", "the record goes once nothing is left in it");
});

test("enabling the Apache management records the sticky bit only where it sets it", () => {
  const body = extractFunction("dir_is_sticky") + extractFunction("record_apache_sticky");
  const result = run(`${stubs}
APACHE_STICKY_MARKER_FILE="$ROOT/sticky-marker"
chmod() { :; }; chown() { :; }
mkdir -p "$ROOT/found" "$ROOT/set"
${body}
dir_is_sticky() { [ "$1" = "$ROOT/found" ]; }
for d in "$ROOT/found" "$ROOT/set" "$ROOT/set"; do
    if ! dir_is_sticky "$d"; then
        chmod +t "$d"
        record_apache_sticky "$d"
    fi
done
sed "s#$ROOT##g" "$APACHE_STICKY_MARKER_FILE"
`);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, "/set\n", "recorded once, and never the directory found already sticky");
  for (const caller of ["write_apache_manage_sudoers", "apply_read_acls"]) {
    assert.match(extractFunction(caller), /if ! dir_is_sticky "\$d"; then\n\s+chmod \+t "\$d"\n\s+record_apache_sticky "\$d"\n\s+fi/, caller);
  }
});

test("disabling the Apache management gives back to root only the Aetheus vhosts the agent owns", () => {
  const result = manageScenario({ vhosts: [
    { name: "app.conf", linkOwner: "aetheus-agent" },
    { name: "admin.conf", marked: false, linkOwner: "aetheus-agent" },
    { name: "other.conf", owner: "alice", linkOwner: "alice" },
  ] });
  const owners = Object.fromEntries(result.OWNERS.split(",").filter(Boolean).map(line => line.split(" ")));
  assert.equal(owners["/etc/apache2/sites-available/app.conf"], "root");
  assert.equal(owners["/etc/apache2/sites-enabled/app.conf"], "root");
  assert.ok(result.calls.includes("CHOWN -h root:root /etc/apache2/sites-enabled/app.conf"), "the link itself, not its target");
  assert.equal(owners["/etc/apache2/sites-available/admin.conf"], "aetheus-agent", "a vhost without the marker is not touched");
  assert.equal(owners["/etc/apache2/sites-available/other.conf"], "alice", "a vhost someone else owns is not touched");
  assert.equal(owners["/etc/apache2/sites-enabled/other.conf"], "alice");
});

test("a second Apache management removal changes nothing and nothing is done on a host that never had it", () => {
  const first = manageScenario({ sticky: ["/etc/apache2/sites-available"], marker: ["/etc/apache2/sites-available"], vhosts: [{ name: "app.conf", linkOwner: "aetheus-agent" }] });
  assert.ok(first.calls.length > 0);
  const second = manageScenario({ writeAcl: [], vhosts: [{ name: "app.conf", owner: "root", linkOwner: "root" }] });
  assert.deepEqual(second.calls, []);
  assert.deepEqual(second.warnings, []);
  assert.equal(second.MARKER, "absent");
});

test("disabling the RKHunter management removes the agent read ACL on its log and only that entry", () => {
  const result = manageScenario({ rkhunterAcl: true });
  assert.ok(!result.acls.includes("/var/log/rkhunter.log user:aetheus-agent:r--"), result.acls.join("\n"));
  assert.ok(result.acls.includes("/var/log/rkhunter.log user:www-data:r--"), "the other user's entry stays");
  assert.ok(!result.left.includes("RKHUNTER_MANAGE_SUDOERS_FILE") && !result.left.includes("RKHUNTER_HELPER_PATH"));
  assert.ok(!result.calls.includes("SETFACL -R -d -x u:aetheus-agent /var/log/rkhunter.log"), "a file has no default entries");
  const again = manageScenario({});
  assert.ok(!again.calls.some(call => call.includes("rkhunter.log")), "nothing to remove the second time");
});

test("the next upgrade derives the Apache and RKHunter management as disabled and keeps the Apache introspection", () => {
  const result = manageScenario({ apache: 1, rkhunterAcl: true, derive: true });
  assert.equal(result.DERIVED_APACHE_MANAGE, "0");
  assert.equal(result.DERIVED_RKHUNTER_MANAGE, "0");
  assert.equal(result.DERIVED_APACHE, "1", "the read ACL left on /etc/apache2 keeps the introspection");
});

test("the purge undoes the Apache management before the agent account is deleted and drops the sticky record", () => {
  const purge = installer.slice(installer.indexOf('if [ "$MODE" = "purge" ]; then'), installer.indexOf("# Mode: UPGRADE"));
  const revoke = purge.indexOf("\n        revoke_apache_manage\n");
  assert.ok(revoke > 0, "the purge calls revoke_apache_manage");
  assert.ok(revoke < purge.indexOf('userdel "$AGENT_USER"'), "before userdel");
  assert.match(purge, /rm -f "\$DOCKER_GROUP_MARKER_FILE" "\$APACHE_STICKY_MARKER_FILE"/);
});

test("the help of every --disable option names what it removes", () => {
  const help = installer.slice(installer.indexOf("Disable options"), installer.indexOf("  --disable-all "));
  const parsed = [...installer.matchAll(/^\s+(--disable-[a-z-]+)\)/gm)].map(match => match[1]).filter(flag => flag !== "--disable-all");
  for (const flag of parsed) assert.ok(help.includes(`  ${flag} `), `${flag} is documented`);
  assert.match(help, /--disable-rkhunter-manage[\s\S]*read ACL on \/var\/log\/rkhunter\.log/);
  assert.match(help, /--disable-apache-manage[\s\S]*sticky bit only[\s\S]*root:root/);
});

// R-19: --disable-teamspeak takes back only what the installer recorded, and the next upgrade does not
// read the leftovers as a request to enable TeamSpeak again.

function teamspeak({ flags, member = true, dir = true, marker = null, derive = false, gpasswdStatus = 0 }) {
  const quoted = flags.map(flag => `'${flag}'`).join(" ");
  const body = `${stubs}
AGENT_USER="aetheus-agent"; AGENT_GROUP="aetheus-agent"; INSTALL_DIR="$ROOT/opt"
TEAMSPEAK_MARKER_FILE="$ROOT/teamspeak-marker"
${marker === null ? "" : `printf '${marker}' > "$TEAMSPEAK_MARKER_FILE"`}
${member ? ': > "$ROOT/member"' : ""}
${dir ? 'mkdir -p "$ROOT/opt/teamspeak"; printf secret > "$ROOT/opt/teamspeak/query-credentials"' : 'mkdir -p "$ROOT/opt"'}
id() { if [ -f "$ROOT/member" ]; then echo "aetheus-agent teamspeak"; else echo "aetheus-agent"; fi; }
getent() { return 0; }
gpasswd() { echo "CALL gpasswd $*" >> "$ROOT/calls"; [ ${gpasswdStatus} -eq 0 ] && rm -f "$ROOT/member"; return ${gpasswdStatus}; }
usermod() { echo "CALL usermod $*" >> "$ROOT/calls"; : > "$ROOT/member"; }
chown() { :; }
getfacl() { return 1; }
set -- --upgrade ${quoted}
${extractParser()}
${extractFunction("teamspeak_marker_has")}
${extractFunction("write_teamspeak_marker")}
${extractFunction("revoke_teamspeak_access")}
${extractFunction("ensure_teamspeak_access")}
${extractFunction("preserve")}
${extractFunction("derive_existing_posture")}
ensure_teamspeak_access
cat "$ROOT/calls" 2>/dev/null || true
if [ -f "$ROOT/member" ]; then echo "MEMBER=1"; else echo "MEMBER=0"; fi
if [ -d "$ROOT/opt/teamspeak" ]; then echo "DIR=1"; else echo "DIR=0"; fi
if [ -f "$TEAMSPEAK_MARKER_FILE" ]; then echo "MARKER=$(tr '\\n' ',' < "$TEAMSPEAK_MARKER_FILE")"; else echo "MARKER="; fi
${derive ? `EXPLICIT_CAPABILITIES=" "; ENABLE_TEAMSPEAK=0
derive_existing_posture
echo "DERIVED=$ENABLE_TEAMSPEAK"` : ""}
`;
  const result = run(body);
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}

const recorded = "user=aetheus-agent\\nstate=enabled\\ngroup-added\\ndir-created\\n";

test("--disable-teamspeak removes the membership and the directory the installer recorded, and lists the files", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"], marker: recorded });
  assert.match(output, /CALL gpasswd -d aetheus-agent teamspeak/);
  assert.match(output, /deleting .*opt\/teamspeak\/query-credentials/);
  const state = values(output);
  assert.equal(state.MEMBER, "0");
  assert.equal(state.DIR, "0");
  assert.match(state.MARKER, /state=disabled/);
  assert.doesNotMatch(state.MARKER, /group-added|dir-created/);
});

test("--disable-teamspeak keeps a membership and a directory without a record, and says so", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"] });
  assert.doesNotMatch(output, /CALL gpasswd/);
  assert.match(output, /WARN .*teamspeak group, which this installer did not add/);
  assert.match(output, /WARN .*not created by this installer/);
  const state = values(output);
  assert.equal(state.MEMBER, "1");
  assert.equal(state.DIR, "1");
});

test("a TeamSpeak record written for another account authorizes nothing", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"], marker: "user=someone-else\\nstate=enabled\\ngroup-added\\ndir-created\\n" });
  assert.doesNotMatch(output, /CALL gpasswd/);
  assert.equal(values(output).DIR, "1");
});

test("a failed TeamSpeak group removal is reported and the record and directory stay", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"], marker: recorded, gpasswdStatus: 3 });
  assert.match(output, /ERROR .*Could not remove aetheus-agent from the teamspeak group/);
  assert.equal(values(output).DIR, "1");
  assert.match(values(output).MARKER, /state=enabled/);
});

test("the next upgrade does not re-enable TeamSpeak after a disable with a record", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"], marker: recorded, derive: true });
  assert.equal(values(output).DERIVED, "0");
});

test("the next upgrade does not re-enable TeamSpeak after a disable that kept a membership found in place", () => {
  const output = teamspeak({ flags: ["--disable-teamspeak"], derive: true });
  assert.equal(values(output).MEMBER, "1");
  assert.equal(values(output).DERIVED, "0");
});

test("a host installed before the marker keeps TEAMSPEAK=1 from the membership, and an enabled record keeps it too", () => {
  assert.equal(values(teamspeak({ flags: [], dir: false, derive: true })).DERIVED, "1");
  assert.equal(values(teamspeak({ flags: [], member: false, dir: false, derive: true, marker: "user=aetheus-agent\\nstate=enabled\\n" })).DERIVED, "1");
});

test("enabling TeamSpeak records what it added and only that", () => {
  const added = values(teamspeak({ flags: ["--enable-teamspeak"], member: false, dir: false }));
  assert.match(added.MARKER, /state=enabled,group-added,dir-created,/);
  const found = values(teamspeak({ flags: ["--enable-teamspeak"] }));
  assert.match(found.MARKER, /^user=aetheus-agent,state=enabled,$/);
});

test("an upgrade that does not name TeamSpeak leaves it alone", () => {
  const output = teamspeak({ flags: ["--disable-cron-manage"], marker: recorded });
  assert.doesNotMatch(output, /CALL gpasswd/);
  assert.equal(values(output).DIR, "1");
});

test("the purge removes the TeamSpeak marker", () => {
  const purge = installer.slice(installer.indexOf('if [ "$MODE" = "purge" ]; then'), installer.indexOf("# Mode: UPGRADE"));
  assert.match(purge, /rm -f [^\n]*"\$TEAMSPEAK_MARKER_FILE"/);
});
