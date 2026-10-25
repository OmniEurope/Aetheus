// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using ProjectEnvironment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Seeding;
/// <summary>
/// Opt-in DEMONSTRATION data: realistic-but-fake servers, projects, pipeline templates, pipelines and alert rules so a
/// fresh instance (local dev, or the isolated E2E stack) shows a populated UI instead of empty lists.
/// <para>
/// Distinct from <see cref="DbInitializer"/>, which seeds only the mandatory bootstrap state (admin,
/// roles, default org, pipeline templates). This seeder is purely cosmetic test/demo content.
/// </para>
/// <para>
/// Idempotent for a named seed date. An <see cref="AppSetting"/> marker records that the dataset was
/// applied, while stable natural keys reconcile rows added to that dated dataset after its first run. The caller in
/// <c>Program.cs</c> gates it on <c>Seed:Demo=true</c> and either a non-production host or the exact
/// <c>Aetheus:EnvironmentTier=qa</c> value. QA selects a supported <c>Seed:DemoDate</c> when replaying
/// a rollback scenario.
/// </para>
/// </summary>
public static class DemoDataSeeder
{
    internal const string DefaultSeedDate = "2026-07-14";

    // Demo data hangs off the default organization seeded by DbInitializer / the ownership migration.
    private const string DefaultOrganizationSlug = "aetheus";
    public static async Task<DemoSeedResult?> SeedDemoAsync(
        AppDbContext db, TimeProvider timeProvider, string? seedDate = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var versionDate = ResolveVersionDate(seedDate);
        var markerKey = GetMarkerKey(versionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var markerExists = await db.AppSettings.AnyAsync(s => s.Key == markerKey).ConfigureAwait(false);

        var orgId = await db.Organizations
            .Where(o => o.Slug == DefaultOrganizationSlug)
            .Select(o => o.Id)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (orgId == 0)
            return null; // bootstrap seed hasn't run yet; nothing to attach the demo data to.

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync().ConfigureAwait(false)
            : null;

        // Historical timestamps are anchored to the dataset version, not to startup time. This makes a
        // partially populated database safely replayable without producing a second history on every attempt.
        var now = DateTime.SpecifyKind(versionDate.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);

        var servers = new[]
        {
            new Server
            {
                Name = "web-01", Hostname = "web-01.demo.local", IpAddress = "10.0.0.11",
                OsType = OsType.Linux, OsDescription = "Ubuntu 24.04 LTS",
                Status = ServerStatus.Offline, Type = ServerType.Normal,
                PipelineRunnerEnabled = false, DockerAvailable = true,
                AgentVersion = "1.0.0", LastHeartbeat = now, OrganizationId = orgId,
                Tags = "[\"web\",\"prod\"]"
            },
            new Server
            {
                Name = "build-01", Hostname = "build-01.demo.local", IpAddress = "10.0.0.21",
                OsType = OsType.Windows, OsDescription = "Windows Server 2022",
                Status = ServerStatus.Offline, Type = ServerType.Build,
                PipelineRunnerEnabled = false,
                AgentVersion = "1.0.0", LastHeartbeat = now, OrganizationId = orgId,
                Tags = "[\"build\"]"
            },
            new Server
            {
                Name = "db-01", Hostname = "db-01.demo.local", IpAddress = "10.0.0.31",
                OsType = OsType.Linux, OsDescription = "Debian 12",
                Status = ServerStatus.Offline, Type = ServerType.Normal,
                AgentVersion = "1.0.0", LastHeartbeat = now.AddHours(-6), OrganizationId = orgId,
                Tags = "[\"db\"]"
            }
        };
        var existingServers = await db.Servers
            .Where(s => s.OrganizationId == orgId && servers.Select(seed => seed.Name).Contains(s.Name))
            .ToDictionaryAsync(s => s.Name).ConfigureAwait(false);
        var missingServers = servers.Where(s => !existingServers.ContainsKey(s.Name)).ToArray();
        db.Servers.AddRange(missingServers);
        foreach (var server in existingServers.Values.Where(server => server.Name is "web-01" or "build-01"))
        {
            // These rows are visual demo inventory only: no agent emits heartbeats or polls their
            // task queue. Advertising synthetic presence produces a delayed false Offline event and
            // warning toast after every local/QA startup.
            server.Status = ServerStatus.Offline;
            server.PipelineRunnerEnabled = false;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        servers = servers.Select(s => existingServers.GetValueOrDefault(s.Name) ?? s).ToArray();

        // Physical server inventory makes the Services and Docker pages genuinely testable in
        // local/QA instead of rendering only empty states. Values are anchored to the dataset date
        // and guarded by stable natural keys so a partially applied seed can be replayed safely.
        var demoServices = new[]
        {
            new ServiceInfo { ServerId = servers[0].Id, Name = "nginx", Type = ServiceType.Systemd, Status = "active (running)", IsRunning = true, IsManageable = true, LastUpdated = now },
            new ServiceInfo { ServerId = servers[0].Id, Name = "docker", Type = ServiceType.Systemd, Status = "active (running)", IsRunning = true, IsManageable = true, LastUpdated = now },
            new ServiceInfo { ServerId = servers[0].Id, Name = "sshd", Type = ServiceType.Systemd, Status = "active (running)", IsRunning = true, IsManageable = false, LastUpdated = now }
        };
        var existingServiceNames = await db.ServiceInfos.Where(service => service.ServerId == servers[0].Id)
            .Select(service => service.Name).ToListAsync().ConfigureAwait(false);
        db.ServiceInfos.AddRange(demoServices.Where(service => !existingServiceNames.Contains(service.Name)));

        var demoContainers = new[]
        {
            new DockerContainer { ServerId = servers[0].Id, ContainerId = "aetheus-demo-web-01", Name = "toto-web", Image = "ghcr.io/aetheus/demo-web:2026.07", State = "running", Status = "Up 2 days", Ports = "0.0.0.0:8080->8080/tcp", CpuPercent = 1.8, MemoryUsageMb = 96, MemoryLimitMb = 512, Created = now.AddDays(-2), LastUpdated = now },
            new DockerContainer { ServerId = servers[0].Id, ContainerId = "aetheus-demo-db-01", Name = "toto-postgres", Image = "postgres:17.5", State = "running", Status = "Up 2 days (healthy)", Ports = "5432/tcp", CpuPercent = 0.7, MemoryUsageMb = 184, MemoryLimitMb = 1024, Created = now.AddDays(-2), LastUpdated = now }
        };
        var existingContainerIds = await db.DockerContainers.Where(container => container.ServerId == servers[0].Id)
            .Select(container => container.ContainerId).ToListAsync().ConfigureAwait(false);
        db.DockerContainers.AddRange(demoContainers.Where(container => !existingContainerIds.Contains(container.ContainerId)));

        var demoImages = new[]
        {
            new DockerImage { ServerId = servers[0].Id, ImageId = "sha256:aetheus-demo-web", Repository = "ghcr.io/aetheus/demo-web", Tag = "2026.07", Size = "182 MB", Created = now.AddDays(-4), LastUpdated = now },
            new DockerImage { ServerId = servers[0].Id, ImageId = "sha256:aetheus-demo-postgres", Repository = "postgres", Tag = "17.5", Size = "438 MB", Created = now.AddDays(-6), LastUpdated = now }
        };
        var existingImageIds = await db.DockerImages.Where(image => image.ServerId == servers[0].Id)
            .Select(image => image.ImageId).ToListAsync().ConfigureAwait(false);
        db.DockerImages.AddRange(demoImages.Where(image => !existingImageIds.Contains(image.ImageId)));

        var demoNetworks = new[]
        {
            new DockerNetwork { ServerId = servers[0].Id, NetworkId = "aetheus-demo-front", Name = "toto_front", Driver = "bridge", Scope = "local", LastUpdated = now },
            new DockerNetwork { ServerId = servers[0].Id, NetworkId = "aetheus-demo-data", Name = "toto_data", Driver = "bridge", Scope = "local", LastUpdated = now }
        };
        var existingNetworkIds = await db.DockerNetworks.Where(network => network.ServerId == servers[0].Id)
            .Select(network => network.NetworkId).ToListAsync().ConfigureAwait(false);
        db.DockerNetworks.AddRange(demoNetworks.Where(network => !existingNetworkIds.Contains(network.NetworkId)));

        var demoVolumes = new[]
        {
            new DockerVolume { ServerId = servers[0].Id, Name = "toto_postgres_data", Driver = "local", Mountpoint = "/var/lib/docker/volumes/toto_postgres_data/_data", LastUpdated = now },
            new DockerVolume { ServerId = servers[0].Id, Name = "toto_uploads", Driver = "local", Mountpoint = "/var/lib/docker/volumes/toto_uploads/_data", LastUpdated = now }
        };
        var existingVolumeNames = await db.DockerVolumes.Where(volume => volume.ServerId == servers[0].Id)
            .Select(volume => volume.Name).ToListAsync().ConfigureAwait(false);
        db.DockerVolumes.AddRange(demoVolumes.Where(volume => !existingVolumeNames.Contains(volume.Name)));

        var demoStacks = new[]
        {
            new DockerComposeStack { ServerId = servers[0].Id, Name = "toto-app", Status = "running", ConfigFile = "/srv/toto/compose.yml", RunningCount = 2, TotalCount = 2, LastUpdated = now },
            new DockerComposeStack { ServerId = servers[0].Id, Name = "observability", Status = "partial", ConfigFile = "/srv/monitoring/compose.yml", RunningCount = 2, TotalCount = 3, LastUpdated = now }
        };
        var existingStackNames = await db.DockerComposeStacks.Where(stack => stack.ServerId == servers[0].Id)
            .Select(stack => stack.Name).ToListAsync().ConfigureAwait(false);
        db.DockerComposeStacks.AddRange(demoStacks.Where(stack => !existingStackNames.Contains(stack.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        await DemoPipelineTemplateSeeder.SeedAsync(db, orgId).ConfigureAwait(false);

        var projects = new[]
        {
            new Project
            {
                Name = "Site Vitrine", Description = "Projet de démo : site web statique",
                Status = ProjectStatus.Active, OrganizationId = orgId, Tags = "[\"demo\"]"
            },
            new Project
            {
                Name = "API Interne", Description = "Projet de démo : API .NET interne",
                Status = ProjectStatus.Active, OrganizationId = orgId, Tags = "[\"demo\",\"api\"]"
            },
            new Project
            {
                Name = TotoConformanceSeeder.DemoProjectName,
                Description = "Projet de démonstration QA : CI, QA et déploiement d'acceptation",
                Status = ProjectStatus.Active, OrganizationId = orgId, Tags = "[\"demo\",\"qa\",\"toto\"]"
            }
        };
        var existingProjects = await db.Projects
            .Where(p => p.OrganizationId == orgId && projects.Select(seed => seed.Name).Contains(p.Name))
            .ToDictionaryAsync(p => p.Name).ConfigureAwait(false);
        var missingProjects = projects.Where(p => !existingProjects.ContainsKey(p.Name)).ToArray();
        db.Projects.AddRange(missingProjects);
        await db.SaveChangesAsync().ConfigureAwait(false);
        projects = projects.Select(p => existingProjects.GetValueOrDefault(p.Name) ?? p).ToArray();

        await DemoDashboardSeeder.SeedAsync(db, now).ConfigureAwait(false);
        var pipelines = new[]
        {
            new Pipeline
            {
                Name = "Build & Deploy Site", Description = "Pipeline de démo",
                ProjectId = projects[0].Id, CreatedByUsername = "admin",
                TriggerType = PipelineTriggerType.Manual, YamlDefinition = DemoSeedDefinitions.DemoPipelineYaml("site")
            },
            new Pipeline
            {
                Name = "API CI", Description = "Pipeline de démo",
                ProjectId = projects[1].Id, CreatedByUsername = "admin",
                TriggerType = PipelineTriggerType.Manual, YamlDefinition = DemoSeedDefinitions.DemoPipelineYaml("api")
            },
            new Pipeline
            {
                Name = "toto-orchestration-accept", Description = "Orchestration de référence QA",
                ProjectId = projects[2].Id, CreatedByUsername = "admin",
                TriggerType = PipelineTriggerType.Manual, YamlDefinition = DemoSeedDefinitions.TotoOrchestrationYaml()
            },
            new Pipeline
            {
                Name = "toto-ci", Description = "CI de référence QA",
                ProjectId = projects[2].Id, CreatedByUsername = "admin",
                TriggerType = PipelineTriggerType.Webhook, YamlDefinition = DemoSeedDefinitions.TotoPipelineYaml("ci", "webhook")
            },
            new Pipeline
            {
                Name = "toto-qa", Description = "QA planifiée de référence",
                ProjectId = projects[2].Id, CreatedByUsername = "admin",
                TriggerType = PipelineTriggerType.Schedule, YamlDefinition = DemoSeedDefinitions.TotoPipelineYaml("qa", "schedule")
            }
        };
        var pipelineNames = pipelines.Select(p => p.Name).ToArray();
        var pipelineProjectIds = pipelines.Select(p => p.ProjectId).Distinct().ToArray();
        var existingPipelines = await db.Pipelines.Where(p =>
                pipelineProjectIds.Contains(p.ProjectId) && pipelineNames.Contains(p.Name))
            .ToDictionaryAsync(p => (p.ProjectId, p.Name)).ConfigureAwait(false);
        var missingPipelines = pipelines.Where(p => !existingPipelines.ContainsKey((p.ProjectId, p.Name))).ToArray();
        db.Pipelines.AddRange(missingPipelines);
        await db.SaveChangesAsync().ConfigureAwait(false);
        pipelines = pipelines.Select(p => existingPipelines.GetValueOrDefault((p.ProjectId, p.Name)) ?? p).ToArray();
        // DM4R: 24 h of hourly metrics for the two online servers so the overview graphs / metric bars
        // and the "last activity" tiles render real history instead of an empty chart in demo. A
        // deterministic wave (no RNG) keeps the demo lively yet reproducible across reseeds.
        var metricServerIds = new[] { servers[0].Id, servers[1].Id };
        var existingMetricKeys = (await db.ServerMetrics
                .Where(metric => metricServerIds.Contains(metric.ServerId)
                    && metric.Timestamp >= now.AddHours(-24) && metric.Timestamp <= now)
                .Select(metric => new { metric.ServerId, metric.Timestamp })
                .ToListAsync().ConfigureAwait(false))
            .Select(metric => (metric.ServerId, metric.Timestamp))
            .ToHashSet();
        foreach (var srv in new[] { servers[0], servers[1] })
        {
            for (var h = 24; h >= 0; h--)
            {
                var timestamp = now.AddHours(-h);
                if (existingMetricKeys.Contains((srv.Id, timestamp))) continue;
                var phase = (24 - h) / 24.0 * Math.PI * 2;
                var cpu = 25 + 20 * Math.Sin(phase) + (srv.Type == ServerType.Build ? 15 : 0);
                db.ServerMetrics.Add(new ServerMetric
                {
                    ServerId = srv.Id,
                    CpuPercent = Math.Round(Math.Clamp(cpu, 2, 98), 1),
                    MemoryUsedMb = Math.Round(3072 + 1024 * Math.Sin(phase + 1), 0),
                    MemoryTotalMb = 8192,
                    DiskUsedGb = Math.Round(40 + (24 - h) * 0.2, 1),
                    DiskTotalGb = 100,
                    Timestamp = timestamp
                });
            }
        }

        // DM4R: a short run history per pipeline (two green, one red) so the Pipelines list "last run"
        // tile, the run timeline and the project activity feed are populated rather than empty.
        var runs = new List<PipelineRun>();
        var pipelineIds = pipelines.Select(pipeline => pipeline.Id).ToArray();
        var existingRunKeys = (await db.PipelineRuns
                .Where(run => pipelineIds.Contains(run.PipelineId)
                    && run.StartedAt >= now.AddDays(-8) && run.StartedAt <= now)
                .Select(run => new { run.PipelineId, run.StartedAt })
                .ToListAsync().ConfigureAwait(false))
            .Select(run => (run.PipelineId, run.StartedAt))
            .ToHashSet();
        foreach (var pipeline in pipelines)
        {
            AddRunIfMissing(pipeline.Id, PipelineStatus.Success, now.AddDays(-7).AddMinutes(-12), now.AddDays(-7), "a1b2c3d");
            AddRunIfMissing(pipeline.Id, PipelineStatus.Failed, now.AddDays(-3).AddMinutes(-5), now.AddDays(-3), "e4f5a6b");
            AddRunIfMissing(pipeline.Id, PipelineStatus.Success, now.AddDays(-1).AddMinutes(-9), now.AddDays(-1), "c7d8e9f");
        }
        db.PipelineRuns.AddRange(runs);
        await db.SaveChangesAsync().ConfigureAwait(false);

        void AddRunIfMissing(int pipelineId, PipelineStatus status, DateTime startedAt, DateTime completedAt, string commit)
        {
            if (existingRunKeys.Contains((pipelineId, startedAt))) return;
            runs.Add(new PipelineRun
            {
                PipelineId = pipelineId,
                Status = status,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                BranchName = "main",
                CommitHash = commit
            });
        }

        var environments = new[]
        {
            new ProjectEnvironment { Name = "Toto QA", Description = "Environnement QA de référence", Type = EnvironmentType.Testing, ProjectId = projects[2].Id, CreatedAt = now, UpdatedAt = now },
            new ProjectEnvironment { Name = "Toto Accept", Description = "Environnement d'acceptation de référence", Type = EnvironmentType.Staging, ProjectId = projects[2].Id, RequireApproval = true, ApprovalInstructions = "Vérifier les résultats QA avant approbation.", CreatedAt = now, UpdatedAt = now },
            new ProjectEnvironment { Name = "API Development", Description = "Environnement de développement API", Type = EnvironmentType.Development, ProjectId = projects[1].Id, CreatedAt = now, UpdatedAt = now }
        };
        var environmentProjectIds = environments.Select(environment => environment.ProjectId).Distinct().ToArray();
        var environmentNames = environments.Select(environment => environment.Name).ToArray();
        var existingEnvironments = await db.Environments
            .Where(environment => environment.ProjectId != null
                && environmentProjectIds.Contains(environment.ProjectId.Value)
                && environmentNames.Contains(environment.Name))
            .ToListAsync().ConfigureAwait(false);
        db.Environments.AddRange(environments.Where(candidate => !existingEnvironments.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);
        environments = environments.Select(candidate => existingEnvironments.FirstOrDefault(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name) ?? candidate).ToArray();

        var projectServers = new[]
        {
            new ProjectServer { ProjectId = projects[2].Id, ServerId = servers[1].Id, Type = ProjectServerType.AgentServer, DisplayName = "Toto runner", Host = servers[1].Hostname, CreatedAt = now, UpdatedAt = now },
            new ProjectServer { ProjectId = projects[2].Id, ServerId = servers[0].Id, Type = ProjectServerType.AgentServer, DisplayName = "Toto accept", Host = servers[0].Hostname, Port = 443, CreatedAt = now, UpdatedAt = now },
            new ProjectServer { ProjectId = projects[1].Id, ServerId = servers[0].Id, Type = ProjectServerType.AgentServer, DisplayName = "API interne", Host = servers[0].Hostname, Port = 8080, CreatedAt = now, UpdatedAt = now }
        };
        var projectIds = projects.Select(project => project.Id).ToArray();
        var existingProjectServers = await db.ProjectServers
            .Where(link => projectIds.Contains(link.ProjectId) && link.ServerId != null)
            .ToListAsync().ConfigureAwait(false);
        db.ProjectServers.AddRange(projectServers.Where(candidate => !existingProjectServers.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.ServerId == candidate.ServerId)));

        var monitoredApps = new[]
        {
            new MonitoredApp { ProjectId = projects[2].Id, EnvironmentId = environments[0].Id, ServerId = servers[1].Id, Name = "toto-qa", ProbeUrl = "https://qa.toto.demo.local/health", CurrentStatus = AppHealthStatus.Up, LastCheckedAt = now, LastResponseTimeMs = 42, CreatedAt = now, UpdatedAt = now },
            new MonitoredApp { ProjectId = projects[2].Id, EnvironmentId = environments[1].Id, ServerId = servers[0].Id, Name = "toto-accept", ProbeUrl = "https://accept.toto.demo.local/health", CurrentStatus = AppHealthStatus.Degraded, LastCheckedAt = now.AddMinutes(-2), LastResponseTimeMs = 1280, CreatedAt = now, UpdatedAt = now },
            new MonitoredApp { ProjectId = projects[1].Id, EnvironmentId = environments[2].Id, ServerId = servers[0].Id, Name = "api-interne", ProbeUrl = "https://api.demo.local/health", CurrentStatus = AppHealthStatus.Up, LastCheckedAt = now, LastResponseTimeMs = 61, CreatedAt = now, UpdatedAt = now }
        };
        var monitoredNames = monitoredApps.Select(app => app.Name).ToArray();
        var existingMonitoredApps = await db.MonitoredApps
            .Where(app => projectIds.Contains(app.ProjectId) && monitoredNames.Contains(app.Name))
            .ToListAsync().ConfigureAwait(false);
        db.MonitoredApps.AddRange(monitoredApps.Where(candidate => !existingMonitoredApps.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name)));

        var backupPolicySeeds = new[]
        {
            new BackupPolicy
            {
                Name = "Toto QA PostgreSQL", ProjectId = projects[2].Id, ServerId = servers[1].Id,
                DbEngine = BackupDbEngine.Postgres, DbHost = "localhost", DbPort = 5432,
                DbName = "toto_qa", DbUser = "toto", ScheduleCron = "0 2 * * *",
                RestoreCheckCron = "0 4 * * 0", RetentionCount = 7,
                LastRunAt = now.AddHours(-8), LastRestoreCheckAt = now.AddDays(-2), CreatedAt = now, UpdatedAt = now
            },
            new BackupPolicy
            {
                Name = "API uploads", ProjectId = projects[1].Id, ServerId = servers[0].Id,
                DbEngine = BackupDbEngine.None, FilePathsJson = "[\"/srv/api/uploads\",\"/srv/api/config\"]",
                ScheduleCron = "0 3 * * *", RestoreCheckCron = "0 5 * * 1", RetentionCount = 14,
                LastRunAt = now.AddHours(-7), LastRestoreCheckAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now
            }
        };
        var backupPolicyNames = backupPolicySeeds.Select(policy => policy.Name).ToArray();
        var existingBackupPolicies = await db.BackupPolicies
            .Where(policy => backupPolicyNames.Contains(policy.Name))
            .ToListAsync().ConfigureAwait(false);
        db.BackupPolicies.AddRange(backupPolicySeeds.Where(candidate => !existingBackupPolicies.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);
        var backupPolicies = backupPolicySeeds.Select(candidate => existingBackupPolicies.FirstOrDefault(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name) ?? candidate).ToArray();
        var backupPolicy = backupPolicies[0];

        var channels = new[]
        {
            new NotificationChannel { Name = "QA email", Type = NotificationChannelType.Email, ConfigurationJson = "{\"recipients\":[\"qa@example.invalid\"]}", CreatedAt = now, UpdatedAt = now },
            new NotificationChannel { Name = "QA Teams", Type = NotificationChannelType.Teams, ConfigurationJson = "{\"webhookUrl\":\"https://example.invalid/teams/qa\"}", CreatedAt = now, UpdatedAt = now }
        };
        var channelNames = channels.Select(channel => channel.Name).ToArray();
        var existingChannels = await db.NotificationChannels
            .Where(channel => channelNames.Contains(channel.Name)).Select(channel => channel.Name)
            .ToListAsync().ConfigureAwait(false);
        db.NotificationChannels.AddRange(channels.Where(channel => !existingChannels.Contains(channel.Name)));

        var workItems = new[]
        {
            new WorkItem { ProjectId = projects[2].Id, Type = WorkItemType.Feature, Title = "QA de régression", Status = WorkItemStatus.Active, Priority = 1, Order = 1, Tags = "qa,toto", CreatedAt = now.AddDays(-4), UpdatedAt = now },
            new WorkItem { ProjectId = projects[2].Id, Type = WorkItemType.Bug, Title = "Vérifier la garde de rollback", Status = WorkItemStatus.New, Priority = 2, Order = 2, Tags = "rollback,qa", CreatedAt = now.AddDays(-1), UpdatedAt = now }
        };
        var workItemTitles = workItems.Select(item => item.Title).ToArray();
        var existingWorkItems = await db.WorkItems
            .Where(item => item.ProjectId == projects[2].Id && workItemTitles.Contains(item.Title))
            .Select(item => item.Title).ToListAsync().ConfigureAwait(false);
        db.WorkItems.AddRange(workItems.Where(item => !existingWorkItems.Contains(item.Title)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var libraries = new[]
        {
            new VariableLibrary { Name = "Toto QA variables", Description = "Variables non secrètes pour les scénarios QA", ProjectId = projects[2].Id, CreatedAt = now, UpdatedAt = now },
            new VariableLibrary { Name = "API development variables", Description = "Configuration de développement de l'API", ProjectId = projects[1].Id, CreatedAt = now, UpdatedAt = now }
        };
        var libraryNames = libraries.Select(library => library.Name).ToArray();
        var existingLibraries = await db.VariableLibraries
            .Where(library => library.ProjectId != null && projectIds.Contains(library.ProjectId.Value)
                && libraryNames.Contains(library.Name))
            .ToListAsync().ConfigureAwait(false);
        db.VariableLibraries.AddRange(libraries.Where(candidate => !existingLibraries.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);
        libraries = libraries.Select(candidate => existingLibraries.FirstOrDefault(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name) ?? candidate).ToArray();

        var libraryEntries = new[]
        {
            new VariableLibraryEntry { VariableLibraryId = libraries[0].Id, Key = "QA_BASE_URL", Value = "https://qa.toto.demo.local" },
            new VariableLibraryEntry { VariableLibraryId = libraries[0].Id, Key = "QA_RETRY_COUNT", Value = "2" },
            new VariableLibraryEntry { VariableLibraryId = libraries[1].Id, Key = "ASPNETCORE_ENVIRONMENT", Value = "Development" },
            new VariableLibraryEntry { VariableLibraryId = libraries[1].Id, Key = "FEATURE_CACHE", Value = "true" }
        };
        var libraryIds = libraries.Select(library => library.Id).ToArray();
        var existingLibraryEntries = await db.VariableLibraryEntries
            .Where(entry => libraryIds.Contains(entry.VariableLibraryId))
            .Select(entry => new { entry.VariableLibraryId, entry.Key }).ToListAsync().ConfigureAwait(false);
        db.VariableLibraryEntries.AddRange(libraryEntries.Where(candidate => !existingLibraryEntries.Any(existing =>
            existing.VariableLibraryId == candidate.VariableLibraryId && existing.Key == candidate.Key)));

        var vaults = new[]
        {
            new Vault { Name = "Toto QA vault", Description = "Coffre de démonstration sans secret", ProjectId = projects[2].Id, CreatedAt = now, UpdatedAt = now },
            new Vault { Name = "API development vault", Description = "Coffre de démonstration sans secret", ProjectId = projects[1].Id, CreatedAt = now, UpdatedAt = now }
        };
        var vaultNames = vaults.Select(vault => vault.Name).ToArray();
        var existingVaults = await db.Vaults
            .Where(vault => vault.ProjectId != null && projectIds.Contains(vault.ProjectId.Value)
                && vaultNames.Contains(vault.Name))
            .Select(vault => new { vault.ProjectId, vault.Name }).ToListAsync().ConfigureAwait(false);
        db.Vaults.AddRange(vaults.Where(candidate => !existingVaults.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Name == candidate.Name)));

        var tasks = new[]
        {
            new ServerTask { ServerId = servers[0].Id, Name = "Demo web diagnostic", Command = "curl --fail https://example.invalid/health", Status = TaskExecutionStatus.Cancelled, CreatedAt = now.AddHours(-6), CompletedAt = now.AddHours(-6), ExitCode = null },
            new ServerTask { ServerId = servers[1].Id, Name = "Demo build diagnostic", Command = "dotnet --info", Status = TaskExecutionStatus.Cancelled, CreatedAt = now.AddHours(-5), CompletedAt = now.AddHours(-5), ExitCode = null }
        };
        var taskNames = tasks.Select(task => task.Name).ToArray();
        var existingTasks = await db.Tasks.Where(task => taskNames.Contains(task.Name)).ToListAsync().ConfigureAwait(false);
        db.Tasks.AddRange(tasks.Where(candidate => !existingTasks.Any(existing => existing.Name == candidate.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);
        tasks = tasks.Select(candidate => existingTasks.FirstOrDefault(existing => existing.Name == candidate.Name) ?? candidate).ToArray();

        var taskLogs = tasks.Select(task => new TaskLog
        {
            TaskId = task.Id,
            Level = TaskLogLevel.Warning,
            Message = "Demo seed record: command intentionally not executed.",
            Timestamp = task.CompletedAt ?? task.CreatedAt
        }).ToArray();
        var taskIds = tasks.Select(task => task.Id).ToArray();
        var loggedTaskIds = await db.TaskLogs.Where(log => taskIds.Contains(log.TaskId))
            .Select(log => log.TaskId).Distinct().ToListAsync().ConfigureAwait(false);
        db.TaskLogs.AddRange(taskLogs.Where(log => !loggedTaskIds.Contains(log.TaskId)));

        var backupRunTimes = new[] { now.AddDays(-2), now.AddDays(-1) };
        var existingBackupRunTimes = await db.BackupRuns
            .Where(run => run.BackupPolicyId == backupPolicy.Id && backupRunTimes.Contains(run.StartedAt))
            .Select(run => run.StartedAt).ToListAsync().ConfigureAwait(false);
        db.BackupRuns.AddRange(backupRunTimes.Where(startedAt => !existingBackupRunTimes.Contains(startedAt))
            .Select(startedAt => new BackupRun
            {
                BackupPolicyId = backupPolicy.Id,
                ServerId = backupPolicy.ServerId,
                Status = BackupRunStatus.Failed,
                StartedAt = startedAt,
                CompletedAt = startedAt.AddMinutes(1),
                RestoreCheckStatus = RestoreCheckStatus.Unverified,
                Message = "Demo seed record: no backup archive was created."
            }));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // DM4R: published releases per project (changelog timeline + releases list + "dernière release"
        // metrics tile). The newest of each project links to its latest green run for run-derived context.
        var seededRuns = await db.PipelineRuns.Where(run => pipelineIds.Contains(run.PipelineId)
                && run.StartedAt >= now.AddDays(-8) && run.StartedAt <= now)
            .ToListAsync().ConfigureAwait(false);
        var siteLatestRun = seededRuns.Where(r => r.PipelineId == pipelines[0].Id && r.Status == PipelineStatus.Success).OrderByDescending(r => r.StartedAt).First();
        var apiLatestRun = seededRuns.Where(r => r.PipelineId == pipelines[1].Id && r.Status == PipelineStatus.Success).OrderByDescending(r => r.StartedAt).First();
        var releases = new[]
        {
            new Release { ProjectId = projects[0].Id, Version = "1.0.0", BranchName = "main", Status = ReleaseStatus.Published, DetectedAt = now.AddDays(-20), PublishedAt = now.AddDays(-20), Changelog = "Version initiale du site vitrine.", BuildNumber = 1, CommitHash = "a1b2c3d" },
            new Release { ProjectId = projects[0].Id, Version = "1.1.0", BranchName = "main", Status = ReleaseStatus.Published, DetectedAt = now.AddDays(-1), PublishedAt = now.AddDays(-1), PipelineRunId = siteLatestRun.Id, Changelog = "Nouvelle page de contact et corrections d'affichage mobile.", BuildNumber = 2, CommitHash = "c7d8e9f" },
            new Release { ProjectId = projects[1].Id, Version = "2.3.0", BranchName = "main", Status = ReleaseStatus.Published, DetectedAt = now.AddDays(-9), PublishedAt = now.AddDays(-9), Changelog = "Endpoints de pagination et durcissement de l'authentification.", BuildNumber = 14, CommitHash = "b2c3d4e" },
            new Release { ProjectId = projects[1].Id, Version = "2.4.0", BranchName = "main", Status = ReleaseStatus.Published, DetectedAt = now.AddDays(-1), PublishedAt = now.AddDays(-1), PipelineRunId = apiLatestRun.Id, Changelog = "Cache des références API et invalidation automatique au redéploiement.", BuildNumber = 15, CommitHash = "c7d8e9f" }
        };
        var releaseVersions = releases.Select(release => release.Version).ToArray();
        var existingReleases = await db.Releases.Where(release => projectIds.Contains(release.ProjectId)
                && releaseVersions.Contains(release.Version))
            .Select(release => new { release.ProjectId, release.Version }).ToListAsync().ConfigureAwait(false);
        db.Releases.AddRange(releases.Where(candidate => !existingReleases.Any(existing =>
            existing.ProjectId == candidate.ProjectId && existing.Version == candidate.Version)));

        var alertRules = new[]
        {
            new AlertRule
            {
                Name = "CPU élevé web-01",
                ServerId = servers[0].Id,
                Metric = MetricType.Cpu,
                Operator = ComparisonOperator.GreaterThan,
                Threshold = 85,
                SustainedSeconds = 120,
                Severity = AlertSeverity.Warning,
                IsEnabled = true
            },
            new AlertRule
            {
                Name = "Disque presque plein db-01",
                ServerId = servers[2].Id,
                Metric = MetricType.Disk,
                Operator = ComparisonOperator.GreaterThanOrEqual,
                Threshold = 90,
                SustainedSeconds = 300,
                Severity = AlertSeverity.Critical,
                IsEnabled = true
            }
        };
        var alertNames = alertRules.Select(rule => rule.Name).ToArray();
        var existingAlertNames = await db.AlertRules.Where(rule => alertNames.Contains(rule.Name))
            .Select(rule => rule.Name).ToListAsync().ConfigureAwait(false);
        db.AlertRules.AddRange(alertRules.Where(rule => !existingAlertNames.Contains(rule.Name)));

        if (!markerExists)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = markerKey,
                Value = timeProvider.GetUtcNow().UtcDateTime.ToString("O")
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (transaction is not null)
            await transaction.CommitAsync().ConfigureAwait(false);
        return new DemoSeedResult(projects[2].Id, DefaultSeedDate);
    }

    internal static IReadOnlyDictionary<string, string> TotoPipelineDefinitions() =>
        DemoSeedDefinitions.TotoPipelineDefinitions();

    internal static string GetMarkerKey(string? seedDate)
    {
        var value = ResolveVersionDate(seedDate);
        return $"DemoDataSeeded:{value:yyyy-MM-dd}";
    }

    internal static DateOnly ResolveVersionDate(string? seedDate)
    {
        var value = string.IsNullOrWhiteSpace(seedDate) ? DefaultSeedDate : seedDate;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            throw new ArgumentException("Seed date must use yyyy-MM-dd.", nameof(seedDate));
        if (value != DefaultSeedDate)
            throw new ArgumentException($"Unsupported demo seed date '{value}'. Supported dates: {DefaultSeedDate}.", nameof(seedDate));
        return parsed;
    }
}
