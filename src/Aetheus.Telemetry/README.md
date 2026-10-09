# Aetheus.Telemetry

`Aetheus.Telemetry` is an optional convenience layer over the official OpenTelemetry .NET SDK. It emits
standard OTLP/HTTP protobuf, so an application using only official OpenTelemetry packages is equivalent.

The `.nupkg` is distributed exclusively through the internal Aetheus NuGet registry. Configure the
private source and its runtime credential as documented in
[`docs/runbooks/package-registry.md`](../../docs/runbooks/package-registry.md); it is never published to
nuget.org (ADR-037).

```csharp
builder.Services.AddAetheusTelemetry(builder.Configuration);
```

Configuration lives under `Aetheus:Telemetry`. `Enabled` defaults to `false` and the
`AETHEUS_TELEMETRY_ENABLED` environment variable has final say. Required values when enabled are the
application name/id, the metrics/traces HTTPS endpoints, and the ingestion key. Logs have their own
switch, `EnableLogs` (environment variable `AETHEUS_TELEMETRY_LOGS_ENABLED`), and are disabled until
it is set; their endpoint is required only when they are enabled. Export uses bounded batches, a
three-second timeout, sampling, and never blocks a request on network I/O.

| Runtime | OpenTelemetry | Support |
| --- | --- | --- |
| .NET 10 | 1.17.x | required (OmniEurope.Performance targets .NET 10) |

## Request performance (measured by OmniEurope.Performance)

When telemetry is enabled, the package also exports how long each route takes to answer. The
measurement is the collector of the `OmniEurope.Performance` package (nuget.org, EUPL-1.2), which
listens to the ASP.NET Core `http.server.request.duration` instrument and keeps the requests of its
window in memory (2 MiB at most, grouped by route template in lower case: no concrete path, identifier
or query value). The window is 24 hours unless the host chose another, through
`OmniPerformance:Window` or by registering the collector itself before `AddAetheusTelemetry`. This
package exports five gauges on the meter `Aetheus.Telemetry.Performance`, one series per `http.route`
(without its leading slash) and `http.request.method`:

| Metric | Unit | Value |
| --- | --- | --- |
| `aetheus.http.server.request.count` | `{request}` | requests of the window |
| `aetheus.http.server.request.duration.p50` | `ms` | median (nearest rank) |
| `aetheus.http.server.request.duration.p95` | `ms` | 95th percentile (nearest rank) |
| `aetheus.http.server.request.duration.p99` | `ms` | 99th percentile (nearest rank) |
| `aetheus.http.server.request.duration.max` | `ms` | slowest request |

A sixth gauge, `aetheus.http.server.request.window` (`s`, no attribute), says how many seconds the kept
requests span: the window at most, less after a restart. A percentile is a request that happened (nearest
rank, no interpolation), so below 20 requests the 95th percentile is the maximum, and below 100 the 99th.

A route's five gauges are exported only for a collection in which its figures changed: an idle route
writes nothing, and a reader keeps each route's newest point.

Aetheus shows them in the Performance tab of the application's Supervision. The window restarts with
the process. Health probes and static assets are not measured. A host that wants the figures without
the export uses `OmniEurope.Performance` directly (`AddOmniPerformance` for its page, or
`AddOmniPerformanceCollector` and `RequestPerformanceRecorder.Summarize()` for a page of its own); with the
export enabled too, both read the same collector.

## Database size (PostgreSQL)

Off by default. A host turns it on by handing over a connection it already has:

```csharp
builder.Services.AddAetheusTelemetry(builder.Configuration, options =>
    options.DatabaseSize.UsePostgreSql(() => new NpgsqlConnection(connectionString)));
```

Every `IntervalMinutes` (15 by default, 5 to 1440) the package reads, from the PostgreSQL catalog only,
the size of the database and of its `TopTables` largest tables (20 by default, 1 to 100), and exports
three gauges on the meter `Aetheus.Telemetry.Database`:

| Metric | Unit | Attributes | Value |
| --- | --- | --- | --- |
| `aetheus.db.size` | `By` | `db.namespace` | `pg_database_size(current_database())` |
| `aetheus.db.table.size` | `By` | `db.schema.name`, `db.collection.name` | `pg_total_relation_size` (heap, indexes, TOAST) |
| `aetheus.db.table.rows` | `{row}` | `db.schema.name`, `db.collection.name` | `pg_class.reltuples`, absent before the first `ANALYZE` |

No row of a table is ever read. Each measurement is exported once, at the first collection after it was
taken. Both `IntervalMinutes` and `TopTables` live under `Aetheus:Telemetry:DatabaseSize`; the
`AETHEUS_TELEMETRY_DATABASE_SIZE_ENABLED` environment variable has the final say, and turning it on
without a connection fails at startup. Aetheus shows the figures in the Database tab of the
application's Supervision.

## Metric send interval

The metrics are sent at the interval Aetheus keeps for the application's project (its « Conservation des
données » setting: 15, 5 or 1 minute by profile). At startup, then every 15 minutes, the package reads
`GET {metrics endpoint}/../configuration` with its ingest key and applies `metricExportIntervalSeconds`
(kept between 10 s and 1 h). Until it has an answer, and when the server gives none (an older Aetheus
answers 404, a network failure), it keeps the OpenTelemetry default: 60 s, or `OTEL_METRIC_EXPORT_INTERVAL`
in milliseconds. A version of the package that predates this keeps its own interval; Aetheus accepts both.

The package composes with additional official instrumentations configured by the host. Both registration
paths use the same `OpenTelemetryBuilder`, so an application may call `AddOpenTelemetry()` before or after
the Aetheus helper without creating a proprietary provider. Calling `AddAetheusTelemetry` more than once
is idempotent.

The equivalent language-neutral resource attributes are `service.name`, `service.version`,
`deployment.environment.name`, and `aetheus.application.id`. See the Aetheus monitoring runbook for the
manual SDK configuration.

An application that does not want the Aetheus package can send the same contract with official packages:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("portfolio", serviceVersion: "1.2.3")
        .AddAttributes([
            new("deployment.environment.name", "production"),
            new("aetheus.application.id", 42)
        ]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
```

Set the standard `OTEL_EXPORTER_OTLP_*_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and
`OTEL_EXPORTER_OTLP_HEADERS=x-aetheus-ingest-key=<key>` environment variables. The same OTLP resource
contract works for Node.js, Java, Python, and other official OpenTelemetry SDKs.
