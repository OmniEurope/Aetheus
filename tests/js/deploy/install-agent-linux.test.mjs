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
