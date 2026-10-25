# Public Distribution Manifest

This repository is the public distribution of Aetheus. It is licensed under the
[European Union Public Licence v1.2](LICENSE).

The manifest documents the boundary applied when synchronising from the private development
repository. It is an allowlist: new top-level content must be reviewed before it can be copied here.
Boundary last reviewed on 2026-08-28.

## Kept

| Path | Purpose |
|------|---------|
| `src/` | Product source code |
| `tests/` | Unit, architecture, integration and E2E suites that apply to this distribution |
| `deploy/docker/`, `deploy/compose/`, `deploy/apache/` | Container and reverse-proxy assets. Apache files are templates using `example.com`. |
| `deploy/agent-installer/`, `deploy/remote-deploy/` | Agent installers and the remote deployment script |
| `deploy/pipeline-templates/` | Reusable pipeline templates, referenced by `Aetheus.Back.csproj` and `Dockerfile.back` |
| `deploy/pipelines/` | Cross-agent conformance pipeline catalogue used as test fixtures |
| `deploy/tools/Aetheus.PackageNormalizer/` | Build tool that is part of the solution |
| `.aetheus/` | Architecture, quality and security rule catalogues read by the product and its Dockerfiles |
| `packages/`, `examples/`, `templates/` | Published package sources, sample consumers, scaffolding |
| `licenses/` | License texts for vendored third-party content |
| `.github/workflows/build-test.yml` | Public CI: build, tests, SPDX and vulnerability checks |
| `.husky/` | Contributor pre-commit and pre-push checks |
| `scripts/` | Repository maintenance and local launch helpers |
| `launch-windows.ps1`, `launch-linux.sh` | Local development launchers |
| `README.md`, `CONTRIBUTING.md`, `SECURITY.md` | Public project documentation |
| `LICENSE`, `THIRD_PARTY_NOTICES.md` | EUPL-1.2 and dependency notices |
| Root build and configuration files | Solution, SDK, NuGet, coverage, lint, editor, Git and Docker configuration |

## Removed

| Private path | Reason |
|--------------|--------|
| `.claude/`, `.codex/`, `CLAUDE.md`, `AGENTS.md` | Agent instructions, handoffs, audit history and private working context |
| `.pipeline/` | Self-deployment pipeline definitions and the production Apache vhosts of the private control plane |
| `docs/` | Internal architecture notes, audits, plans and operational runbooks |
| `docs-site/` | Documentation site, deployed separately |
| `site/` | Separately deployed showcase site |
| `CHANGELOG.md`, `MISTAKES.md` | Private development history and operational detail |
| `scratch/`, `.local/`, `.worktrees/`, `.analysis-duplication-current/` | Local scratch space and analysis output |
| `z*.prompt.md` | Private prompts, backlog and work notes |
| `.github/workflows/release.yml` | Private release process coupled to the private changelog |
| `src/**/appsettings.Development.json` | Local development settings carrying development credentials. Published as `.example` templates instead. |

## Adapted

- The Apache vhost templates and the default Git remote of `deploy/remote-deploy/deploy.sh` use
  `example.com` and this repository instead of the private infrastructure host names.
- The remote-agent example in `launch-linux.sh` uses `root@example.com:2222` instead of the private
  server address. Re-apply this on every synchronisation: the private launcher still carries the real
  one, and rule 4 covers IP addresses as well as host names.
- The launchers are named `launch-windows.ps1` and `launch-linux.sh` here, with
  `scripts/launch-core.ps1` as their shared core.
- Public CI names each test suite explicitly instead of filtering by category. A `--filter`
  argument would override the platform exclusion that `tests/Directory.Build.props` applies by
  default, and the E2E project needs a running application, so it is not part of CI.
- `README.md` and `CONTRIBUTING.md` link to the public site and documentation rather than to
  internal documents.
- Local development settings ship as `appsettings.Development.json.example` templates whose secrets
  are `CHANGE_ME` placeholders. The database connection string is kept literal because
  `deploy/compose/dev-db.compose.yml` already defines those same local credentials and binds the
  container to `127.0.0.1` only.

## Rules

1. **No test may depend on a path listed under Removed.** A test that reads `.pipeline/`, `docs/`,
   `site/`, `docs-site/` or `.github/workflows/release.yml` cannot pass here, so it is not part of
   this distribution. When synchronising, such tests are removed rather than weakened, skipped or
   made conditional. Never keep a test green by relaxing its assertions.
2. Every top-level path must be listed under Kept or reviewed before it is copied.
3. Never add credentials, tokens, private keys, `.env*` files, local overrides or database dumps.
4. Never add real production domains, IP addresses, server names or filesystem paths.
5. Never add private incident reports, audit outputs, handoffs or infrastructure runbooks.
6. Never add generated build output: `bin/`, `obj/`, test results, coverage or artifacts.

## Publication checks

Before publishing a revision:

1. Verify that every top-level path is listed under Kept or has been explicitly reviewed.
2. Search for private domains, personal addresses, server IPs and key or token signatures.
3. Run `dotnet build Aetheus.slnx -c Release --warnaserror`.
4. Run each unit suite named in `.github/workflows/build-test.yml`, with no `--filter` argument.
5. Review `git status` and the staged diff before pushing.
