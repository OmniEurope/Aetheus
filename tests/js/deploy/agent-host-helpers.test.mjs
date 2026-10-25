// SPDX-License-Identifier: EUPL-1.2
// The root-owned helpers of deploy/agent-host-config run under a NOPASSWD sudo grant, so what they
// refuse is the boundary between the agent account and root. They are run here for real, on a copy
// whose /etc, /var and /opt are moved under a temporary root and whose system commands (systemctl,
// apt-get, ufw, iptables...) are stand-ins on PATH that record every call. Nothing touches the host.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
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
    assert.equal(result.stdout.trim(), "aetheus-certbot-helper-version: 6");
    const script = readFileSync(join(helpersRoot, "certbot/aetheus-certbot-manage"), "utf8");
    assert.match(script, /^# aetheus-certbot-helper-version: 6$/m);
  } finally { env.cleanup(); }
});

// --- aetheus-certbot-manage normalize: the server-wide ACME alias ---------------------------------
// Lets Encrypt follows the HTTP to HTTPS redirect of a vhost Aetheus does not own, and the HTTPS vhost
// proxies everything to its application, which answers 404 to the challenge: normalizing a lineage
// to the web root then fails its dry run. The server-level conf therefore also takes the challenge path
// out of every proxy rule (ProxyPass exclusion, and the no-proxy variable for ProxyPass inside <Location>),
// and the helper writes that conf itself before converting, so a host whose conf predates it is repaired
// by the verb. The Apache behaviour of these lines is proved against a real Apache outside this suite
// (Ubuntu 24.04, apache2 2.4); here the helper is run with stand-ins for apache2ctl, systemctl and certbot.
const acmeConfPath = "/etc/apache2/conf-available/aetheus-acme-webroot.conf";
const acmeEnabledPath = "/etc/apache2/conf-enabled/aetheus-acme-webroot.conf";
const staleAcmeConf = "Alias /.well-known/acme-challenge/ /var/www/aetheus-acme/.well-known/acme-challenge/\n";
const acmeConfTemplate = readFileSync(join(helpersRoot, "certbot/aetheus-acme-webroot.conf"), "utf8");

function normalizeSetUp({ stubs = {}, enabled = true, current = staleAcmeConf } = {}) {
  const env = setUp("certbot/aetheus-certbot-manage", { stubs });
  env.write("/etc/letsencrypt/renewal/shop.example.com.conf", "authenticator = apache\n");
  if (enabled) {
    env.write(acmeConfPath, current);
    env.write(acmeEnabledPath, current);
  }
  const wanted = acmeConfTemplate.replaceAll("$ACME_WEBROOT", `${env.root}/var/www/acme`);
  const index = prefix => env.calls().findIndex(call => call.startsWith(prefix));
  return { ...env, wanted, index };
}

test("the server-level ACME conf takes the challenge path out of every proxy rule", () => {
  assert.match(acmeConfTemplate, /^\s*ProxyPass \/\.well-known\/acme-challenge\/ !$/m);
  assert.match(acmeConfTemplate, /^\s*SetEnvIf Request_URI "\^\/\\\.well-known\/acme-challenge\/" no-proxy$/m);
  assert.match(acmeConfTemplate, /<IfModule mod_proxy\.c>/);
  assert.match(acmeConfTemplate, /<IfModule mod_setenvif\.c>/);
});

test("normalize rewrites the ACME conf, tests Apache, reloads it, then converts the lineages", () => {
  const env = normalizeSetUp();
  try {
    assert.ok(env.text.includes(`ACME_CONF=${env.root}/etc/apache2/conf-available/`), "the conf path was rebased");
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(env.file(acmeConfPath), "utf8"), env.wanted);
    const [enable, check, reload, convert] = [
      env.index("a2enconf aetheus-acme-webroot"), env.index("apache2ctl configtest"),
      env.index("systemctl reload apache2"), env.index("certbot reconfigure --cert-name shop.example.com")];
    assert.ok(enable >= 0 && check > enable && reload > check && convert > reload, env.calls().join("\n"));
  } finally { env.cleanup(); }
});

