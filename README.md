```
 █████╗ ███████╗████████╗██╗  ██╗███████╗██╗   ██╗███████╗
██╔══██╗██╔════╝╚══██╔══╝██║  ██║██╔════╝██║   ██║██╔════╝
███████║█████╗     ██║   ███████║█████╗  ██║   ██║███████╗
██╔══██║██╔══╝     ██║   ██╔══██║██╔══╝  ██║   ██║╚════██║
██║  ██║███████╗   ██║   ██║  ██║███████╗╚██████╔╝███████║
╚═╝  ╚═╝╚══════╝   ╚═╝   ╚═╝  ╚═╝╚══════╝ ╚═════╝ ╚══════╝
       Infrastructure Manager  ·  CI/CD  ·  Observability
```

**Aetheus** is a self-hosted infrastructure and CI/CD platform: a central server, distributed
agents on your own machines, and a web dashboard. You define pipelines in YAML, run them on
your fleet, deploy the result, and watch what you deployed, all from one place.

Licensed under the [European Union Public Licence v1.2](LICENSE).

- Website: <https://aetheus.sonytumen.com/>
- Documentation: <https://docs.aetheus.sonytumen.com/>

## Features

**CI/CD pipelines**
- YAML pipelines with stages, jobs, steps and triggers (manual, webhook, scheduled).
- Agent pools, per-stage target selection, and cross-agent job dispatch.
- Artifacts with configurable retention, run logs, and a visual pipeline editor.
- Native blue-green deployment steps, binary symlink-flip or container compose, with smoke gates and rollback.

**Fleet and agents**
- .NET worker-service agents for Linux (systemd) and Windows (Windows Service).
- Pull model: HTTP polling for work, SignalR over WebSocket for real time.
- Non-root by default with opt-in capabilities chosen at install time.
- Metric collectors for the host and its services.

**Deployed-application monitoring**
- Availability probing from an agent or from the backend, for on-fleet and off-fleet apps.
- Optional OTLP ingestion of metrics, logs and errors, with a per-application ingestion key.

**Projects, secrets and access**
- Organizations, projects, environments, and per-resource authorization with administrable roles.
- Variable libraries scoped to a project, environment or project-server.
- Vaults holding AES-256-GCM encrypted secrets, masked in every output.
- JWT authentication with refresh-token rotation, TOTP two-factor, and account lockout.
- SHA-256 hash-chained audit log, append-only at the database level, with a tamper-detection endpoint.

**Quality tooling**
- Built-in code analysis campaigns: coverage, duplication, complexity and security rules.
- Custom Roslyn analyzers shipped with the solution.
- Optional packages for OpenTelemetry (`Aetheus.Telemetry`) and privacy-preserving web analytics
  (`Aetheus.WebAnalytics`, plus a browser package under `packages/`).

## Quick start

### Prerequisites

- .NET 10 SDK, version 10.0.202 or later. `global.json` prefers the 10.0.2xx feature band.
- Docker and Docker Compose, for the local database and for container deployment.
- Playwright browsers, only for E2E tests:
  `pwsh bin/Debug/net10.0/playwright.ps1 install` from `tests/Aetheus.E2E/`.

### Configure

The backend reads its local settings from a gitignored `appsettings.Development.json`. Copy the
template and replace every `CHANGE_ME` value before the first run:

```bash
cp src/Aetheus.Back/appsettings.Development.json.example src/Aetheus.Back/appsettings.Development.json
```

`Auth:JwtKey` must be at least 32 UTF-8 bytes. You may leave it empty in development, in which case
the backend generates an ephemeral signing key at startup and every token is invalidated on restart.
The connection string in the template already matches the local database container, which the
launchers start for you.

Do the same for `src/Aetheus.Agent.Windows/appsettings.Development.json.example` when you run the
Windows agent from source.

### Launch

The repository ships one launcher per platform. They build the solution, start a local
PostgreSQL container, run the backend and the frontend, and keep test databases isolated.

**Windows**

```powershell
.\launch-windows.ps1          # build, start API (5301) + front (5401), open the browser
.\launch-windows.ps1 -s       # same, without opening a browser
.\launch-windows.ps1 -r       # reset the database with a fresh seed, then start
.\launch-windows.ps1 -hr      # start with hot reload (dotnet watch)
.\launch-windows.ps1 -hl      # detailed help with examples
```

