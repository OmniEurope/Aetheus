# Contributing to Aetheus

Thank you for your interest in contributing! This document explains how to get
started and what standards to follow.

## Getting Started

1. **Read [`AGENTS.md`](AGENTS.md)** -- it is the single source of truth for
   architecture rules, coding standards, and interdictions. `CLAUDE.md` is a
   pointer to it and deliberately holds no rules of its own.
2. Browse `docs/contracts/` for detailed guidance on security, deployment, and
   UI patterns.
3. Set up the dev environment (see [`docs/contracts/deployment.md`](docs/contracts/deployment.md)):
   - A stable .NET 10 SDK, version 10.0.100 or later (`global.json` is a floor with
     `rollForward: latestFeature`, ADR-048)
   - Docker (for the local PostgreSQL container)
   - Run `launch-windows.ps1 -s` (Windows) or `launch-linux.sh -s` (Linux) to start the
     backend, frontend, and required local dependencies, including PostgreSQL.

## Branch Conventions

| Branch          | Purpose                          |
|-----------------|----------------------------------|
| `main`          | Production-ready releases        |
| `develop`       | Active development               |
| `feature/*`     | Feature branches off `develop`   |
| `fix/*`         | Bug-fix branches                 |

## Before Submitting

Run through this checklist:

- [ ] `dotnet build -c Release --warnaserror` -- zero errors, zero warnings.
- [ ] `.\launch-windows.ps1 -ta` (Windows) or `./launch-linux.sh -ta` (Linux) -- unit,
      PostgreSQL integration, and Playwright E2E tests green with their required dependencies.
- [ ] `.\launch-windows.ps1 -c` (Windows) or `./launch-linux.sh -c` (Linux) -- product
      coverage report passes the 80.1% gate (see docs/contracts/testing.md).
- [ ] Architecture Guard Tests pass (migration drift, file size, controller auth).
- [ ] New `.cs` / `.razor` files have the SPDX header:
      `// SPDX-License-Identifier: EUPL-1.2`
- [ ] User-facing strings use `IStringLocalizer<AppStrings>` (EN + FR resx).
- [ ] No `DateTime.UtcNow` / `DateTime.Now` in backend production code -- inject
      `TimeProvider`.
- [ ] No inline `@code` in `.razor` files -- use code-behind `.razor.cs`.
- [ ] DTO text properties carry `[Required]` + `[StringLength]`.
- [ ] Data controllers have `[Authorize]` at class level.

## Coding Standards (Summary)

- **One class per file** (exception: related DTOs may share a file).
- **Code-behind** for Blazor: `.razor` + `.razor.cs`.
- **Least exposure**: `private` > `internal` > `protected` > `public`.
- **Async**: methods end with `Async`, propagate `CancellationToken`,
  `ConfigureAwait(false)` in services/repos.
- **EF Core**: `AsNoTracking()` on reads, `WHERE` before `Include`, no raw SQL.
- **CSS in rem**, responsive, dark-mode aware. No `.razor.css` / inline styles.

See `AGENTS.md` for the complete list.

## Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(module): short description
fix(module): what was broken and how it's fixed
test(module): what coverage was added
docs(module): what was documented
refactor(module): what changed structurally
chore(module): maintenance task
```

## Git Hooks

This project uses [Husky.NET](https://alirezanet.github.io/Husky.Net/) to run
checks before each commit. After cloning, restore the tooling and install the
hooks:

```bash
dotnet tool restore
dotnet husky install
```

The pre-commit hook (`dotnet husky run --group pre-commit`) runs three tasks:

1. **Build** -- `dotnet build -c Release --warnaserror` (zero errors, zero warnings).
2. **SPDX check** -- verifies every `.cs` / `.razor` file under `src/` and
   `tests/` contains the `SPDX-License-Identifier: EUPL-1.2` header (ignores
   `obj/`, `bin/`, and `Migrations/`).
3. **Credential scan** -- rejects commits that contain potential secrets
   (passwords, keys, tokens) in `appsettings.json` / `appsettings.Development.json`.

If any task fails, the commit is blocked. Fix the issue and retry.

The pre-push hook (`dotnet husky run --group pre-push`) additionally runs the four unit suites the
CI gate blocks on -- `test-backend`, `test-agent-core`, `test-front`, `test-analyzers` -- so a red
test surfaces before the code reaches a shared branch rather than hours later in nightly. Expect a
push to take several minutes.

The hook starts by unsetting `GIT_DIR`, `GIT_INDEX_FILE`, `GIT_WORK_TREE` and `GIT_PREFIX`. `git`
exports those to its hooks, and without the reset every `git` command the test suites spawn in their
own temporary repositories silently targets *this* repository instead. Do not remove that line.

## Security

If you discover a vulnerability, **do not open a public issue**. Follow the
process in [`SECURITY.md`](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the
[EUPL-1.2](LICENSE).
