<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Optional Aetheus observability

The default build has no Aetheus package reference and needs only public NuGet sources:

```bash
dotnet build
```

The integration model of a client project:

- the references are conditional and off by default (`EnableAetheusTelemetry`,
  `EnableAetheusWebAnalytics` default to `false` in the `.csproj`), so a local build restores no
  Aetheus package;
- the code that uses them sits under `#if AETHEUS_TELEMETRY` / `#if AETHEUS_WEB_ANALYTICS`, the
  constants the `.csproj` defines only when the matching property is `true`;
- only the build of the deployed image enables them, in `dotnet restore` and `dotnet publish`:

```bash
dotnet publish -p:EnableAetheusTelemetry=true
dotnet publish -p:EnableAetheusWebAnalytics=true
dotnet publish -p:EnableAetheusTelemetry=true -p:EnableAetheusWebAnalytics=true
```

- `NuGet.config` declares the Aetheus source **without credentials**
  (`https://<aetheus-api>/api/packages/nuget/v3/index.json`) and a `packageSourceMapping` that maps
  `Aetheus.*` to it and everything else to nuget.org.

No token is needed to consume `Aetheus.Telemetry`, `Aetheus.WebAnalytics` or `@aetheus/web-analytics`:
the Aetheus registry serves these packages to anonymous readers (see
`docs/runbooks/package-registry.md`), so the image builds on a deploy agent that carries no token.
Publishing them stays authenticated and admin-only.

Runtime emission remains independently disabled unless `AETHEUS_TELEMETRY_ENABLED=true` or
`AETHEUS_WEB_ANALYTICS_ENABLED=true` is provided.
