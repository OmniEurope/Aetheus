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
function setUp(helper, { stubs = {} } = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-helper-")).replace(/\\/g, "/");
  const bin = `${root}/bin`;
  mkdirSync(bin);
  const log = `${root}/calls.log`;
  for (const command of new Set([...stubbed, ...Object.keys(stubs)])) {
    const body = stubs[command] ?? "exit 0";
    writeFileSync(`${bin}/${command}`, `#!/bin/sh\necho "${command} $*" >> "${log}"\n${body}\n`);
    chmodSync(`${bin}/${command}`, 0o755);
  }
  const source = readFileSync(join(helpersRoot, helper), "utf8");
  const text = source
    .replace(/#\{ACME_WEBROOT\}#/g, `${root}/var/www/acme`)
    .replace(/(?<=^|[\s"'=(])\/usr\/sbin\/(ufw|sshd)\b/gm, `${bin}/$1`)
    .replace(/(?<=^|[\s"'=(])\/(etc|var|opt)\//gm, `${root}/$1/`);
  const script = `${root}/${helper.split("/").pop()}`;
  writeFileSync(script, text);
  const run = (args, { env = {}, input = "" } = {}) => spawnSync("sh", ["-c", wrapper], {
    encoding: "utf8",
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

// ---- DEP-002: an argument holding a line break is refused before anything runs ------------------

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

// ---- DEP-003: enable stops when the administration port cannot be allowed --------------------------

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

// ---- DEP-004: delete reports the result of both variants -------------------------------------------

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

// ---- DEP-006: portsentry-setup and unblock-ip only report success that happened --------------------

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

// ---- DEP-007: an installed TeamSpeak that does not start is a failure ------------------------------

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
