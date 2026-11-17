# rehearsal-blazor

Minimal Blazor application the pipeline rehearsal (`launch-windows.ps1 -pr`, `scripts/pipeline-rehearsal.ps1`) pushes into
a fresh Aetheus project. The rehearsal copies this folder into a new Git repository, sets the project's pipelines up
through the real setup wizard and runs them on a simulated VPS. It is never built inside the Aetheus solution.

- `GET /health` answers `Healthy` once the application runs.
- `GET /version` answers the release version the deployment gave it (`APP_VERSION`).
- `src/RehearsalBlazor/VersionInfo.cs` is the file the rehearsal moves between its two deployments (the Wordmorrow
  incident: a file moved between two releases left a stale copy in the build directory and the rebuild failed).
- `tests/RehearsalBlazor.Tests` is a NUnit unit test project (the default QA of a .NET repository runs it on the sealed source).
- `tests/RehearsalBlazor.E2E` is a Playwright for .NET (NUnit) project with two browser tests: the QA pipeline runs it in the
  pinned Playwright image (`APPLICATION_QA_E2E_IMAGE`) against the disposable stack's front (`QA_FRONT_URL`), as the setup
  wizard declares it.
