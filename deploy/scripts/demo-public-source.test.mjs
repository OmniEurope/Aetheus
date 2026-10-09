// SPDX-License-Identifier: EUPL-1.2
// demo-public-source.sh is the gate of aetheus-demo (recette R-523): nothing is built, so nothing is
// deployed, unless the public repository is the distribution of a recent main commit. It is run here
// for real, with git, node, dotnet and docker replaced by stand-ins that answer from the test and
// record every call, so the refusal, the build and what the stage publishes are observed directly.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";

const PUBLIC = "a".repeat(40);
const MATCHED = "b".repeat(40);

function write(path, content, executable = false) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
  if (executable) chmodSync(path, 0o755);
}

function setUp({ verify = "ok", shallow = "false" } = {}) {
  const root = mkdtempSync(join(tmpdir(), "aetheus-demo-source-"));
  const head = join(root, "public-head").replace(/\\/g, "/");
  const bin = join(root, "bin");
  const workspace = join(root, "ws");
  const template = join(root, "public-template");
  const log = join(root, "calls.log").replace(/\\/g, "/");
  mkdirSync(join(workspace, "deploy/scripts"), { recursive: true });
  cpSync(resolve("deploy/scripts/demo-public-source.sh"), join(workspace, "deploy/scripts/demo-public-source.sh"));

  // The internal checkout's node: answers --verify as the test decides.
  write(join(bin, "fake-node"), `#!/bin/sh
echo "node $*" >> "${log}"
if [ "$FAKE_VERIFY" = ok ]; then
  echo "Verified: public commit ${PUBLIC} is the distribution of HEAD commit ${MATCHED}."
else
  echo "Refused: public commit ${PUBLIC} is the distribution of none of the last 20 commit(s) of HEAD." >&2
  exit 1
fi
`, true);
  write(join(workspace, "deploy/scripts/ensure-node-runtime.sh"), `echo "${join(bin, "fake-node").replace(/\\/g, "/")}"\n`);

  // What the clone lays down: the public tree's own build scripts, as stand-ins that record.
  const record = name => `echo "${name} BUILD_SOURCEVERSION=\${BUILD_SOURCEVERSION:-} $*" >> "${log}"\n`;
  write(join(template, "deploy/scripts/build-fast-release-images.sh"),
    record("build-fast-release-images") + `echo "build-workspace \${WORKSPACE:-}" >> "${log}"\n`);
  write(join(template, "deploy/scripts/buildx-build-load.sh"), record("buildx-build-load"));
  write(join(template, "deploy/scripts/ensure-dotnet-sdk.sh"), `echo "${join(bin, "fake-dotnet").replace(/\\/g, "/")}"\n`);
  write(join(template, "deploy/scripts/deploy-identity.sh"), "deploy_image_repos() { BACK_IMAGE_REPO=aetheus-back; FRONT_IMAGE_REPO=aetheus-front; }\n");
  for (const file of ["Directory.Build.props", "Directory.Packages.props", "NuGet.config", "deploy/docker/Dockerfile.e2e",
    "tests/Aetheus.E2E/Aetheus.E2E.csproj", "tests/Aetheus.E2E/obj/project.assets.json",
    "tests/Aetheus.E2E/obj/Aetheus.E2E.csproj.nuget.g.props", "tests/Aetheus.E2E/obj/Aetheus.E2E.csproj.nuget.g.targets",
    "tests/Aetheus.E2E/bin/Release/net10.0/Aetheus.E2E.dll", "tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node/win32_x64/node.exe"]) {
    write(join(template, file), "x\n");
  }
  write(join(template, "tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node/linux-x64/node"), "#!/bin/sh\n", true);

  write(join(bin, "git"), `#!/bin/sh
echo "git $*" >> "${log}"
case "$*" in
  clone*) for target; do :; done; cp -r "${template.replace(/\\/g, "/")}" "$target" ;;
  *"checkout --quiet --detach"*) for sha; do :; done; echo "$sha" > "${head}" ;;
  *"rev-parse HEAD"*) if [ -f "${head}" ]; then cat "${head}"; else echo "${PUBLIC}"; fi ;;
  *is-shallow-repository*) echo "$FAKE_SHALLOW" ;;
  *"rev-list --count HEAD"*) echo "\${FAKE_HELD:-1000}" ;;
esac
exit 0
`, true);
  write(join(bin, "fake-dotnet"), `#!/bin/sh\necho "dotnet $*" >> "${log}"\n`, true);
  write(join(bin, "docker"), `#!/bin/sh
echo "docker $*" >> "${log}"
case "$1 $2" in
  "image inspect") if [ -f "${head}" ]; then cat "${head}"; else echo "${PUBLIC}"; fi ;;
  # The live demo backend: running or not, and the revision its image carries.
  "inspect --format") case "$*" in *State.Running*) echo "\${FAKE_LIVE_RUNNING:-false}" ;; *) echo "\${FAKE_LIVE_REVISION:-}" ;; esac ;;
esac
`, true);

  const run = (environment = {}) => spawnSync("sh", [join(workspace, "deploy/scripts/demo-public-source.sh")], {
    encoding: "utf8",
    env: {
      ...process.env,
      PATH: `${bin}${process.platform === "win32" ? ";" : ":"}${process.env.PATH}`,
      WORKSPACE: workspace.replace(/\\/g, "/"),
      PUBLIC_SOURCE_URL: "https://example.test/public.git",
      APP_VERSION: "1.2.3-demo",
      COMPOSE_PROJECT: "aetheus-demo",
      AETHEUS_BUILDX_BUILDER: "test-builder",
      FAKE_VERIFY: verify,
      FAKE_SHALLOW: shallow,
      ...environment
    }
  });
  const calls = () => (existsSync(log) ? readFileSync(log, "utf8").trim().split("\n").filter(Boolean) : []);
  return { root, run, calls, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

const LAST_PUBLIC = "c".repeat(40);

test("a public repository that matches no recent main commit is never built", () => {
  const env = setUp({ verify: "refused" });
  try {
    const result = env.run();
    assert.equal(result.status, 1);
    assert.match(result.stderr, /nothing is deployed/);
    assert.ok(!env.calls().some(call => /build|dotnet|docker/.test(call)));
    assert.doesNotMatch(result.stdout, /##aetheus\[setvariable/);
  } finally { env.cleanup(); }
});

// The live demo, as the fallback reads it: the upstream names the colour, that colour's backend runs.
function liveDemo(env, { colour = "green", running = "true", revision = LAST_PUBLIC } = {}) {
  const upstream = join(env.root, "000-aetheus-demo-upstream.conf");
  writeFileSync(upstream, `Define AETHEUS_DEMO_FRONT_PORT 10033\nDefine AETHEUS_DEMO_BACK_PORT 10034\n# Active colour: ${colour}\n`);
  return { UPSTREAM_CONF: upstream.replace(/\\/g, "/"), FAKE_LIVE_RUNNING: running, FAKE_LIVE_REVISION: revision };
}

test("a refused public repository rebuilds the version the live demo runs, so the demo is still reset", () => {
  const env = setUp({ verify: "refused" });
  try {
    const result = env.run(liveDemo(env));
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /reset with the version it runs/);
    const calls = env.calls();
    assert.ok(calls.some(call => call.startsWith("docker inspect") && call.includes("aetheus-demo-green-back")), calls.join("\n"));
    const fetch = calls.findIndex(call => call.includes(`fetch --quiet --depth 1 origin ${LAST_PUBLIC}`));
    const build = calls.findIndex(call => call.startsWith("build-fast-release-images"));
    assert.ok(fetch >= 0 && fetch < build, calls.join("\n"));
    assert.ok(calls.includes(`build-fast-release-images BUILD_SOURCEVERSION=${LAST_PUBLIC} `));
    assert.ok(result.stdout.includes(`##aetheus[setvariable name=PUBLIC_COMMIT]${LAST_PUBLIC}`), result.stdout);
    assert.ok(result.stdout.includes("##aetheus[setvariable name=PUBLIC_MATCHED_COMMIT]unknown"), result.stdout);
  } finally { env.cleanup(); }
});

for (const [label, live, reason] of [
  ["no live colour", { colour: "none" }, /no live demo colour is known/],
  ["a stopped live backend", { running: "false" }, /is not running/],
  ["a live backend without a revision", { revision: "<no value>" }, /carries no readable revision/]
]) {
  test(`a refused public repository with ${label} touches nothing and builds nothing`, () => {
    const env = setUp({ verify: "refused" });
    try {
      const result = env.run(liveDemo(env, live));
      assert.equal(result.status, 1);
      assert.match(result.stderr, reason);
      assert.ok(!env.calls().some(call => /build|dotnet|fetch --quiet --depth/.test(call)));
    } finally { env.cleanup(); }
  });
}

test("a verified public commit is built from the public tree and published with the main commit it matched", () => {
  const env = setUp();
  try {
    const result = env.run();
    assert.equal(result.status, 0, result.stderr);
    assert.ok(env.calls().includes(`build-fast-release-images BUILD_SOURCEVERSION=${PUBLIC} `));
    assert.ok(env.calls().some(call => call.startsWith("buildx-build-load") && call.includes(`-t aetheus-demo-browser-smoke:${PUBLIC}`)));
    assert.match(result.stdout, new RegExp(`##aetheus\\[setvariable name=PUBLIC_COMMIT\\]${PUBLIC}`));
    assert.match(result.stdout, new RegExp(`##aetheus\\[setvariable name=PUBLIC_MATCHED_COMMIT\\]${MATCHED}`));
  } finally { env.cleanup(); }
});

// Run #2551 (2026-10-04): the inherited WORKSPACE (the internal checkout) sent the published backend
// outside the public tree Docker builds from.
test("the public images are built with the public tree as their workspace", () => {
  const env = setUp();
  try {
    assert.equal(env.run().status, 0);
    const workspace = env.calls().find(call => call.startsWith("build-workspace "));
    assert.ok(workspace?.endsWith("/.public-source"), env.calls().join("\n"));
  } finally { env.cleanup(); }
});

// Run #2534 (2026-10-03): the step holds no Git credential, and a deepen asked the internal repository
// for a username. The checkout brings the history (AETHEUS_GIT_HISTORY_DEPTH); the step only counts it.
test("a shallow checkout holding enough commits is verified without fetching", () => {
  const env = setUp({ shallow: "true" });
  try {
    assert.equal(env.run({ FAKE_HELD: "20" }).status, 0);
    const calls = env.calls();
    assert.ok(!calls.some(call => call.startsWith("git fetch") && !call.startsWith("git -C")), calls.join("\n"));
    assert.ok(calls.some(call => call.startsWith("node ") && call.includes("--verify")), calls.join("\n"));
  } finally { env.cleanup(); }
});

test("a shallow checkout holding too few commits is refused before the verification", () => {
  const env = setUp({ shallow: "true" });
  try {
    const result = env.run({ FAKE_HELD: "3" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /holds 3 commit\(s\) of main and the verification needs 20: raise AETHEUS_GIT_HISTORY_DEPTH/);
    assert.ok(!env.calls().some(call => call.startsWith("node ") && call.includes("--verify")));
  } finally { env.cleanup(); }
});

test("a source that is not an https URL is refused before anything is cloned", () => {
  const env = setUp();
  try {
    const result = env.run({ PUBLIC_SOURCE_URL: "file:///tmp/public" });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /must be an https URL/);
    assert.deepEqual(env.calls(), []);
  } finally { env.cleanup(); }
});