test("normalize restores the previous ACME conf and converts nothing when Apache rejects the new one", () => {
  const env = normalizeSetUp({ stubs: { apache2ctl: '[ "$1" != configtest ] || exit 1' } });
  try {
    const result = env.run(["normalize"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Apache rejected the new ACME configuration/);
    assert.equal(readFileSync(env.file(acmeConfPath), "utf8"), staleAcmeConf);
    assert.equal(env.index("systemctl reload apache2"), -1);
    assert.equal(env.index("certbot reconfigure --cert-name"), -1);
  } finally { env.cleanup(); }
});

test("normalize restores the previous ACME conf and converts nothing when Apache cannot reload", () => {
  const env = normalizeSetUp({ stubs: { systemctl: '[ "$1 $2" != "reload apache2" ] || exit 1' } });
  try {
    const result = env.run(["normalize"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Apache could not reload the new ACME configuration/);
    assert.equal(readFileSync(env.file(acmeConfPath), "utf8"), staleAcmeConf);
    assert.equal(env.index("certbot reconfigure --cert-name"), -1);
  } finally { env.cleanup(); }
});

test("normalize leaves Apache alone when its ACME conf is already current", () => {
  const env = normalizeSetUp();
  try {
    env.write(acmeConfPath, env.wanted);
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(env.index("systemctl reload apache2"), -1);
    assert.equal(env.index("apache2ctl configtest"), -1);
    assert.ok(env.index("certbot reconfigure --cert-name shop.example.com") >= 0);
  } finally { env.cleanup(); }
});

test("normalize converts no lineage and writes no conf on a host without the Aetheus ACME alias", () => {
  const env = normalizeSetUp({ enabled: false });
  try {
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Aetheus ACME alias not enabled in Apache: no lineage converted/);
    assert.ok(!existsSync(env.file(acmeConfPath)));
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});

test("normalize keeps the previous renewal settings and fails when certbot refuses a lineage", () => {
  const env = normalizeSetUp({ stubs: { certbot: '[ "$2" != "--cert-name" ] || exit 1' } });
  try {
    const result = env.run(["normalize"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /shop\.example\.com: reconfigure failed, previous renewal settings kept/);
    assert.equal(readFileSync(env.file("/etc/letsencrypt/renewal/shop.example.com.conf"), "utf8"), "authenticator = apache\n");
  } finally { env.cleanup(); }
});

// --- aetheus-certbot-manage normalize: proxying RewriteRule lines (recette R-2) -------------------
// A RewriteRule with the P flag in a virtual host is translated before mod_alias, so the server-wide
// alias and its ProxyPass exclusion never see the challenge; what keeps it out is a RewriteCond naming the
// challenge ahead of the rule. The helper adds it to the Apache files it may edit, tests and reloads
// Apache, puts the files back when Apache refuses, and records the rules it could not protect. The Apache
// behaviour (the challenge reaches the web root, the application still answers every other path, for 15
// vhost shapes) is proved against a real Apache 2.4.58 outside this suite; here the helper is run with
// stand-ins for apache2ctl, systemctl and certbot, on files of a temporary tree.
const siteFile = "/etc/apache2/sites-enabled/shop.conf";
const challengeCond = "RewriteCond %{REQUEST_URI} !^/\\.well-known/acme-challenge/";
const proxyingSite = [
  "<VirtualHost *:80>",
  "    ServerName shop.example.com",
  "    RewriteEngine on",
  "    RewriteRule ^/(.*)$ http://127.0.0.1:5000/$1 [P]",
  "</VirtualHost>",
  "<VirtualHost *:443>",
  "    ServerName shop.example.com",
  "    RewriteEngine on",
  "    RewriteCond %{HTTP:Upgrade} websocket [NC]",
  "    RewriteRule ^/(.*)$ ws://127.0.0.1:5000/$1 [P,L]",
  "    RewriteRule ^/(.*)$ http://127.0.0.1:5000/$1 [QSA,proxy]",
  "</VirtualHost>",
  ""
].join("\n");

// The ACME conf is already current, so the only Apache test and reload are the ones of the rewrite step.
function rewriteSetUp({ site = proxyingSite, stubs = {} } = {}) {
  const env = normalizeSetUp({ stubs });
  env.write(acmeConfPath, env.wanted);
  env.write(acmeEnabledPath, env.wanted);
  env.write(siteFile, site);
  const read = path => readFileSync(env.file(path), "utf8");
  const count = prefix => env.calls().filter(call => call.startsWith(prefix)).length;
  return { ...env, read, count };
}

test("normalize puts a challenge condition ahead of each proxying RewriteRule, except the websocket-only one", () => {
  const env = rewriteSetUp();
  try {
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /ACME challenge kept out of the proxying RewriteRule of .*shop\.conf/);
    const lines = env.read(siteFile).split("\n");
    const at = text => lines.findIndex(line => line.includes(text));
    // http vhost: the condition sits right before the rule.
    assert.equal(lines[at("http://127.0.0.1:5000/$1 [P]") - 1].trim(), challengeCond);
    // https vhost, recette R-11: the websocket rule only proxies an Upgrade request, which the challenge never
    // is, so it is left as it was.
    assert.equal(lines[at("RewriteCond %{HTTP:Upgrade}") - 1].trim(), "RewriteEngine on");
    // the second rule of the https vhost has no condition of its own: it gets one.
    assert.equal(lines[at("[QSA,proxy]") - 1].trim(), challengeCond);
    assert.equal(lines.filter(line => line.includes("aetheus-acme-webroot: keeps")).length, 2);
    // the original is kept once, outside Apache's directory, for a manual return.
    assert.equal(readFileSync(env.file("/var/lib/aetheus-certbot/vhost-originals/" + readdirSync(env.file("/var/lib/aetheus-certbot/vhost-originals"))[0]), "utf8"), proxyingSite);
    assert.ok(existsSync(env.file("/var/lib/aetheus-certbot/vhost-originals")), "originals directory");
    const check = env.index("apache2ctl configtest");
    const reload = env.index("systemctl reload apache2");
    const convert = env.index("certbot reconfigure --cert-name shop.example.com");
    assert.ok(check >= 0 && reload > check && convert > reload, env.calls().join("\n"));
  } finally { env.cleanup(); }
});

test("normalize adds the challenge condition once: a second run changes nothing and reloads nothing", () => {
  const env = rewriteSetUp();
  try {
    assert.equal(env.run(["normalize"]).status, 0);
    const once = env.read(siteFile);
    const reloads = env.count("systemctl reload apache2");
    const second = env.run(["normalize"]);
    assert.equal(second.status, 0, second.stderr);
    assert.equal(env.read(siteFile), once);
    assert.equal(env.count("systemctl reload apache2"), reloads);
    assert.doesNotMatch(second.stdout, /kept out of the proxying RewriteRule/);
  } finally { env.cleanup(); }
});

test("normalize leaves a rule that already names the challenge, one that does not proxy, and per-directory rules alone", () => {
  const site = [
    "<VirtualHost *:443>",
    "    RewriteEngine on",
    "    RewriteCond %{REQUEST_URI} !^/.well-known/acme-challenge/",
    "    RewriteRule ^/(.*)$ http://127.0.0.1:5000/$1 [P]",
    "    RewriteCond %{HTTPS} off",
    "    RewriteRule ^/(.*)$ https://%{HTTP_HOST}/$1 [R=301,L]",
    "    RewriteRule ^/(.*)$ /index.html [L]",
    "    <Location /api>",
    "        RewriteEngine on",
    "        RewriteRule ^/api/(.*)$ http://127.0.0.1:5001/$1 [P]",
    "    </Location>",
    "</VirtualHost>",
    ""
  ].join("\n");
  const env = rewriteSetUp({ site });
  try {
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(env.read(siteFile), site);
    assert.equal(env.count("systemctl reload apache2"), 0);
    assert.equal(readFileSync(env.file("/var/lib/aetheus-certbot/acme-proxy-rules"), "utf8"), "");
  } finally { env.cleanup(); }
});

test("normalize reads a proxy flag in any case, a rule written over two lines, and tab separators", () => {
  const site = "<VirtualHost *:80>\n\trewriteengine\tOn\n\trewriterule\t^/(.*)$\thttp://127.0.0.1:5000/$1\t[L,p]\n"
    + "    RewriteRule ^/(.*)$ \\\n        http://127.0.0.1:5000/$1 [P]\n</VirtualHost>\n";
  const env = rewriteSetUp({ site });
  try {
    assert.equal(env.run(["normalize"]).status, 0);
    const lines = env.read(siteFile).split("\n");
    assert.equal(lines.filter(line => line.trim() === challengeCond).length, 2);
    assert.equal(lines[lines.findIndex(line => line.startsWith("\trewriterule")) - 1].trim(), challengeCond);
    // the guard goes before the first physical line of the two-line rule, never between its lines
    const multi = lines.findIndex(line => line.trim() === "RewriteRule ^/(.*)$ \\");
    assert.equal(lines[multi - 1].trim(), challengeCond);
    assert.equal(lines[multi + 1].trim(), "http://127.0.0.1:5000/$1 [P]");
  } finally { env.cleanup(); }
});

for (const [label, stubs, error] of [
  ["rejects them", { apache2ctl: '[ "$1" != configtest ] || exit 1' }, /Apache rejected the ACME challenge condition added to its RewriteRule lines/],
  ["cannot reload", { systemctl: '[ "$1 $2" != "reload apache2" ] || exit 1' }, /Apache could not reload the ACME challenge condition added to its RewriteRule lines/]
]) {
  test(`normalize puts every Apache file back and converts nothing when Apache ${label}`, () => {
    const env = rewriteSetUp({ stubs });
    try {
      env.write("/etc/apache2/conf-enabled/other.conf", "<VirtualHost *:81>\n    RewriteRule ^/(.*)$ http://127.0.0.1:6000/$1 [P]\n</VirtualHost>\n");
      const result = env.run(["normalize"]);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, error);
      assert.match(result.stderr, /the previous files were restored and no lineage was converted/);
      assert.equal(env.read(siteFile), proxyingSite);
      assert.equal(env.read("/etc/apache2/conf-enabled/other.conf"), "<VirtualHost *:81>\n    RewriteRule ^/(.*)$ http://127.0.0.1:6000/$1 [P]\n</VirtualHost>\n");
      assert.equal(env.index("certbot reconfigure --cert-name"), -1);
    } finally { env.cleanup(); }
  });
}

// Production 2026-10-23: the agent's own vhosts could not be written (fs.protected_regular refuses a
// redirection to root), and the helper still said it had kept the challenge out of them.
test("normalize fails, puts every file back and converts nothing when a vhost cannot be written", () => {
  const cp = 'case "$*" in *"/new "*shop.conf) echo "cp: cannot create regular file: Permission denied" >&2; exit 1 ;; esac\nexec /usr/bin/cp "$@"';
  const env = rewriteSetUp({ stubs: { cp } });
  try {
    const other = "<VirtualHost *:81>\n    RewriteRule ^/(.*)$ http://127.0.0.1:6000/$1 [P]\n</VirtualHost>\n";
    env.write("/etc/apache2/conf-enabled/other.conf", other);
    const result = env.run(["normalize"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Could not write the ACME challenge condition into .*shop\.conf: the previous files were restored and no lineage was converted/);
    assert.doesNotMatch(result.stdout, /kept out of the proxying RewriteRule of .*shop\.conf/);
    assert.equal(env.read(siteFile), proxyingSite);
    assert.equal(env.read("/etc/apache2/conf-enabled/other.conf"), other);
    assert.equal(env.index("systemctl reload apache2"), -1);
    assert.equal(env.index("certbot reconfigure --cert-name"), -1);
  } finally { env.cleanup(); }
});

test("normalize records, without editing, a proxying rule outside the Apache directory", () => {
  const outside = "RewriteEngine on\nRewriteRule ^/(.*)$ http://127.0.0.1:5000/$1 [P]\n";
  const env = rewriteSetUp({ site: "<VirtualHost *:80>\n    Include /srv/site.inc\n</VirtualHost>\n" });
  try {
    env.write("/srv/site.inc", outside);
    const include = env.file("/srv/site.inc");
    const bin = `${env.root}/bin`;
    writeFileSync(`${bin}/apache2ctl`, `#!/bin/sh\necho "apache2ctl $*" >> "${env.root}/calls.log"\n[ "$1" != "-t" ] || echo "  (1) ${include}"\n`);
    const result = env.run(["normalize"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(include, "utf8"), outside);
    const rules = readFileSync(env.file("/var/lib/aetheus-certbot/acme-proxy-rules"), "utf8").trim().split("\n");
    assert.equal(rules.length, 1);
    assert.match(rules[0], /^rule=.*site\.inc:2$/);
  } finally { env.cleanup(); }
});

test("renewal-check records the proxying rules that still let the challenge through, and edits no file", () => {
  const env = rewriteSetUp();
  try {
    const result = env.run(["renewal-check"]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(env.read(siteFile), proxyingSite);
    const rules = readFileSync(env.file("/var/lib/aetheus-certbot/acme-proxy-rules"), "utf8").trim().split("\n");
    // the rule of the http vhost, then the last rule; the websocket rule (line 10, its condition on line 9) is
    // not reported (recette R-11): the challenge never carries an Upgrade header.
    assert.equal(rules.length, 2, rules.join("\n"));
    assert.match(rules[0], /^rule=.*shop\.conf:4$/);
    assert.match(rules[1], /^rule=.*shop\.conf:11$/);
  } finally { env.cleanup(); }
});

// Recette R-11: the websocket condition keeps the challenge out only when every condition of the rule must
// hold. An [OR] makes it one alternative among others, and a negated pattern lets a plain request through.
for (const [label, conditions] of [
  ["an [OR] chain", ["    RewriteCond %{HTTP:Upgrade} websocket [NC,OR]", "    RewriteCond %{HTTP_HOST} ^shop\\.example\\.com$"]],
  ["a negated pattern", ["    RewriteCond %{HTTP:Upgrade} !websocket [NC]"]]
]) {
  test(`renewal-check still reports a proxying rule whose websocket condition sits in ${label}`, () => {
    const site = ["<VirtualHost *:443>", "    RewriteEngine on", ...conditions,
      "    RewriteRule ^/(.*)$ http://127.0.0.1:5000/$1 [P,L]", "</VirtualHost>", ""].join("\n");
    const env = rewriteSetUp({ site });
    try {
      assert.equal(env.run(["renewal-check"]).status, 0);
      const rules = readFileSync(env.file("/var/lib/aetheus-certbot/acme-proxy-rules"), "utf8").trim().split("\n");
      assert.equal(rules.length, 1, rules.join("\n"));
      assert.match(rules[0], /^rule=.*shop\.conf:3$/);
    } finally { env.cleanup(); }
  });
}

test("renewal-check does not report the websocket rule of the Aetheus API vhost template, in any case or quoting", () => {
  const site = ["<VirtualHost *:443>", "    RewriteEngine On",
    "    RewriteCond %{HTTP:Upgrade} \"=WebSocket\" [NC]", "    RewriteCond %{HTTP:Connection} upgrade [NC]",
    "    RewriteRule ^/?(.*) ws://127.0.0.1:5000/$1 [P,L]", "</VirtualHost>", ""].join("\n");
  const env = rewriteSetUp({ site });
  try {
    assert.equal(env.run(["renewal-check"]).status, 0);
    assert.equal(readFileSync(env.file("/var/lib/aetheus-certbot/acme-proxy-rules"), "utf8"), "");
  } finally { env.cleanup(); }
});

// --- aetheus-certbot-manage remove-name (recette R-12) ---------------------------------------------
// The helper reissues a lineage with every name it covers but one, through the ACME web root, and never
// deletes it. certbot is a stand-in: `certbot certificates` lists the names below, any other call is recorded.
const listedNames = "aetheus.sonytumen.com app.aetheus.sonytumen.com aetheus-api.sonytumen.com";
function removeNameSetUp({ names = listedNames, alias = true, certonly = "exit 0" } = {}) {
  const certbot = `case "$1" in certificates) printf 'Found the following certs:\\n  Certificate Name: aetheus.sonytumen.com\\n    Domains: %s\\n' "${names}" ;; certonly) ${certonly} ;; esac`;
  const env = setUp("certbot/aetheus-certbot-manage", { stubs: { certbot } });
  if (alias) env.write(acmeEnabledPath, "Alias /.well-known/acme-challenge/ /var/www/acme/.well-known/acme-challenge/\n");
  const certonlyCalls = () => env.calls().filter(call => call.startsWith("certbot certonly"));
  return { ...env, certonlyCalls };
}

test("remove-name reissues the certificate in place with every other name, through the ACME web root", () => {
  const env = removeNameSetUp();
  try {
    const result = env.run(["remove-name", "aetheus.sonytumen.com", "AETHEUS-API.sonytumen.com"]);
    assert.equal(result.status, 0, result.stderr);
    const calls = env.certonlyCalls();
    assert.equal(calls.length, 1, env.calls().join("\n"));
    assert.equal(calls[0], `certbot certonly --cert-name aetheus.sonytumen.com --webroot --webroot-path ${env.root}/var/www/acme `
      + "--non-interactive --agree-tos --renew-with-new-domains -d aetheus.sonytumen.com -d app.aetheus.sonytumen.com");
    assert.match(result.stdout, /aetheus\.sonytumen\.com reissued without aetheus-api\.sonytumen\.com, now covering: aetheus\.sonytumen\.com app\.aetheus\.sonytumen\.com/);
    assert.equal(env.calls().filter(call => /^certbot (delete|revoke)/.test(call)).length, 0);
  } finally { env.cleanup(); }
});

for (const [label, args, options, error] of [
  ["a name the certificate does not cover", ["aetheus.sonytumen.com", "other.example.com"], {}, /does not cover other\.example\.com/],
  ["the last name of the certificate", ["api.aetheus.sonytumen.com", "api.aetheus.sonytumen.com"], { names: "api.aetheus.sonytumen.com" }, /is the last name of api\.aetheus\.sonytumen\.com: delete the certificate instead/],
  ["a certificate certbot does not know", ["gone.example.com", "gone.example.com"], { names: "" }, /certificate gone\.example\.com not found/],
  ["a name with a path or an option", ["aetheus.sonytumen.com", "-d"], {}, /invalid domain name/],
  ["a multi-line name", ["aetheus.sonytumen.com", "app.aetheus.sonytumen.com\nevil.example.com"], {}, /invalid domain name/],
  ["an invalid certificate name", ["../live", "app.aetheus.sonytumen.com"], {}, /invalid cert name/],
  ["a host without the Aetheus ACME alias", ["aetheus.sonytumen.com", "app.aetheus.sonytumen.com"], { alias: false }, /ACME alias not enabled/],
  // A listed "*" is refused as written: globbed, it would become the file names of the working directory.
  ["a wildcard name, never expanded to file names", ["aetheus.sonytumen.com", "aetheus-api.sonytumen.com"], { names: "aetheus.sonytumen.com * aetheus-api.sonytumen.com" }, /refuses to pass on: \*$/m]
]) {
  test(`remove-name refuses ${label} and runs no certbot certonly`, () => {
    const env = removeNameSetUp(options);
    try {
      const result = env.run(["remove-name", ...args]);
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, error);
      assert.equal(env.certonlyCalls().length, 0, env.calls().join("\n"));
    } finally { env.cleanup(); }
  });
}

test("remove-name fails honestly when certbot cannot reissue, and deletes nothing", () => {
  const env = removeNameSetUp({ certonly: "exit 1" });
  try {
    const result = env.run(["remove-name", "aetheus.sonytumen.com", "aetheus-api.sonytumen.com"]);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /could not reissue aetheus\.sonytumen\.com without aetheus-api\.sonytumen\.com: the certificate was left as it was/);
    assert.equal(env.calls().filter(call => /^certbot (delete|revoke)/.test(call)).length, 0);
  } finally { env.cleanup(); }
});

// --- aetheus-certbot-manage renewal-check (recette R1-7) ----------------------------------------
// The helper runs `certbot renew --dry-run` and records, for the agent, the lineages that failed with
// certbot's reason and, on a failed run, the last meaningful lines of its output. certbot is a stand-in
// printing the output below. These outputs are rebuilt from certbot's own messages (renewal.py, the
// authenticator report and the end-of-run summary), they are NOT a capture from the failing server.
const certbotRule = "- ".repeat(39).trim();
const certbotHeader = (name, domains) => `Saving debug log to /var/log/letsencrypt/letsencrypt.log

${certbotRule}
Processing /etc/letsencrypt/renewal/${name}.conf
${certbotRule}
Account registered.
Simulating renewal of an existing certificate for ${domains}
`;
const certbotSummary = names => `
${certbotRule}
All simulated renewals failed. The following certificates could not be renewed:
${names.map(name => `  /etc/letsencrypt/live/${name}/fullchain.pem (failure)`).join("\n")}
${certbotRule}

${names.length} renew failure(s), 0 parse failure(s)
`;
const failedChallenge = (name, domain, type, detail) => `${certbotHeader(name, domain)}Certbot failed to authenticate some domains (authenticator: webroot). The Certificate Authority reported these problems:
  Domain: ${domain}
  Type:   ${type}
  Detail: ${detail}

Hint: The Certificate Authority failed to download the temporary challenge files created by Certbot. Ensure that the listed domains serve their content from the provided --webroot-path/-w and that files created there can be downloaded from the internet.

Failed to renew certificate ${name} with error: Some challenges have failed.
`;

const renewalCheck = (output, { exit = 1 } = {}) => {
  const env = setUp("certbot/aetheus-certbot-manage", {
    stubs: { certbot: `cat <<'CERTBOT_OUTPUT'\n${output}\nCERTBOT_OUTPUT\nexit ${exit}` }
  });
  try {
    const result = env.run(["renewal-check"]);
    const record = readFileSync(env.file("/var/lib/aetheus-certbot/renewal-check"), "utf8").split("\n").filter(Boolean);
    return { result, record, failed: record.filter(l => l.startsWith("failed=")), tail: record.filter(l => l.startsWith("output=")) };
  } finally { env.cleanup(); }
};

for (const [label, output, name, reason] of [
  ["a firewalled port 80", failedChallenge("shop.example.com", "shop.example.com", "connection",
    "203.0.113.10: Fetching http://shop.example.com/.well-known/acme-challenge/Kd7xq: Timeout during connect (likely firewall problem)"),
    "shop.example.com", /^Some challenges have failed\. \(shop\.example\.com: 203\.0\.113\.10: Fetching .*Timeout during connect \(likely firewall problem\)\)$/],
  ["a refused connection", failedChallenge("old.example.org", "old.example.org", "connection",
    "198.51.100.7: Fetching http://old.example.org/.well-known/acme-challenge/abc: Connection refused"),
    "old.example.org", /Connection refused\)$/],
  ["a challenge answered 404", failedChallenge("app.example.com", "app.example.com", "unauthorized",
    "203.0.113.20: Invalid response from http://app.example.com/.well-known/acme-challenge/tok: 404"),
    "app.example.com", /Invalid response from http:\/\/app\.example\.com\/\.well-known\/acme-challenge\/tok: 404\)$/],
  ["a rate limit", `${certbotHeader("api.example.com", "api.example.com")}Failed to renew certificate api.example.com with error: urn:ietf:params:acme:error:rateLimited :: There were too many requests of a given type :: too many failed authorizations recently: see https://letsencrypt.org/docs/rate-limits/\n`,
    "api.example.com", /^urn:ietf:params:acme:error:rateLimited :: .*too many failed authorizations recently/],
  ["an unexpected error of the lineage", `${certbotHeader("mail.example.com", "mail.example.com")}Attempting to renew cert (mail.example.com) from /etc/letsencrypt/renewal/mail.example.com.conf produced an unexpected error: HTTPSConnectionPool(host='acme-staging-v02.api.letsencrypt.org', port=443): Max retries exceeded. Skipping.\n`,
    "mail.example.com", /^HTTPSConnectionPool\(host=.*Max retries exceeded\. Skipping\.$/],
  ["an unexpected error naming the configuration file", `${certbotHeader("vpn.example.com", "vpn.example.com")}Renewal configuration file /etc/letsencrypt/renewal/vpn.example.com.conf (cert: vpn.example.com) produced an unexpected error: Plugin 'nginx' not found. Skipping.\n`,
    "vpn.example.com", /^Plugin 'nginx' not found\. Skipping\.$/]
]) {
  test(`renewal-check records the lineage and its reason for ${label}, with the output tail`, () => {
    const { failed, tail, record } = renewalCheck(`${output}${certbotSummary([name])}`);
    assert.equal(record[1], "exit_code=1");
    assert.equal(failed.length, 1, failed.join("\n"));
    const [recorded, why] = failed[0].slice("failed=".length).split("\t");
    assert.equal(recorded, name);
    assert.match(why, reason);
    assert.match(tail[0], /^output=certbot renew --dry-run ran for \d+ s and exited with code 1$/);
    assert.ok(tail.some(line => line.includes("1 renew failure(s), 0 parse failure(s)")), tail.join("\n"));
  });
}

test("renewal-check keeps the second line of a reason that ends with a colon (a domain without web root)", () => {
  const output = `${certbotHeader("docs.example.com", "docs.example.com and www.docs.example.com")}Failed to renew certificate docs.example.com with error: Missing command line flag or config entry for this setting:
Input the webroot for www.docs.example.com:

(You can set this with the --webroot-path flag)
${certbotSummary(["docs.example.com"])}`;
  const { failed } = renewalCheck(output);
  assert.equal(failed.length, 1);
  assert.equal(failed[0], "failed=docs.example.com\tMissing command line flag or config entry for this setting: Input the webroot for www.docs.example.com:");
});

test("renewal-check names every failed lineage, once, and lists the others of the summary without reason", () => {
  const output = `${failedChallenge("shop.example.com", "shop.example.com", "connection", "203.0.113.10: Timeout during connect (likely firewall problem)")}
${certbotHeader("blog.example.com", "blog.example.com")}Failed to renew certificate blog.example.com with error: too many certificates already issued
${certbotSummary(["shop.example.com", "blog.example.com", "wiki.example.com"])}`;
  const { failed } = renewalCheck(output);
  assert.deepEqual(failed.map(line => line.split("\t")[0]), ["failed=shop.example.com", "failed=blog.example.com", "failed=wiki.example.com"]);
  assert.equal(failed[2], "failed=wiki.example.com\t");
});

test("renewal-check lists a lineage of the summary even when only the end-of-run summary names it", () => {
  const { failed, tail } = renewalCheck(`Saving debug log to /var/log/letsencrypt/letsencrypt.log\n${certbotSummary(["lonely.example.com"])}`);
  assert.deepEqual(failed, ["failed=lonely.example.com\t"]);
  assert.ok(tail.length > 1);
});

test("renewal-check with no known message still records the last meaningful lines of the output", () => {
  const output = `Saving debug log to /var/log/letsencrypt/letsencrypt.log
${certbotRule}
Another instance of Certbot is already running.
Ask for help or search for solutions at https://community.letsencrypt.org. See the logfile /var/log/letsencrypt/letsencrypt.log or re-run Certbot with -v for more details.
`;
  const { failed, tail, record } = renewalCheck(output);
  assert.deepEqual(failed, []);
  assert.equal(record[1], "exit_code=1");
  assert.deepEqual(tail.slice(1), [
    "output=Another instance of Certbot is already running.",
    "output=Ask for help or search for solutions at https://community.letsencrypt.org. See the logfile /var/log/letsencrypt/letsencrypt.log or re-run Certbot with -v for more details."
  ]);
});

test("renewal-check keeps only the last fifteen meaningful lines, each at most 300 characters", () => {
  const lines = Array.from({ length: 40 }, (_, index) => `line ${index + 1} ${"x".repeat(index === 39 ? 400 : 5)}`);
  const { tail } = renewalCheck(lines.join("\n"));
  assert.equal(tail.length, 16);
  assert.equal(tail[1], "output=line 26 xxxxx");
  assert.equal(tail[15].length, "output=".length + 300);
});

test("renewal-check records no output line and no failure for a passing rehearsal", () => {
  const passing = `${certbotHeader("shop.example.com", "shop.example.com")}
Congratulations, all simulated renewals succeeded:
  /etc/letsencrypt/live/shop.example.com/fullchain.pem (success)`;
  const { record, failed, tail, result } = renewalCheck(passing, { exit: 0 });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(record[1], "exit_code=0");
  assert.deepEqual([failed, tail], [[], []]);
});

test("renewal-check drops control characters of certbot output before recording it", () => {
  const { tail } = renewalCheck("colored \u001b[31merror\u001b[0m with CR\r");
  assert.equal(tail[1], "output=colored [31merror[0m with CR");
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
// docker run --user 1000 -v <repo>:/r -w /r node node --test tests/js/deploy/agent-host-helpers.test.mjs).
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
