// SPDX-License-Identifier: EUPL-1.2
// local.ps1 drives the local Docker stack. It is run here for real under PowerShell, with docker and
// sh replaced by stand-ins that record their calls and answer the exit code the test asks for.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

const script = resolve("deploy/compose/local.ps1");
const windows = process.platform === "win32";
const pwsh = spawnSync("pwsh", ["-NoProfile", "-Command", "exit 0"]);
const skip = pwsh.error ? "PowerShell 7 (pwsh) is not installed" : false;

function setUp({ failOn = "" } = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-local-"));
  const bin = join(root, "bin");
  mkdirSync(bin);
  const log = join(root, "docker.log");
  // docker fails (exit 1) when its arguments contain FAIL_ON; sh (the host publish) always succeeds.
  if (windows) {
    writeFileSync(join(bin, "docker.cmd"), `@echo off\r\necho %*>>"${log}"\r\nif not "%FAIL_ON%"=="" echo %*| findstr /c:"%FAIL_ON%" >nul && exit /b 1\r\nexit /b 0\r\n`);
    writeFileSync(join(bin, "sh.cmd"), "@exit /b 0\r\n");
  } else {
    writeFileSync(join(bin, "docker"), `#!/bin/sh\necho "$*" >> "${log}"\n[ -n "$FAIL_ON" ] && case "$*" in *"$FAIL_ON"*) exit 1 ;; esac\nexit 0\n`);
    writeFileSync(join(bin, "sh"), "#!/bin/sh\nexit 0\n");
    chmodSync(join(bin, "docker"), 0o755);
    chmodSync(join(bin, "sh"), 0o755);
  }
  const run = (args, input = "") => spawnSync("pwsh", ["-NoProfile", "-File", script, ...args], {
    encoding: "utf8",
    input,
    env: { ...process.env, PATH: `${bin}${windows ? ";" : ":"}${process.env.PATH}`, FAIL_ON: failOn }
  });
  const calls = () => (existsSync(log) ? readFileSync(log, "utf8").trim().split(/\r?\n/).filter(Boolean) : []);
  return { run, calls, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

for (const [label, args, input, failOn, said] of [
  ["-Down", ["-Down"], "", "down", /Containers stopped/],
  ["-Wipe", ["-Wipe"], "wipe\n", "down -v", /Containers and volumes removed/],
  ["the start", [], "", "up -d", /Containers started/]
]) {
  test(`${label} fails when docker compose fails and does not announce success`, { skip }, () => {
    const env = setUp({ failOn });
    try {
      const result = env.run(args, input);
      assert.notEqual(result.status, 0, result.stdout);
      assert.match(result.stdout + result.stderr, /failed with exit code 1/);
      assert.doesNotMatch(result.stdout, said);
    } finally { env.cleanup(); }
  });

  test(`${label} announces success once docker compose succeeded`, { skip }, () => {
    const env = setUp();
    try {
      const result = env.run(args, input);
      assert.equal(result.status, 0, result.stdout + result.stderr);
      assert.match(result.stdout, said);
      assert.ok(env.calls().some(call => call.includes(failOn)), env.calls().join(" | "));
    } finally { env.cleanup(); }
  });
}

test("a failed rebuild does not go on to start the stack", { skip }, () => {
  const env = setUp({ failOn: "build --no-cache" });
  try {
    const result = env.run(["-Rebuild"]);
    assert.notEqual(result.status, 0);
    assert.ok(!env.calls().some(call => call.includes("up -d")), env.calls().join(" | "));
    assert.doesNotMatch(result.stdout, /Containers started/);
  } finally { env.cleanup(); }
});

test("-Wipe without the confirmation calls no docker command", { skip }, () => {
  const env = setUp();
  try {
    const result = env.run(["-Wipe"], "no\n");
    assert.notEqual(result.status, 0);
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});