**Linux**

```bash
./launch-linux.sh             # build and start the stack
./launch-linux.sh -s          # start without opening a browser
./launch-linux.sh -hl         # detailed help with examples
```

### Tests

Both launchers expose the same test flags:

```bash
./launch-linux.sh -t          # unit tests, including the analyzer suite
./launch-linux.sh -ti         # integration tests on Testcontainers PostgreSQL
./launch-linux.sh -te         # Playwright E2E tests on an isolated ephemeral database
./launch-linux.sh -tec <name> # E2E tests filtered by category
./launch-linux.sh -ta         # everything: unit, integration and E2E
./launch-linux.sh -c          # tests with the coverage report and its gate
```

### Without the launchers

```bash
dotnet build Aetheus.slnx
dotnet run --project src/Aetheus.Back    # https://localhost:5301
dotnet run --project src/Aetheus.Front   # https://localhost:5401
```

| Service | URL |
|---------|-----|
| Backend API | `https://localhost:5301`, OpenAPI at `/openapi/v1.json` |
| Frontend (Blazor WebAssembly) | `https://localhost:5401` |
| SignalR hubs | `https://localhost:5301/hubs/*` |
| Health | `/health` authenticated, `/health/live` and `/health/ready` anonymous |

## Installing an agent

**Linux**

The installer needs the agent binaries next to it, so it runs from the extracted agent archive,
not from a checkout. Download the archive from the Aetheus UI, or from
`{SERVER_URL}/downloads/aetheus-agent-linux-x64.tar.gz`, extract it, then run from inside it:

```bash
mkdir aetheus-agent && tar -xzf aetheus-agent-linux-x64*.tar.gz -C aetheus-agent
cd aetheus-agent
sudo sh ./install-agent-linux.sh
# prompts for the server URL and a registration token, then creates a systemd service
```

**Windows**

Download the agent archive from the Aetheus UI, or from
`{SERVER_URL}/downloads/aetheus-agent-win-x64.zip`, extract it, then run from inside it:

```powershell
powershell -ExecutionPolicy Bypass -File .\install-agent-windows.ps1
```

## Deployment

```powershell
.\deploy\compose\local.ps1            # local Docker stack: build and start
.\deploy\compose\local.ps1 -Down      # stop it
```

`deploy.sh` assumes no host: `SERVERNAME` (the public domain) and `GITREMOTE` (the repository to
deploy) are required, and the script stops when either is missing.

```bash
export SERVERNAME=aetheus.example.com
export GITREMOTE=https://git.example.com/you/aetheus.git
sh deploy/remote-deploy/deploy.sh           # remote Linux deployment, production, branch main
sh deploy/remote-deploy/deploy.sh -b develop
sh deploy/remote-deploy/deploy.sh -r        # rebuild without cache
sh deploy/remote-deploy/deploy.sh -d        # stop
```

`deploy/apache/` holds reverse-proxy templates using `example.com` placeholders. Replace the
`ServerName` values and the certificate paths with your own before use.

## Repository layout

```
src/
  Aetheus.Back/            ASP.NET Core API (.NET 10)
  Aetheus.Front/           Blazor WebAssembly dashboard
  Aetheus.Shared/          DTOs, enums, API contracts
  Aetheus.Agent.Core/      Platform-agnostic agent library
  Aetheus.Agent.Linux/     Linux worker service
  Aetheus.Agent.Windows/   Windows worker service
  Aetheus.Cli/             Command line client
  Aetheus.Analyzers/       Custom Roslyn analyzers
  Aetheus.Telemetry/       Optional OpenTelemetry package
  Aetheus.WebAnalytics/    Optional web analytics package
tests/                     Unit, integration, architecture and Playwright E2E suites
deploy/                    Dockerfiles, compose files, Apache templates, scripts, pipeline templates
packages/                  Published package sources
examples/                  Sample consumers
templates/                 Vertical-slice scaffolding
```

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) for the coding standards, the test expectations and the
commit conventions. Security issues follow [SECURITY.md](SECURITY.md); please do not open a
public issue for a vulnerability.

## License

Aetheus is distributed under the [EUPL-1.2](LICENSE). Third-party notices are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
