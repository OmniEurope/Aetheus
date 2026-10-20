// SPDX-License-Identifier: EUPL-1.2
// The root-owned helpers of deploy/agent-host-config run under a NOPASSWD sudo grant, so what they
// refuse is the boundary between the agent account and root. They are run here for real, on a copy
// whose /etc, /var and /opt are moved under a temporary root and whose system commands (systemctl,
// apt-get, ufw, iptables...) are stand-ins on PATH that record every call. Nothing touches the host.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";

const helpersRoot = resolve("deploy/agent-host-config");
const separator = process.platform === "win32" ? ";" : ":";
const stubbed = [
  "systemctl", "apt-get", "iptables", "ip6tables", "certbot", "openssl", "journalctl", "postconf",
  "debconf-set-selections", "useradd", "curl", "tar", "chown", "chmod", "a2enconf", "apache2ctl",
  "ufw", "sshd", "postsuper"
];

// The same directory as the shell sees it: on Windows, /c/... rather than C:/..., which a helper
// that only accepts absolute POSIX paths (teamspeak-setup) can be given as an argument.
const shellPath = path => path.replace(/^([A-Za-z]):/, (_, drive) => `/${drive.toLowerCase()}`);

// Arguments travel through the environment and are rebuilt by a small wrapper: Windows joins argv
// into one command line, and the MSYS shell splits an argument at its line break there, which is
// the very byte these tests are about. The environment block keeps it.
const wrapper = 'i=0; set --; while [ "$i" -lt "$HELPER_ARGC" ]; do eval "set -- \\"\\$@\\" \\"\\$HELPER_ARG_$i\\""; i=$((i + 1)); done; exec sh "$HELPER_SCRIPT" "$@"';

