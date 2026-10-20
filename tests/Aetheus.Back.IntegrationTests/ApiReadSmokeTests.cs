// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Authenticated read sweep: as the bootstrap admin, every top-level collection GET must
/// return a concrete <c>200 OK</c> with a non-empty body through the real pipeline. Unlike the
/// anonymous sweep (which short-circuits at the auth middleware), this drives the full
/// controller → service → repository → Postgres path for each module, so a broken EF query, a
/// missing migration column, or a DI wiring fault surfaces as a concrete failing route rather
/// than a vague 500 somewhere in prod.
/// <para>
/// The assertion is <c>== 200</c> (not merely <c>IsSuccessStatusCode</c>) plus a non-empty body
/// on purpose: a route silently regressing to <c>204 No Content</c>, or a 200 whose serialization
/// collapsed to an empty body, is a real defect this sweep must catch rather than wave through as
/// "reachable". This is a reachability + content-presence check, NOT a projection check - a body
/// of <c>[]</c>/<c>{}</c> is non-empty and passes (an empty seeded DB legitimately yields those),
/// so it does not detect an EF projection that wrongly collapsed populated data to an empty list.
/// </para>
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class ApiReadSmokeTests(ApiSmokeFixture fixture)
{
    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/servers")]
    [InlineData("/api/servers/names")]
    [InlineData("/api/servers/1/ports")]
    [InlineData("/api/users")]
    [InlineData("/api/users/me")]
    [InlineData("/api/users/roles")]
    [InlineData("/api/roles")]
    [InlineData("/api/organizations")]
    [InlineData("/api/organizations/me")]
    [InlineData("/api/agent-pools")]
    [InlineData("/api/audit")]
    [InlineData("/api/audit/actions")]
    [InlineData("/api/audit/entity-types")]
    [InlineData("/api/dashboards")]
    [InlineData("/api/alerts")]
    [InlineData("/api/monitoring/dashboard")]
    [InlineData("/api/plugins")]
    [InlineData("/api/package-feeds")]
    [InlineData("/api/package-registry")]
    [InlineData("/api/system-logs/files")]
    [InlineData("/api/releases")]
    [InlineData("/api/pipelines")]
    [InlineData("/api/pipelines/templates")]
    [InlineData("/api/pipelines/approvals/pending")]
    [InlineData("/api/pipelines/ownership/candidates")]
    [InlineData("/api/service-connections")]
    [InlineData("/api/test-suites")]
    [InlineData("/api/webhooks")]
    [InlineData("/api/vaults")]
    [InlineData("/api/vaults/names")]
    [InlineData("/api/tasks")]
    [InlineData("/api/tasks/active")]
    [InlineData("/api/variable-libraries")]
    [InlineData("/api/variable-libraries/names")]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/daily-recap")]
    [InlineData("/api/mail-templates")]
    [InlineData("/api/mail-templates/NotificationChannelTest/fr")]
    [InlineData("/api/environments")]
    [InlineData("/api/git/repos")]
    [InlineData("/api/git/connections")]
    [InlineData("/api/notifications/channels")]
    [InlineData("/api/notifications/me/unread-count")]
    [InlineData("/api/users/me/preferences")]
    [InlineData("/api/auth/registration-tokens")]
    [InlineData("/api/personal-access-tokens")]
    [InlineData("/api/backups")]
    [InlineData("/api/analysis/policies/global")]
    [InlineData("/api/ai/profiles")]
    [InlineData("/api/admin/performance")]
    // Covers both AppMonitoringController and AppTelemetryController (shared api/appmonitoring prefix).
    [InlineData("/api/appmonitoring/summary")]
    [InlineData("/metrics")]
    // Storage and reclamation sizes are read from PostgreSQL catalog measurements.
    [InlineData("/api/data-storage/projects")]
    [InlineData("/api/data-storage/reclaim")]
    // The mailbox directory is read from the stored mail inventory of every readable server.
    [InlineData("/api/mail/mailboxes")]
    public async Task CollectionGetEndpoint_AsAdmin_ReturnsNonEmpty200(string route)
    {
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync(route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET {route} returned 200 with an empty body - broken projection or no-content regression.");
    }

    // Server-management GET endpoints read cached repository snapshots rather than contacting
    // agents, making them deterministic targets for the admin read sweep.

    [Theory]
    [InlineData("certbot")]
    [InlineData("docker/containers")]
    [InlineData("apache")]
    [InlineData("mail")]
    [InlineData("portsentry")]
    [InlineData("rkhunter")]
    [InlineData("teamspeak")]
    [InlineData("modules")]
    [InlineData("apps")]
    [InlineData("module-links")]
    public async Task ServerScopedGet_AsAdmin_ReturnsNonEmpty200(string suffix)
    {
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync($"/api/servers/{fixture.ServerId}/{suffix}", cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET /api/servers/{{id}}/{suffix} returned {(int)response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET /api/servers/{{id}}/{suffix} returned 200 with an empty body.");
    }

    // --- project-scoped read paths ------------------------------------------------------------
    // These require a real project id, so they live as dedicated cases (not the parametrized
    // top-level sweep). work-items additionally requires ?projectId - the controller now refuses
    // an unscoped listing - so this also re-covers the read path that was dropped when the route
    // was removed from the sweep rather than fixed with a seeded projectId.

    [Theory]
    [InlineData("/api/work-items?projectId=")]
    [InlineData("/api/artifacts/project/")]
    [InlineData("/api/gitgraph/project/")]
    [InlineData("/api/pipelines/setup/unmet?projectId=")]
    public async Task ProjectScopedGet_AsAdmin_ReturnsNonEmpty200(string prefix)
    {
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync($"{prefix}{fixture.ProjectId}", cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {prefix}{{projectId}} returned {(int)response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET {prefix}{{projectId}} returned 200 with an empty body.");
    }

    /// <summary>The data retention setting of the seeded project, Standard by default.</summary>
    [Theory]
    [InlineData("/api/projects/0/data-retention")]
    public async Task ProjectSettingGet_AsAdmin_ReturnsNonEmpty200(string template)
    {
        using var client = fixture.CreateAdminClient();
        var route = template.Replace("/0/", $"/{fixture.ProjectId}/", StringComparison.Ordinal);

        using var response = await client.GetAsync(route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode}. Body: {Truncate(body)}");
        Assert.Contains("telemetryExportIntervalMinutes", body, StringComparison.Ordinal);
    }

    /// <summary>The version series of the seeded project: a JSON array, empty before any release.</summary>
    [Theory]
    [InlineData("/api/projects/0/release-series")]
    public async Task ProjectReleaseSeriesGet_AsAdmin_ReturnsAnArray(string template)
    {
        using var client = fixture.CreateAdminClient();
        var route = template.Replace("/0/", $"/{fixture.ProjectId}/", StringComparison.Ordinal);

        using var response = await client.GetAsync(route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode}. Body: {Truncate(body)}");
        Assert.StartsWith("[", body.TrimStart(), StringComparison.Ordinal);
    }

    /// <summary> The configuration the Aetheus.Telemetry package reads with its ingest key. The key
    /// belongs to an application of the seeded project (Standard profile: 5 minutes). </summary>
    [Theory]
    [InlineData("/api/ingest/otlp/v1/configuration")]
    public async Task IngestConfiguration_WithAnIngestKey_ReturnsTheSendInterval(string route)
    {
        var key = $"smoke-{Guid.NewGuid():N}";
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Aetheus.Back.Components.AppMonitoring.Ingest.IngestKeyHasher>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MonitoredApps.Add(new Aetheus.Back.Data.Entities.MonitoredApp
            {
                ProjectId = fixture.ProjectId,
                Name = $"smoke-{Guid.NewGuid():N}",
                IngestKeyHash = hasher.Hash(key),
                IngestKeyExpiresAt = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-aetheus-ingest-key", key);

        var configuration = await client.GetFromJsonAsync<IngestConfigurationDto>(
            route, IntegrationJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(300, configuration!.MetricExportIntervalSeconds);
    }

    /// <summary> The Maven repository reads with the credential Maven sends: a read-only personal
    /// access token as the Basic password (the registry scheme refuses the admin JWT). The metadata is
    /// generated from the database, so a seeded version is enough; no file is written. </summary>
    [Theory]
    [InlineData("/api/packages/maven/org/example/smoke/maven-metadata.xml")]
    public async Task MavenMetadata_WithAReadOnlyToken_ReturnsTheSeededVersion(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.RegistryPackages.AnyAsync(package => package.NormalizedName == "org.example:smoke", ct))
            {
                db.RegistryPackages.Add(new RegistryPackage
                {
                    Kind = PackageRegistryKind.Maven,
                    Name = "org.example:smoke",
                    NormalizedName = "org.example:smoke",
                    Versions =
                    [
                        new RegistryPackageVersion
                        {
                            Version = "1.0.0", NormalizedVersion = "1.0.0", FilePath = "maven/smoke/smoke-1.0.0.jar",
                            ContentType = "application/java-archive", Sha256 = new string('a', 64), Sha1 = new string('b', 40),
                            Integrity = "sha512-smoke", Metadata = "{}", PublishedBy = "admin",
                            Files =
                            [
                                new RegistryPackageFile
                                {
                                    FileName = "smoke-1.0.0.jar", FilePath = "maven/smoke/smoke-1.0.0.jar",
                                    ContentType = "application/java-archive", Sha256 = new string('a', 64),
                                    Sha1 = new string('b', 40), Md5 = new string('c', 32), Sha512 = new string('d', 128),
                                    PublishedBy = "admin"
                                }
                            ]
                        }
                    ]
                });
                await db.SaveChangesAsync(ct);
            }
        }

        using var admin = fixture.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/personal-access-tokens",
            new CreatePersonalAccessTokenRequest { Name = $"smoke-{Guid.NewGuid():N}", Scope = PatScope.ReadOnly, ExpirationDays = 1 }, ct);
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>(IntegrationJsonOptions.Default, ct))!.PlaintextToken;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:" + token)));

        using var response = await client.GetAsync(route, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode}. Body: {Truncate(body)}");
        Assert.Contains("<version>1.0.0</version>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckpointResumePreview_AsAdmin_ReturnsNonEmpty200()
    {
        using var client = fixture.CreateAdminClient();
        var route = $"/api/pipelines/runs/{fixture.PipelineRunId}/checkpoint-resume-preview";

        using var response = await client.GetAsync(
            route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET {route} returned 200 with an empty body.");
    }

    [Fact]
    public async Task RunLineage_AsAdmin_ReturnsNonEmpty200()
    {
        // The audit lookup and the bounded text match of the downstream runs, on PostgreSQL.
        using var client = fixture.CreateAdminClient();
        var route = $"/api/pipelines/runs/{fixture.PipelineRunId}/lineage";

        using var response = await client.GetAsync(
            route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        Assert.Contains("downstream", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStageBaselines_AsAdmin_ReturnsNonEmpty200()
    {
        using var client = fixture.CreateAdminClient();
        var route = $"/api/pipelines/{fixture.PipelineId}/runs/{fixture.PipelineRunId}/stage-baselines";

        using var response = await client.GetAsync(
            route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET {route} returned 200 with an empty body.");
    }

    /// <summary>
    /// Recette R1-43, on PostgreSQL: the directive line a blue-green migrate step writes is parsed the
    /// way task completion parses it, dispatched through the registered handler, stored by the
    /// migration's table, and read back through the run's endpoint.
    /// </summary>
    [Fact]
    public async Task MigrationGate_AsAdmin_ReturnsTheVerdictAMigrateStepReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var migrationId = $"20261014000000_Drop{Guid.NewGuid():N}";
        var line = MigrationGateReport.Directive + System.Text.Json.JsonSerializer.Serialize(
            new MigrationGateReport
            {
                Outcome = MigrationGateOutcome.Refused,
                PendingCount = 1,
                Migrations =
                [
                    new MigrationGateMigrationVerdict
                    {
                        MigrationId = migrationId,
                        Compatible = false,
                        Violations = [$"{migrationId}: line 12 is not expand-compatible: migrationBuilder.DropColumn("]
                    }
                ]
            },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var verdicts = Aetheus.Back.Components.Tasks.TaskMigrationGateParser.Parse(
                "Pending migrations are not expand-compatible.\n" + line,
                fixture.PipelineRunId, "Deploy", "Migrate", DateTime.UtcNow);
            Assert.Single(verdicts);
            await scope.ServiceProvider
                .GetRequiredService<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>()
                .DispatchStrictAsync(
                    new Aetheus.Back.Components.Tasks.Events.PipelineStepMigrationGateReportedEvent(
                        fixture.PipelineRunId, verdicts),
                    ct);
        }

        using var client = fixture.CreateAdminClient();
        var route = $"/api/pipelines/runs/{fixture.PipelineRunId}/migration-gate";
        using var response = await client.GetAsync(route, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        var gates = await response.Content.ReadFromJsonAsync<List<PipelineRunMigrationGateDto>>(
            IntegrationJsonOptions.Default, ct);
        var gate = Assert.Single(gates!, g => g.Migrations.Any(m => m.MigrationId == migrationId));
        Assert.Equal(MigrationGateOutcome.Refused, gate.Outcome);
        Assert.Equal("Migrate", gate.StepName);
        Assert.Equal(1, gate.PendingCount);
        var migration = Assert.Single(gate.Migrations);
        Assert.False(migration.Compatible);
        Assert.Contains("not expand-compatible", Assert.Single(migration.Violations), StringComparison.Ordinal);
    }

    /// <summary>Candidate-server lookup requires creating a library through its API first, so the
    /// dynamic route is covered here.</summary>
    [Fact]
    public async Task VariableLibraryPortTargets_AsAdmin_ReturnsNonEmpty200()
    {
        using var client = fixture.CreateAdminClient();

        using var created = await client.PostAsJsonAsync(
            "/api/variable-libraries",
            new CreateVariableLibraryRequest { Name = $"ports-read-{Guid.NewGuid():N}"[..40] },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var library = await created.Content.ReadFromJsonAsync<VariableLibraryDto>(
            IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        var route = $"/api/variable-libraries/{library!.Id}/ports/servers";
        using var response = await client.GetAsync(
            route, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"GET {route} returned 200 with an empty body.");
    }

    /// <summary>Recette R3-59: the read-only installed package list is asked with a POST (it queues an agent
    /// task) but changes nothing on the host, so Read is enough. On an online Linux agent publishing
    /// <c>package.inventory</c> it queues the typed PackageInventory task on PostgreSQL; on the seeded server
    /// (agent offline, capability absent) it is refused instead of queued.</summary>
    [Fact]
    public async Task InstalledPackageInventory_AsAdminAndReader_QueuesTheReadOnlyTask()
    {
        var ct = TestContext.Current.CancellationToken;
        int serverId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var org = await db.Organizations.FirstAsync(o => o.Slug == "aetheus", ct);
            var server = new Server
            {
                Name = $"pkg-{Guid.NewGuid():N}"[..30],
                Hostname = $"pkg-{Guid.NewGuid():N}.test",
                OrganizationId = org.Id,
                Status = ServerStatus.Online,
                OsType = OsType.Linux,
                LastHeartbeat = DateTime.UtcNow,
                AgentCapabilitiesJson = $"[\"{AgentCapabilities.PackageInventory}\",\"{AgentCapabilities.ShellExecution}\"]"
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(ct);
            serverId = server.Id;
        }

        using var admin = fixture.CreateAdminClient();
        using var reader = await fixture.CreateReaderClientAsync();
        var queuedIds = new List<int>();
        foreach (var client in new[] { admin, reader })
        {
            using var response = await client.PostAsync($"/api/servers/{serverId}/packages/inventory", content: null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"POST packages/inventory returned {(int)response.StatusCode}. Body: {Truncate(body)}");
            var queued = await response.Content.ReadFromJsonAsync<ServiceTaskResponse>(IntegrationJsonOptions.Default, ct);
            Assert.True(queued!.TaskId > 0);
            queuedIds.Add(queued.TaskId);
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tasks = await db.Tasks.Where(t => queuedIds.Contains(t.Id)).ToListAsync(ct);
            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, task =>
            {
                Assert.Equal(serverId, task.ServerId);
                Assert.Equal(OperationKind.PackageInventory, task.Operation);
                Assert.Equal("-", task.Command);
                Assert.Equal(InstalledPackages.TaskName, task.Name);
            });
        }

        using var refused = await admin.PostAsync($"/api/servers/{fixture.ServerId}/packages/inventory", content: null, ct);
        Assert.True(refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
            $"The seeded server (agent offline, no capability) answered {(int)refused.StatusCode}.");
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300];
}