// Copies one helper under a temporary root. Host paths are rebased with a lookbehind so only real
// path starts move; the caller asserts the rebase it relies on, so a renamed path fails loudly.
// stubs: { command: "shell body" } overrides the default stand-in (record the call, exit 0).
// rewrite: applied to the helper text first, for what a path rebase cannot express (an account, a PATH).
function setUp(helper, { stubs = {}, rewrite = text => text } = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-helper-")).replace(/\\/g, "/");
  const bin = `${root}/bin`;
  mkdirSync(bin);
  const log = `${root}/calls.log`;
  for (const command of new Set([...stubbed, ...Object.keys(stubs)])) {
    const body = stubs[command] ?? "exit 0";
    writeFileSync(`${bin}/${command}`, `#!/bin/sh\necho "${command} $*" >> "${log}"\n${body}\n`);
    chmodSync(`${bin}/${command}`, 0o755);
  }
  // A #{include:<path>}# line is inlined the way render_host_config does it at install time.
  const source = readFileSync(join(helpersRoot, helper), "utf8")
    .replace(/^#\{include:(.+)\}#$/gm, (_, path) => readFileSync(join(helpersRoot, path), "utf8").replace(/\n$/, ""));
  const text = rewrite(source)
    .replace(/#\{ACME_WEBROOT\}#/g, `${root}/var/www/acme`)
    .replace(/(?<=^|[\s"'=(])\/usr\/sbin\/(ufw|sshd)\b/gm, `${bin}/$1`)
    .replace(/(?<=^|[\s"'=(])\/(etc|var|opt)\//gm, `${root}/$1/`);
  const script = `${root}/${helper.split("/").pop()}`;
  writeFileSync(script, text);
  const run = (args, { env = {}, input = "" } = {}) => spawnSync("sh", ["-c", wrapper], {
    encoding: "utf8",
    timeout: 60000,
    input,
    env: {
      ...process.env,
      PATH: `${bin}${separator}${process.env.PATH}`,
      HELPER_SCRIPT: script,
      HELPER_ARGC: String(args.length),
      ...Object.fromEntries(args.map((value, index) => [`HELPER_ARG_${index}`, value])),
      ...env
    }
  });
  const calls = () => (existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").filter(Boolean) : []);
  const file = path => `${root}${path}`;
  const directory = path => mkdirSync(file(path), { recursive: true });
  const write = (path, content) => {
    mkdirSync(dirname(file(path)), { recursive: true });
    writeFileSync(file(path), content);
  };
  return { root, shRoot: shellPath(root), text, run, calls, file, directory, write, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

// Reject line breaks in arguments before execution so one input cannot become multiple commands.
// See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

test("cron-apply refuses a multi-line command and leaves the existing cron file as it was", () => {
  const env = setUp("cron/cron-apply");
  try {
    assert.ok(env.text.includes(`CRON_DIR="${env.root}/etc/cron.d"`), "the cron directory was rebased");
    const existing = "# Managed by Aetheus (job job1)\n0 3 * * * deploy /usr/bin/backup\n";
    env.write("/etc/cron.d/aetheus-job1", existing);
    // The audit's exploit: the first line passes the pattern, the second is a root cron line.
    const result = env.run(["save", "job1", "deploy", "* * * * *", "true\n* * * * * root /bin/sh /tmp/x"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /invalid command/);
    assert.equal(readFileSync(env.file("/etc/cron.d/aetheus-job1"), "utf8"), existing);
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});

for (const [label, args, reason] of [
  ["an id", ["save", "job1\nx", "deploy", "* * * * *", "true"], /invalid id/],
  ["a user", ["save", "job1", "deploy\nroot", "* * * * *", "true"], /invalid user/],
  ["a schedule", ["save", "job1", "deploy", "* * * * *\n*", "true"], /invalid schedule/],
  ["a carriage return in the command", ["save", "job1", "deploy", "* * * * *", "true\r* * * * * root x"], /invalid command/],
  ["a delete id", ["delete", "job1\njob2"], /invalid id/]
]) {
  test(`cron-apply refuses ${label} holding a line break`, () => {
    const env = setUp("cron/cron-apply");
    try {
      env.write("/etc/cron.d/aetheus-job1", "kept\n");
      const result = env.run(args);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, reason);
      assert.equal(readFileSync(env.file("/etc/cron.d/aetheus-job1"), "utf8"), "kept\n");
      assert.ok(!existsSync(env.file("/etc/cron.d/aetheus-job1\nx")));
    } finally { env.cleanup(); }
  });
}

test("cron-apply still writes a valid single-line job", () => {
  const env = setUp("cron/cron-apply");
  try {
    env.directory("/etc/cron.d");
    const result = env.run(["save", "job1", "deploy", "*/5 * * * *", "/usr/bin/backup --quiet"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(env.file("/etc/cron.d/aetheus-job1"), "utf8"),
      "# Managed by Aetheus (job job1)\n*/5 * * * * deploy /usr/bin/backup --quiet\n");
  } finally { env.cleanup(); }
});

// Each case: the helper, the multi-line call, the refusal, then the same call on one line, which
// must get past validation and reach the named stand-in (so the refusal is the line break's doing).
const refusals = [
  { helper: "deploy/deploy-restart", bad: ["app1\nother"], reason: /invalid app/, good: ["app1"], reaches: "systemctl" },
  { helper: "certbot/aetheus-certbot-issue", bad: ["example.com\nevil.com"], reason: /invalid primary domain/, good: ["example.com"], reaches: "openssl", env: { AETHEUS_CERTBOT_MODE: "local" } },
  { helper: "certbot/aetheus-certbot-issue", bad: ["example.com"], reason: /invalid domain in list/, good: ["example.com"], reaches: "openssl", env: { AETHEUS_CERTBOT_MODE: "local", AETHEUS_CERTBOT_DOMAINS: "example.com,a.example.com\nb.example.com" }, goodEnv: { AETHEUS_CERTBOT_MODE: "local", AETHEUS_CERTBOT_DOMAINS: "example.com,a.example.com" } },
  { helper: "certbot/aetheus-certbot-issue", bad: ["example.com"], reason: /invalid email/, good: ["example.com"], reaches: "openssl", env: { AETHEUS_CERTBOT_MODE: "local", AETHEUS_CERTBOT_EMAIL: "a@example.com\n--server" }, goodEnv: { AETHEUS_CERTBOT_MODE: "local", AETHEUS_CERTBOT_EMAIL: "a@example.com" } },
  { helper: "certbot/aetheus-certbot-manage", bad: ["renew", "lineage\n--x"], reason: /invalid cert name/, good: ["renew", "lineage"], reaches: "certbot renew" },
  { helper: "portsentry/unblock-ip", bad: ["10.0.0.1\n10.0.0.2"], reason: /invalid ip/, good: ["10.0.0.1"], reaches: "iptables" },
  { helper: "portsentry/portsentry-setup", bad: ["atcp\nx", "80", "53"], reason: /invalid mode/, good: ["atcp", "80", "53"], reaches: "apt-get" },
  { helper: "portsentry/portsentry-setup", bad: ["atcp", "80\n22", "53"], reason: /invalid tcp ports/, good: ["atcp", "80,22", "53"], reaches: "apt-get" },
  { helper: "portsentry/portsentry-setup", bad: ["atcp", "80", "53\r"], reason: /invalid udp ports/, good: ["atcp", "80", "53"], reaches: "apt-get" },
  { helper: "teamspeak/teamspeak-setup", bad: ["/srv/ts\n/etc"], reason: /invalid install path/, good: ["/srv/ts"], reaches: "systemctl" },
  { helper: "teamspeak/teamspeak-setup", bad: ["/srv/ts", "9987\n1"], reason: /invalid voice port/, good: ["/srv/ts", "9987"], reaches: "systemctl" },
  { helper: "firewall/aetheus-firewall", bad: ["allow", "8080", "tcp\nudp"], reason: /invalid protocol/, good: ["allow", "8080", "tcp"], reaches: "ufw allow" },
  { helper: "firewall/aetheus-firewall", bad: ["allow", "8080", "tcp", "10.0.0.1\n0.0.0.0/0"], reason: /invalid source/, good: ["allow", "8080", "tcp", "10.0.0.1"], reaches: "ufw allow" },
  { helper: "mail/mail-setup", bad: ["mail.example.com\nx.org", "example.com", "s1", "admin@example.com", "1024"], reason: /invalid hostname/, good: ["mail.example.com", "example.com", "s1", "admin@example.com", "1024"], reaches: "debconf-set-selections", input: "longpassword\n" },
  { helper: "mail/mail-setup", bad: ["mail.example.com", "example.com", "s1\nx", "admin@example.com", "1024"], reason: /invalid selector/, good: ["mail.example.com", "example.com", "s1", "admin@example.com", "1024"], reaches: "debconf-set-selections", input: "longpassword\n" },
  { helper: "mail/mail-setup", bad: ["mail.example.com", "example.com", "s1", "admin@example.com", "1024\n9"], reason: /invalid quota/, good: ["mail.example.com", "example.com", "s1", "admin@example.com", "1024"], reaches: "debconf-set-selections", input: "longpassword\n" },
  { helper: "mail/mail-manage", bad: ["logs", "postfix", "10", "sshd\n.*"], reason: /invalid filter/, good: ["logs", "postfix", "10", "sshd"], reaches: "journalctl" },
  { helper: "mail/mail-manage", bad: ["logs", "postfix", "10\n2"], reason: /invalid line count/, good: ["logs", "postfix", "10"], reaches: "journalctl" },
  { helper: "mail/mail-manage", bad: ["add-domain", "example.com\nevil.org"], reason: /invalid domain/, good: ["add-domain", "example.com"], reaches: "chown" },
  { helper: "mail/mail-manage", bad: ["add-alias", "a@example.com\nb@evil.org", "c@example.com"], reason: /invalid alias/ },
  { helper: "mail/mail-manage", bad: ["queue-delete", "ABCDEF12\nALL"], reason: /invalid queue id/, good: ["queue-delete", "ABCDEF12"], reaches: "postsuper" }
];

for (const { helper, bad, reason, good, reaches, env: badEnv = {}, goodEnv = badEnv, input = "" } of refusals) {
  test(`${helper} refuses ${JSON.stringify(bad.join(" "))}${badEnv.AETHEUS_CERTBOT_DOMAINS || badEnv.AETHEUS_CERTBOT_EMAIL ? " with a multi-line environment value" : ""}`, () => {
    const refused = setUp(helper);
    try {
      const result = refused.run(bad, { env: badEnv, input });
      assert.notEqual(result.status, 0, result.stdout);
      assert.match(result.stderr, reason);
      assert.deepEqual(refused.calls().filter(call => !call.startsWith("sshd")), [], "nothing ran before the refusal");
    } finally { refused.cleanup(); }
    if (!good) return;
    const accepted = setUp(helper);
    try {
      const result = accepted.run(good, { env: goodEnv, input });
      assert.doesNotMatch(result.stderr, reason);
      assert.ok(accepted.calls().some(call => call.startsWith(reaches)), `${reaches} reached: ${accepted.calls().join(" | ")} ${result.stderr}`);
    } finally { accepted.cleanup(); }
  });
}

// Refuse enablement when the administration port cannot be allowed so the reported state remains
// usable. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

test("the firewall is not enabled when allowing the administration port fails", () => {
  const env = setUp("firewall/aetheus-firewall", {
    stubs: { sshd: 'echo "port 2222"', ufw: 'case "$1" in allow) echo "ERROR: problem running" >&2; exit 1 ;; esac; exit 0' }
  });
  try {
    const result = env.run(["enable"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /2222\/tcp could not be allowed/);
    assert.ok(env.calls().includes("ufw allow 2222/tcp"));
    assert.ok(!env.calls().some(call => call.startsWith("ufw --force enable")), env.calls().join(" | "));
  } finally { env.cleanup(); }
});

test("the firewall is enabled once the administration port is allowed", () => {
  const env = setUp("firewall/aetheus-firewall", { stubs: { sshd: 'echo "port 2222"' } });
  try {
    const result = env.run(["enable"]);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(env.calls().filter(call => call.startsWith("ufw")), ["ufw allow 2222/tcp", "ufw --force enable"]);
  } finally { env.cleanup(); }
});

// Report both deletion variants so a partial operation cannot appear complete. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

// The stand-in holds the rules named in RULES ("allow" and/or "deny"); deleting an absent one prints
// ufw's own message, and FAIL makes that variant fail for a real reason.
const deleteStub = (rules, fail = "") => `rules="${rules}"; fail="${fail}"
[ "$1" = "--force" ] && [ "$2" = "delete" ] || exit 0
case " $fail " in *" $3 "*) echo "ERROR: Couldn't determine iptables version" >&2; exit 1 ;; esac
case " $rules " in *" $3 "*) echo "Rule deleted"; exit 0 ;; esac
echo "Could not delete non-existent rule"; exit 0`;

for (const [label, rules, fail, status, expected] of [
  ["an allow rule alone", "allow", "", 0, /Rule deleted[\s\S]*no deny rule to delete/],
  ["a deny rule alone", "deny", "", 0, /no allow rule to delete[\s\S]*Rule deleted/],
  ["no rule at all", "", "", 0, /no allow rule to delete[\s\S]*no deny rule to delete/],
  ["both rules", "allow deny", "", 0, /Rule deleted[\s\S]*Rule deleted/]
]) {
  test(`deleting ${label} succeeds and says what it removed`, () => {
    const env = setUp("firewall/aetheus-firewall", { stubs: { ufw: deleteStub(rules, fail) } });
    try {
      const result = env.run(["delete", "8080", "tcp"]);
      assert.equal(result.status, status, result.stderr);
      assert.match(result.stdout, expected);
      assert.deepEqual(env.calls().filter(call => call.startsWith("ufw")),
        ["ufw --force delete allow 8080/tcp", "ufw --force delete deny 8080/tcp"]);
    } finally { env.cleanup(); }
  });
}

for (const [label, args, failing] of [
  ["the allow variant", ["delete", "8080", "tcp"], "allow"],
  ["the deny variant", ["delete", "8080", "tcp"], "deny"],
  ["the allow variant of a sourced rule", ["delete", "8080", "tcp", "10.0.0.0/8"], "allow"]
]) {
  test(`a real ufw failure on ${label} fails the delete`, () => {
    const env = setUp("firewall/aetheus-firewall", { stubs: { ufw: deleteStub("allow deny", failing) } });
    try {
      const result = env.run(args);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, new RegExp(`ufw delete ${failing} failed`));
      assert.equal(env.calls().filter(call => call.startsWith("ufw --force delete")).length, 2, "both variants were tried");
    } finally { env.cleanup(); }
  });
}

// Require successful portsentry setup and unblock operations before reporting success. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

for (const [label, stubs, reason] of [
  ["the package install fails", { "apt-get": 'case "$1" in install) exit 100 ;; esac' }, /apt-get install portsentry failed/],
  ["the restart is refused", { systemctl: 'case "$1" in restart) exit 1 ;; esac' }, /systemctl restart portsentry failed/],
  ["the service is not active afterwards", { systemctl: 'case "$1" in is-active) exit 3 ;; esac' }, /not active after the restart/]
]) {
  test(`portsentry-setup fails when ${label}`, () => {
    const env = setUp("portsentry/portsentry-setup", { stubs });
    try {
      const result = env.run(["atcp", "80", "53"]);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, reason);
      assert.doesNotMatch(result.stdout, /setup completed/);
    } finally { env.cleanup(); }
  });
}

test("portsentry-setup reports completion once the service is active", () => {
  const env = setUp("portsentry/portsentry-setup");
  try {
    env.write("/etc/default/portsentry", 'TCP_MODE="tcp"\nUDP_MODE="udp"\n');
    const result = env.run(["atcp", "80", "53"]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /PortSentry setup completed \(mode atcp\)/);
    assert.ok(env.calls().includes("systemctl is-active --quiet portsentry"));
    assert.equal(readFileSync(env.file("/etc/default/portsentry"), "utf8"), 'TCP_MODE="atcp"\nUDP_MODE="atcp"\n');
  } finally { env.cleanup(); }
});

// The iptables stand-in holds COUNT DROP rules for the address: -C answers 0 while one is left.
const iptablesStub = (count, { checkStatus = "", deleteStatus = "0" } = {}) => `state="$(dirname "$0")/../rules"
[ -f "$state" ] || echo ${count} > "$state"
n=$(cat "$state")
case "$1" in
  -C) ${checkStatus ? `exit ${checkStatus}` : '[ "$n" -gt 0 ] && exit 0; exit 1'} ;;
  -D) [ ${deleteStatus} -eq 0 ] || exit ${deleteStatus}; echo $((n - 1)) > "$state"; exit 0 ;;
esac`;

test("unblock-ip removes every DROP rule and the hosts.deny line, then reports it", () => {
  const env = setUp("portsentry/unblock-ip", { stubs: { iptables: iptablesStub(2) } });
  try {
    env.write("/etc/hosts.deny", "ALL: 10.0.0.1\nALL: 10.0.0.10\n");
    const result = env.run(["10.0.0.1"]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /iptables: 2 DROP rule\(s\) removed[\s\S]*unblocked 10\.0\.0\.1/);
    assert.equal(readFileSync(env.file("/etc/hosts.deny"), "utf8"), "ALL: 10.0.0.10\n");
    assert.ok(!env.calls().some(call => call.startsWith("ip6tables")), "an IPv4 address is not given to ip6tables");
  } finally { env.cleanup(); }
});

test("unblock-ip treats an absent rule as nothing to remove", () => {
  const env = setUp("portsentry/unblock-ip", { stubs: { ip6tables: iptablesStub(0) } });
  try {
    const result = env.run(["2001:db8::1"]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /ip6tables: 0 DROP rule\(s\) removed[\s\S]*unblocked 2001:db8::1/);
  } finally { env.cleanup(); }
});

for (const [label, stub, reason] of [
  ["the rules cannot be read", iptablesStub(1, { checkStatus: "4" }), /could not check the INPUT rules/],
  ["the rule cannot be removed", iptablesStub(1, { deleteStatus: "4" }), /could not remove the DROP rule/]
]) {
  test(`unblock-ip fails when ${label}`, () => {
    const env = setUp("portsentry/unblock-ip", { stubs: { iptables: stub } });
    try {
      const result = env.run(["10.0.0.1"]);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, reason);
      assert.doesNotMatch(result.stdout, /unblocked/);
    } finally { env.cleanup(); }
  });
}

// grep answering 2 on hosts.deny stands for a file that cannot be read; every other grep is the real one.
const unreadableHostsDeny = 'case "$*" in *hosts.deny*) echo "grep: hosts.deny: Input/output error" >&2; exit 2 ;; esac\nPATH="${PATH#*:}"\nexec grep "$@"';

test("unblock-ip keeps hosts.deny when it cannot be read", () => {
  const env = setUp("portsentry/unblock-ip", { stubs: { iptables: iptablesStub(0), grep: unreadableHostsDeny } });
  try {
    env.write("/etc/hosts.deny", "ALL: 10.0.0.1\n");
    const result = env.run(["10.0.0.1"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /could not read .*hosts\.deny \(grep exit 2\), file left unchanged/);
    assert.equal(readFileSync(env.file("/etc/hosts.deny"), "utf8"), "ALL: 10.0.0.1\n");
    assert.doesNotMatch(result.stdout, /unblocked/);
  } finally { env.cleanup(); }
});

// Treat an installed TeamSpeak service that cannot start as a failure. See
// docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.

function teamspeak({ active, start = "0", unit = true }) {
  const state = `"$(dirname "$0")/../active"`;
  const env = setUp("teamspeak/teamspeak-setup", {
    stubs: {
      systemctl: `case "$1" in
  is-active) [ -f ${state} ] && exit 0; exit 3 ;;
  start) [ ${start} -eq 0 ] || exit ${start}; ${active === "after-start" ? `touch ${state}` : ":"} ;;
esac`
    }
  });
  if (active === true) env.write("/active", "");
  env.write("/srv/ts/ts3server", "#!/bin/sh\n");
  chmodSync(env.file("/srv/ts/ts3server"), 0o755);
  env.directory("/etc/systemd/system");
  if (unit) env.write("/etc/systemd/system/teamspeak3.service", "[Unit]\n");
  return env;
}

test("teamspeak-setup leaves a running server untouched", () => {
  const env = teamspeak({ active: true });
  try {
    const result = env.run([`${env.shRoot}/srv/ts`]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /leaving it untouched/);
    assert.ok(!env.calls().some(call => call.startsWith("systemctl start")));
  } finally { env.cleanup(); }
});

test("teamspeak-setup fails when the installed server does not start", () => {
  const env = teamspeak({ active: false, start: "1" });
  try {
    const result = env.run([`${env.shRoot}/srv/ts`]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /teamspeak3 did not start/);
    assert.ok(!env.calls().some(call => call.startsWith("curl")), "the installed files are not downloaded again");
  } finally { env.cleanup(); }
});

test("teamspeak-setup fails when the started unit does not stay active", () => {
  const env = teamspeak({ active: false });
  try {
    const result = env.run([`${env.shRoot}/srv/ts`]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /teamspeak3 is not active/);
  } finally { env.cleanup(); }
});

test("teamspeak-setup writes a missing unit again and starts the installed server", () => {
  const env = teamspeak({ active: "after-start", unit: false });
  try {
    const result = env.run([`${env.shRoot}/srv/ts`]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /without the teamspeak3 unit - writing it again[\s\S]*started the existing server/);
    assert.ok(existsSync(env.file("/etc/systemd/system/teamspeak3.service")));
    const order = env.calls().filter(call => call.startsWith("systemctl")).map(call => call.split(" ")[1]);
    assert.deepEqual(order, ["is-active", "daemon-reload", "enable", "start", "is-active"]);
    assert.ok(!env.calls().some(call => call.startsWith("curl")));
  } finally { env.cleanup(); }
});

// ---- Recette R1-41: mail-manage master-set writes the Dovecot master user, never its password -------

function masterSet({ version = "2.4.1 (abc)", doveconf = "exit 0" } = {}) {
  const env = setUp("mail/mail-manage", {
    stubs: {
      doveadm: "cat > /dev/null; echo '{SHA512-CRYPT}$6$hashed'",
      dovecot: `echo "${version}"`,
      doveconf
    }
  });
  env.directory("/etc/dovecot/conf.d");
  return env;
}

for (const [version, marker] of [["2.4.1 (abc)", "passwd_file_path = "], ["2.3.21 (47349e2482)", "args = scheme=SHA512-CRYPT "]]) {
  test(`master-set writes the hashed master user and the ${version.slice(0, 3)} passdb, then reloads Dovecot`, () => {
    const env = masterSet({ version });
    try {
      const result = env.run(["master-set"], { input: "M4sterPassw0rd!\n" });
      assert.equal(result.status, 0, result.stderr);
      const users = readFileSync(env.file("/etc/dovecot/master-users"), "utf8");
      assert.equal(users, "aetheus-master:{SHA512-CRYPT}$6$hashed\n");
      assert.doesNotMatch(users + result.stdout + result.stderr + env.calls().join("\n"), /M4sterPassw0rd/);
      const conf = readFileSync(env.file("/etc/dovecot/conf.d/99-aetheus-master.conf"), "utf8");
      assert.match(conf, /^auth_master_user_separator = \*$/m);
      assert.match(conf, /master = yes/);
      assert.ok(conf.includes(marker + env.root + "/etc/dovecot/master-users"), conf);
      assert.doesNotMatch(conf, /#\{/);
      assert.ok(env.calls().includes("systemctl reload dovecot"), env.calls().join(" | "));
    } finally { env.cleanup(); }
  });
}

test("master-set refuses a missing or short password before anything is written", () => {
  for (const input of ["", "short\n"]) {
    const env = masterSet();
    try {
      const result = env.run(["master-set"], { input });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /no password on stdin|empty password|password too short/);
      assert.ok(!existsSync(env.file("/etc/dovecot/master-users")));
      assert.deepEqual(env.calls(), []);
    } finally { env.cleanup(); }
  }
});

test("master-set removes a configuration Dovecot rejects and does not reload", () => {
  const env = masterSet({ doveconf: "exit 1" });
  try {
    const result = env.run(["master-set"], { input: "M4sterPassw0rd!\n" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /dovecot rejected the master user configuration/);
    assert.ok(!existsSync(env.file("/etc/dovecot/conf.d/99-aetheus-master.conf")));
    assert.ok(!env.calls().some(call => call.startsWith("systemctl")));
  } finally { env.cleanup(); }
});

// The collector reads the marker line from the installed helper and the `version` verb prints the same
// number; the verb answers before the certbot-installed check, so it works on a host without certbot.
test("aetheus-certbot-manage announces its helper version, marker and verb agreeing", () => {
  const env = setUp("certbot/aetheus-certbot-manage");
  try {
    const result = env.run(["version"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), "aetheus-certbot-helper-version: 1");
    const script = readFileSync(join(helpersRoot, "certbot/aetheus-certbot-manage"), "utf8");
    assert.match(script, /^# aetheus-certbot-helper-version: 1$/m);
  } finally { env.cleanup(); }
});

// --- aetheus-agent-read-grant -------------------------------------------------------------------
// Root-owned helper run by aetheus-agent-read-grant.service before every agent start: read-only POSIX ACLs
// for the agent account on the SSH configuration. It is run for real on a fake tree; setfacl is a stand-in
// that records the call (the helper never names a path to setfacl, only the open file /proc/self/fd/3). The
// test account stands for root (/etc/ssh) and for the login user: it owns every file of the tree, so the
// owner check passes, or is made to fail by the uid the passwd line claims.
const ownUid = Number(spawnSync("sh", ["-c", "id -u"], { encoding: "utf8" }).stdout.trim());

// The tests that depend on file modes (the world-readable files are skipped), on real symbolic links
// and on FIFOs need a file system that has them: a Windows checkout (Git Bash on NTFS) has none, its
// chmod does nothing. They are skipped there, with that reason, and run on Linux and macOS (CI, WSL,
// docker run --user 1000 -v <repo>:/r -w /r node node --test deploy/scripts/agent-host-helpers.test.mjs).
const posixFiles = (() => {
  const dir = mkdtempSync(join(tmpdir(), "aetheus-posix-")).replace(/\\/g, "/");
  try {
    const probe = spawnSync("sh", ["-c", `cd "${shellPath(dir)}" && touch f && chmod 600 f && ln -s f l && mkfifo p && [ "$(stat -c %A f)" = "-rw-------" ] && [ -L l ] && [ -p p ]`]);
    return probe.status === 0;
  } finally { rmSync(dir, { recursive: true, force: true }); }
})();
const posixTest = (name, body) => test(name, { skip: posixFiles ? false : "needs POSIX file modes, symbolic links and FIFOs (not available on this file system)" }, body);

function setUpGrant({ stubs = {}, timeout = 5 } = {}) {
  const env = setUp("agent/aetheus-agent-read-grant", {
    stubs: { id: "exit 0", setfacl: "exit 0", ...stubs },
    rewrite: text => text
      .replace('AGENT="#{AGENT_USER}#"', 'AGENT="aetheus-agent"')
      .replace("PATH=/usr/sbin:/usr/bin:/sbin:/bin", 'PATH="$PATH:/usr/sbin:/usr/bin:/sbin:/bin"')
      .replace("OPEN_TIMEOUT=5", `OPEN_TIMEOUT=${timeout}`)
      .replace("f 0 r", `f ${ownUid} r`)
  });
  const rooted = env.text.replace('echo "0 /root"', `echo "${ownUid} ${env.shRoot}/home/root"`);
  writeFileSync(`${env.root}/aetheus-agent-read-grant`, rooted);
  assert.ok(rooted.includes(`SSHD_CONFIG=${env.root}/etc/ssh/sshd_config`), "the sshd paths were rebased");
  assert.ok(rooted.includes(`f ${ownUid} r`) && rooted.includes(`"${ownUid} ${env.shRoot}/home/root"`), "the owner and the root home were rewritten");
  return env;
}

const grantCalls = env => env.calls().filter(call => call.startsWith("setfacl "));
const permissionOf = call => call.match(/ u:aetheus-agent:([rx]) /)?.[1];
const shell = command => {
  const result = spawnSync("sh", ["-c", command], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
};
const stripRoot = (env, text) => text.replaceAll(env.root, "").replaceAll(env.shRoot, "");

// Root's .ssh, the sshd files, and the files the helper must leave alone (world-readable ones, a private
// key, a file that is not a .conf), and a nologin account that is not a login user.
function grantTree(env) {
  env.directory("/etc/ssh/sshd_config.d");
  env.directory("/home/root/.ssh");
  env.write("/etc/passwd", `root:x:0:0::/root:/bin/bash\nsvc:x:${ownUid}:${ownUid}::${env.shRoot}/home/svc:/usr/sbin/nologin\n`);
  for (const [path, mode] of [
    ["/etc/ssh/sshd_config", 0o600], ["/etc/ssh/sshd_config.d/50-cloud-init.conf", 0o600],
    ["/etc/ssh/sshd_config.d/10-open.conf", 0o644], ["/etc/ssh/sshd_config.d/notes.txt", 0o600],
    ["/home/root/.ssh/authorized_keys", 0o600], ["/home/root/.ssh/id_ed25519", 0o600],
    ["/home/root/.ssh/config", 0o644], ["/home/root/.ssh/known_hosts", 0o644]
  ]) {
    env.write(path, "x\n");
    chmodSync(env.file(path), mode);
  }
  chmodSync(env.file("/home/root/.ssh"), 0o700);
}

posixTest("aetheus-agent-read-grant grants read on the SSH files and traverse on .ssh, only r and x", () => {
  const env = setUpGrant();
  try {
    grantTree(env);
    const result = env.run([]);
    assert.equal(result.status, 0, result.stderr);
    const granted = [...result.stdout.matchAll(/may now read (\S+) \((\w)\)/g)].map(m => `${m[2]} ${stripRoot(env, m[1])}`);
    assert.deepEqual(granted, [
      "r /etc/ssh/sshd_config", "r /etc/ssh/sshd_config.d/50-cloud-init.conf",
      "x /home/root/.ssh", "r /home/root/.ssh/authorized_keys"
    ]);
    // Only r and x, only on the open file; the 0644 drop-in, the .txt, the private key and the 0644 files
    // of .ssh get no entry.
    const calls = grantCalls(env);
    assert.equal(calls.length, 4);
    for (const call of calls) assert.match(call, /^setfacl -m u:aetheus-agent:[rx] \/proc\/self\/fd\/3$/);
    assert.deepEqual(calls.map(permissionOf), ["r", "r", "x", "r"]);
  } finally { env.cleanup(); }
});

test("aetheus-agent-read-grant leaves a file alone when its owner is not the expected user", () => {
  const env = setUpGrant();
  try {
    const stranger = ownUid === 4242 ? 4243 : 4242;
    env.directory("/home/alice/.ssh");
    env.write("/etc/passwd", `alice:x:${stranger}:${stranger}::${env.shRoot}/home/alice:/bin/bash\n`);
    env.write("/home/alice/.ssh/authorized_keys", "x\n");
    chmodSync(env.file("/home/alice/.ssh"), 0o700);
    chmodSync(env.file("/home/alice/.ssh/authorized_keys"), 0o600);
    // /etc/ssh files must be owned by root: here they belong to the test account.
    env.write("/etc/ssh/sshd_config", "x\n");
    chmodSync(env.file("/etc/ssh/sshd_config"), 0o600);
    const script = `${env.root}/aetheus-agent-read-grant`;
    writeFileSync(script, readFileSync(script, "utf8").replace(`f ${ownUid} r`, "f 0 r"));
    const result = env.run([]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal((result.stderr.match(/is not owned by uid/g) ?? []).length, 3, result.stderr);
    assert.match(result.stderr, new RegExp(`alice/\\.ssh is not owned by uid ${stranger}`));
    assert.match(result.stderr, /sshd_config is not owned by uid 0/);
    assert.deepEqual(grantCalls(env), []);
  } finally { env.cleanup(); }
});

posixTest("aetheus-agent-read-grant refuses a symbolic link, even to a file the right user owns", () => {
  const env = setUpGrant();
  try {
    grantTree(env);
    env.write("/home/root/real-keys", "x\n");
    chmodSync(env.file("/home/root/real-keys"), 0o600);
    env.write("/etc/ssh/real.conf", "x\n");
    chmodSync(env.file("/etc/ssh/real.conf"), 0o600);
    // The targets have the right owner: only the link itself can stop the grant.
    shell(`ln -s "${env.shRoot}/home/root/real-keys" "${env.shRoot}/home/root/.ssh/authorized_keys2" && ln -s "${env.shRoot}/etc/ssh/real.conf" "${env.shRoot}/etc/ssh/sshd_config.d/60-link.conf"`);
    const result = env.run([]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /authorized_keys2 is a symbolic link, left alone/);
    assert.match(result.stderr, /60-link\.conf is a symbolic link, left alone/);
    // Only the four regular files of the first case are granted.
    assert.equal(grantCalls(env).length, 4);
    assert.ok(!/real-keys|real\.conf|authorized_keys2|60-link/.test(result.stdout));
  } finally { env.cleanup(); }
});

posixTest("aetheus-agent-read-grant does not block on a FIFO, planted or swapped in after the check", () => {
  const planted = setUpGrant();
  try {
    grantTree(planted);
    shell(`mkfifo "${planted.shRoot}/home/root/.ssh/authorized_keys2"`);
    const started = Date.now();
    const result = planted.run([]);
    assert.equal(result.status, 0, result.stderr);
    assert.ok(Date.now() - started < 4000, "a FIFO made the helper wait");
    assert.equal(grantCalls(planted).length, 4);
  } finally { planted.cleanup(); }

  // The race itself: the kind check is told "regular file" for this one path (a stand-in for stat), then
  // the open meets the FIFO. The open is killed at the timeout and the other files still get their grant.
  const swapped = setUpGrant({
    timeout: 1,
    stubs: { stat: 'case "$*" in *authorized_keys2*) echo "regular file"; exit 0 ;; esac\nPATH="${PATH#*:}"\nexec stat "$@"' }
  });
  try {
    grantTree(swapped);
    shell(`mkfifo "${swapped.shRoot}/home/root/.ssh/authorized_keys2"`);
    const started = Date.now();
    const result = swapped.run([]);
    assert.equal(result.status, 0, result.stderr);
    assert.ok(Date.now() - started < 10000, "the helper waited on the FIFO");
    assert.match(result.stderr, /authorized_keys2 did not open within 1s, skipped/);
    assert.equal(grantCalls(swapped).length, 4);
  } finally { swapped.cleanup(); }
});

test("aetheus-agent-read-grant --revoke removes the agent entry from the same files and grants nothing", () => {
  const env = setUpGrant();
  try {
    grantTree(env);
    const result = env.run(["--revoke"]);
    assert.equal(result.status, 0, result.stderr);
    const calls = grantCalls(env);
    // sshd_config, the two drop-ins (the world-readable one too: a revoke does not look at the mode), .ssh, authorized_keys.
    assert.equal(calls.length, 5);
    for (const call of calls) assert.equal(call, "setfacl -x u:aetheus-agent /proc/self/fd/3");
  } finally { env.cleanup(); }
});

test("aetheus-agent-read-grant --list-ssh-dirs prints the .ssh directories and changes nothing", () => {
  const env = setUpGrant();
  try {
    grantTree(env);
    const result = env.run(["--list-ssh-dirs"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), `${env.shRoot}/home/root/.ssh`);
    assert.deepEqual(grantCalls(env), []);
  } finally { env.cleanup(); }
});
