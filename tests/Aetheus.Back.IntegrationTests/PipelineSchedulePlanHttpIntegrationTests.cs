// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// <c>GET api/pipelines/schedule-plan</c> through the real HTTP pipeline, JWT included, on real PostgreSQL.
/// The window is one fixed day; each test seeds its own organizations.
/// </summary>
public sealed class PipelineSchedulePlanHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    private static string At(int hour, int minute = 0) => $"0 {minute} {hour} * * *";

    private static string Url(int organizationId, DateTime? from = null, DateTime? to = null) =>
        $"/api/pipelines/schedule-plan?organizationId={organizationId}"
        + $"&from={from ?? Day:yyyy-MM-ddTHH:mm:ssZ}&to={to ?? Day.AddDays(1):yyyy-MM-ddTHH:mm:ssZ}";

    [Fact]
    public async Task UserOfOneOrganization_SeesItsPipelines_AndNothingOfTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organizationA, projectA) = await SeedOrganizationAsync(factory, "a");
        var (organizationB, projectB) = await SeedOrganizationAsync(factory, "b");
        var ownedA = await SeedPipelineAsync(factory, projectA, "owned-a", At(1));
        var environmentOwnedA = await SeedEnvironmentPipelineAsync(factory, projectA, "environment-a", At(3));
        var ownedB = await SeedPipelineAsync(factory, projectB, "owned-b", At(1));
        var unowned = await SeedPipelineAsync(factory, null, "unowned", At(1));
        var userA = await IntegrationUsers.CreateAsync(factory.Services, "plan-a",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organizationA);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userA.Username);

        var plan = await ReadPlanAsync(client, organizationA);
        Assert.Equal(organizationA, plan.OrganizationId);
        Assert.Equal(Day, plan.FromUtc);
        Assert.Equal(DateTimeKind.Utc, plan.FromUtc.Kind);
        Assert.Equal([environmentOwnedA, ownedA], plan.Tasks.Select(task => task.PipelineId));

        var foreign = await client.GetAsync(Url(organizationB), ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain("owned-b", await foreign.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // A site Admin reads either organization, and a pipeline with no owner is in neither.
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        Assert.Equal([ownedB], (await ReadPlanAsync(admin, organizationB)).Tasks.Select(task => task.PipelineId));
        var adminA = await ReadPlanAsync(admin, organizationA);
        Assert.DoesNotContain(unowned, adminA.Tasks.Select(task => task.PipelineId));
    }

    [Fact]
    public async Task UserWithoutReadOnAPipeline_DoesNotSeeIt_NorItsOverlap()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "read");
        var readable = await SeedPipelineAsync(factory, project, "readable", At(1));
        var hidden = await SeedPipelineAsync(factory, project, "hidden", At(1));
        var named = await IntegrationUsers.CreateAsync(factory.Services, "plan-named",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, readable, Permission.Read)], organization);
        var wildcard = await IntegrationUsers.CreateAsync(factory.Services, "plan-wildcard",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organization);
        var none = await IntegrationUsers.CreateAsync(factory.Services, "plan-none", [], organization);
        using var namedClient = await IntegrationUsers.LoginClientAsync(factory, named.Username);
        using var wildcardClient = await IntegrationUsers.LoginClientAsync(factory, wildcard.Username);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, none.Username);

        var limited = Assert.Single((await ReadPlanAsync(namedClient, organization)).Tasks);
        Assert.Equal(readable, limited.PipelineId);
        Assert.Equal(0, Assert.Single(limited.Occurrences).OverlapMinutes);

        // The positive control: the same two pipelines do overlap for a caller who reads both.
        var both = await ReadPlanAsync(wildcardClient, organization);
        Assert.Equal([hidden, readable], both.Tasks.Select(task => task.PipelineId));
        Assert.All(both.Tasks, task => Assert.Equal(60, Assert.Single(task.Occurrences).OverlapMinutes));

        Assert.Empty((await ReadPlanAsync(noneClient, organization)).Tasks);
    }

    [Fact]
    public async Task Durations_AreTheAverageOfTheRuns_OrThePlannedHour_AndOverlapsAreFilled()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "duration");
        var threeRuns = await SeedPipelineAsync(factory, project, "a-three-runs", At(1));
        var neverRan = await SeedPipelineAsync(factory, project, "b-never-ran", At(1, 15));
        var edgeToEdge = await SeedPipelineAsync(factory, project, "c-edge-to-edge", At(2, 15));
        await SeedRunsAsync(factory, threeRuns, [10, 20, 60]);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var tasks = (await ReadPlanAsync(admin, organization)).Tasks;

        Assert.Equal([threeRuns, neverRan, edgeToEdge], tasks.Select(task => task.PipelineId));
        var (first, second, third) = (tasks[0], tasks[1], tasks[2]);
        Assert.Equal(30, first.DurationMinutes);
        Assert.False(first.IsPlanned);
        Assert.Equal(ScheduleDefaults.PlannedMinutes, second.DurationMinutes);
        Assert.True(second.IsPlanned);

        var firstRun = Assert.Single(first.Occurrences);
        Assert.Equal(Day.AddHours(1), firstRun.StartUtc);
        Assert.Equal(Day.AddHours(1).AddMinutes(30), firstRun.EndUtc);
        // 01:00 to 01:30 against 01:15 to 02:15: 15 minutes shared, each way.
        Assert.Equal(15, firstRun.OverlapMinutes);
        Assert.Equal(15, Assert.Single(second.Occurrences).OverlapMinutes);
        // 02:15 starts where the previous one ends: edge to edge is not an overlap.
        Assert.Equal(0, Assert.Single(third.Occurrences).OverlapMinutes);

        Assert.Equal(ScheduleFrequency.EveryEvening, first.Frequency);
        Assert.Equal(1, first.Hour);
        Assert.Equal(0, first.Minute);
    }

    [Fact]
    public async Task ManualPipelineKeepingItsSchedule_IsDisabled_AndNeverOverlaps()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "manual");
        var live = await SeedPipelineAsync(factory, project, "a-live", At(1));
        var kept = await SeedPipelineAsync(factory, project, "b-kept", At(1), PipelineTriggerType.Manual);
        // The word alone, in a step, is not a schedule.
        await SeedPipelineAsync(factory, project, "c-mentions", yaml:
            "name: c-mentions\ntrigger: manual\nstages:\n  - name: schedule\n    steps: []", trigger: PipelineTriggerType.Manual);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var tasks = (await ReadPlanAsync(admin, organization)).Tasks;

        Assert.Equal([live, kept], tasks.Select(task => task.PipelineId));
        Assert.True(tasks[0].Enabled);
        Assert.Equal(0, Assert.Single(tasks[0].Occurrences).OverlapMinutes);
        Assert.NotNull(tasks[0].NextRunUtc);
        Assert.False(tasks[1].Enabled);
        Assert.Equal(At(1), tasks[1].CronExpression);
        Assert.Equal(ScheduleFrequency.EveryEvening, tasks[1].Frequency);
        Assert.Empty(tasks[1].Occurrences);
        Assert.Null(tasks[1].NextRunUtc);
    }

    [Theory]
    [InlineData("0 3 * * *")]
    public async Task InvalidExpression_StaysListedAsCustom_WithNoOccurrence(string cron)
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "invalid");
        var invalid = await SeedPipelineAsync(factory, project, "invalid", cron);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var task = Assert.Single((await ReadPlanAsync(admin, organization)).Tasks);

        Assert.Equal(invalid, task.PipelineId);
        Assert.True(task.Enabled);
        Assert.Equal(cron, task.CronExpression);
        Assert.Equal(ScheduleFrequency.Custom, task.Frequency);
        Assert.Empty(task.Occurrences);
        Assert.False(task.OccurrencesTruncated);
        Assert.Null(task.NextRunUtc);
    }

    [Fact]
    public async Task ScheduledPipelineWithUnreadableYaml_StaysListed_WithNoOccurrence()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "unreadable");
        var unreadable = await SeedPipelineAsync(factory, project, "unreadable", yaml: "name: [unclosed");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var task = Assert.Single((await ReadPlanAsync(admin, organization)).Tasks);

        Assert.Equal(unreadable, task.PipelineId);
        Assert.Equal(ScheduleFrequency.Custom, task.Frequency);
        Assert.Empty(task.CronExpression);
        Assert.Empty(task.Occurrences);
    }

    [Fact]
    public async Task InvalidRequests_AreRefused_NeverA500()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, _) = await SeedOrganizationAsync(factory, "window");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var longest = Day.AddDays(ScheduleDefaults.MaxWindowDays);
        string[] refused =
        [
            Url(0),
            Url(organization, Day, Day),
            Url(organization, Day.AddDays(1), Day),
            Url(organization, Day, longest.AddSeconds(1)),
            Url(organization, new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            Url(organization, new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            $"/api/pipelines/schedule-plan?organizationId={organization}",
            $"/api/pipelines/schedule-plan?organizationId={organization}&from=nonsense&to=2027-01-05T00:00:00Z",
            "/api/pipelines/schedule-plan?from=2027-01-04T00:00:00Z&to=2027-01-05T00:00:00Z"
        ];

        foreach (var url in refused)
        {
            var response = await admin.GetAsync(url, ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{url} gave {response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Url(organization, Day, longest), ct)).StatusCode);
    }

    // NextRunUtc reads the injected clock, not the window: the service alone, on the same database.
    [Fact]
    public async Task NextRun_IsFromTheClock_NotFromTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "clock");
        await SeedPipelineAsync(factory, project, "clocked", At(1));
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new PipelineSchedulePlanService(
            new PipelineScheduleRepository(db), new ServerActionPlanReader(new ServerActionScheduleRepository(db), clock, Options.Create(new VulnerabilityWatchOptions())),
            scope.ServiceProvider.GetRequiredService<IPipelineGitService>(), clock);

        var plan = await service.GetPlanAsync(organization, Day, Day.AddDays(1), null, ServerActionScope.All, null, ct);

        Assert.Equal(new DateTime(2030, 6, 2, 1, 0, 0, DateTimeKind.Utc), Assert.Single(plan.Tasks).NextRunUtc);
    }

    // R-44: the latest run of each pipeline, with its duration, and the gate and grade of its latest analysis.
    [Fact]
    public async Task LastRun_IsTheLatestRun_WithItsDuration_AndTheGateAndGradeOfItsLatestAnalysis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "lastrun");
        var judged = await SeedPipelineAsync(factory, project, "a-judged", At(1));
        var running = await SeedPipelineAsync(factory, project, "b-running", At(2));
        var neverRan = await SeedPipelineAsync(factory, project, "c-never-ran", At(3));
        int newestRunId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PipelineRuns.Add(new PipelineRun { PipelineId = judged, BuildNumber = 4, Status = PipelineStatus.Failed, StartedAt = Day.AddDays(-5), CompletedAt = Day.AddDays(-5).AddMinutes(3) });
            var newest = new PipelineRun
            {
                PipelineId = judged,
                BuildNumber = 5,
                Status = PipelineStatus.Success,
                StartedAt = Day.AddDays(-1),
                CompletedAt = Day.AddDays(-1).AddMinutes(7).AddSeconds(12)
            };
            db.PipelineRuns.Add(newest);
            db.PipelineRuns.Add(new PipelineRun { PipelineId = running, BuildNumber = 1, Status = PipelineStatus.Running, StartedAt = Day.AddHours(-1) });
            await db.SaveChangesAsync(ct);
            newestRunId = newest.Id;
            var report = new AnalysisReport
            {
                OrganizationId = organization,
                ProjectId = project,
                PipelineRunId = newest.Id,
                ScannerKey = "k",
                ScannerName = "k",
                ScannerVersion = "1",
                Category = AnalysisCategory.Architecture,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.MetricsJson,
                ContentHash = Guid.NewGuid().ToString("N"),
                StartedAt = Day,
                CompletedAt = Day
            };
            db.AnalysisReports.Add(report);
            await db.SaveChangesAsync(ct);
            // Two analyses of the same run: the later one is the gate the block says.
            db.AnalysisEvaluations.Add(new AnalysisEvaluation { OrganizationId = organization, ProjectId = project, AnalysisReportId = report.Id, PipelineRunId = newest.Id, Status = AnalysisGateStatus.Passed, Grade = AnalysisGrade.A, EvaluatedAt = Day.AddHours(1) });
            db.AnalysisEvaluations.Add(new AnalysisEvaluation { OrganizationId = organization, ProjectId = project, AnalysisReportId = report.Id, PipelineRunId = newest.Id, Status = AnalysisGateStatus.Warning, Grade = AnalysisGrade.C, EvaluatedAt = Day.AddHours(2) });
            await db.SaveChangesAsync(ct);
        }

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var tasks = (await ReadPlanAsync(admin, organization)).Tasks.ToDictionary(task => task.PipelineId);

        var last = tasks[judged].LastRun!;
        Assert.Equal((newestRunId, 5, PipelineStatus.Success), (last.RunId, last.BuildNumber, last.Status));
        Assert.Equal(8, last.DurationMinutes);
        Assert.Equal((AnalysisGateStatus.Warning, AnalysisGrade.C), (last.GateStatus!.Value, last.Grade!.Value));
        var live = tasks[running].LastRun!;
        Assert.Equal(PipelineStatus.Running, live.Status);
        Assert.Null(live.DurationMinutes);
        Assert.Null(live.GateStatus);
        Assert.Null(live.Grade);
        Assert.Null(tasks[neverRan].LastRun);
    }

    // R-39: the capacity is the organization's pipeline runners (at least one); runs side by side within it are no overlap.
    [Fact]
    public async Task Overlaps_CountOnlyBeyondTheExecutionCapacity_OfTheOrganizationsPipelineRunners()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "capacity");
        var (aloneOrganization, aloneProject) = await SeedOrganizationAsync(factory, "alone");
        foreach (var (owner, runnerEnabled) in new[] { (organization, true), (organization, true), (organization, false), (aloneOrganization, false) })
        {
            await SeedRunnerAsync(factory, owner, runnerEnabled);
        }

        foreach (var (target, name) in new[] { (project, "two-a"), (project, "two-b"), (aloneProject, "one-a"), (aloneProject, "one-b") })
        {
            await SeedPipelineAsync(factory, target, name, At(1));
        }

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var roomForTwo = await ReadPlanAsync(admin, organization);
        var roomForOne = await ReadPlanAsync(admin, aloneOrganization);

        Assert.Equal(2, roomForTwo.ExecutionCapacity);
        Assert.All(roomForTwo.Tasks, task => Assert.Equal(0, Assert.Single(task.Occurrences).OverlapMinutes));
        Assert.Equal(1, roomForOne.ExecutionCapacity);
        Assert.All(roomForOne.Tasks, task => Assert.Equal(60, Assert.Single(task.Occurrences).OverlapMinutes));

        // A third run at the same time exceeds the two runners.
        await SeedPipelineAsync(factory, project, "two-c", At(1));
        var crowded = await ReadPlanAsync(admin, organization);
        Assert.Equal(3, crowded.Tasks.Count);
        Assert.All(crowded.Tasks, task => Assert.Equal(60, Assert.Single(task.Occurrences).OverlapMinutes));
    }

    // R-8: the servers' scheduled rkhunter scans, through the real RBAC and PostgreSQL: listed only for a server the
    // caller reads, movable only for one it administers, never another organization's, and the write is checked again.
    [Fact]
    public async Task ServerActions_FollowTheServerRights_AndNeverCrossTheOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organizationA, _) = await SeedOrganizationAsync(factory, "srv-a");
        var (organizationB, _) = await SeedOrganizationAsync(factory, "srv-b");
        var scanned = await SeedScheduledServerAsync(factory, organizationA, "a-scanned", "0 1 * * *", scanMinutes: [4, 8]);
        var other = await SeedScheduledServerAsync(factory, organizationA, "b-other", "30 1 * * *");
        var foreign = await SeedScheduledServerAsync(factory, organizationB, "c-foreign", "0 1 * * *");
        var pipelineRead = new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read);
        var noServer = await IntegrationUsers.CreateAsync(factory.Services, "srv-none", [pipelineRead], organizationA);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "srv-reader",
            [pipelineRead, new IntegrationUsers.Grant(ResourceType.Server, scanned, Permission.Read),
             new IntegrationUsers.Grant(ResourceType.Server, foreign, Permission.Admin)], organizationA);
        var administrator = await IntegrationUsers.CreateAsync(factory.Services, "srv-admin",
            [pipelineRead, new IntegrationUsers.Grant(ResourceType.Server, scanned, Permission.Admin)], organizationA);
        using var noServerClient = await IntegrationUsers.LoginClientAsync(factory, noServer.Username);
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        using var administratorClient = await IntegrationUsers.LoginClientAsync(factory, administrator.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Empty((await ReadPlanAsync(noServerClient, organizationA)).ServerActions);

        // Read without the Administrator right: listed, not movable, and the server's endpoint refuses the write. A
        // right named on another organization's server reaches nothing here.
        var read = Assert.Single((await ReadPlanAsync(readerClient, organizationA)).ServerActions);
        Assert.Equal(scanned, read.ServerId);
        Assert.False(read.Movable);
        Assert.False(read.IsPlanned);
        Assert.Equal(6, read.DurationMinutes);
        var refused = await readerClient.PutAsJsonAsync(
            $"/api/servers/{scanned}/rkhunter/schedule", new RkhunterScheduleRequest { CronExpression = "0 2 * * *" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var administered = Assert.Single((await ReadPlanAsync(administratorClient, organizationA)).ServerActions);
        Assert.True(administered.Movable);
        var moved = await administratorClient.PutAsJsonAsync(
            $"/api/servers/{scanned}/rkhunter/schedule", new RkhunterScheduleRequest { CronExpression = "0 2 * * *" }, ct);
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync(ct));
        Assert.Equal(2, Assert.Single((await ReadPlanAsync(administratorClient, organizationA)).ServerActions).Hour);

        // A site Admin sees every server of the organization it reads, movable, and never another organization's.
        var all = (await ReadPlanAsync(admin, organizationA)).ServerActions;
        Assert.Equal([scanned, other], all.Select(action => action.ServerId));
        Assert.All(all, action => Assert.True(action.Movable));
        Assert.DoesNotContain(foreign, all.Select(action => action.ServerId));
        Assert.Equal([foreign], (await ReadPlanAsync(admin, organizationB)).ServerActions.Select(action => action.ServerId));
    }

    private static async Task<int> SeedScheduledServerAsync(
        AetheusWebApplicationFactory factory, int organizationId, string label, string cron, int[]? scanMinutes = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = $"plan-{label}-{Guid.NewGuid():N}"[..30];
        var server = new Server { Name = name, Hostname = name, Status = ServerStatus.Online, OrganizationId = organizationId };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        db.RkhunterStates.Add(new RkhunterState { ServerId = server.Id, ScanScheduleCron = cron });
        var started = Day.AddDays(-1);
        foreach (var minutes in scanMinutes ?? [])
        {
            db.Tasks.Add(new ServerTask
            {
                ServerId = server.Id,
                Name = "RKHunter - scheduled scan",
                Operation = OperationKind.RkhunterScan,
                Status = TaskExecutionStatus.Success,
                StartedAt = started,
                CompletedAt = started.AddMinutes(minutes)
            });
            started = started.AddHours(-1);
        }

        await db.SaveChangesAsync(ct);
        return server.Id;
    }

    private static async Task SeedRunnerAsync(AetheusWebApplicationFactory factory, int organizationId, bool pipelineRunnerEnabled)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = $"plan-runner-{Guid.NewGuid():N}";
        db.Servers.Add(new Server
        {
            Name = name,
            Hostname = name,
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = pipelineRunnerEnabled,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<SchedulePlanDto> ReadPlanAsync(HttpClient client, int organizationId, DateTime? from = null, DateTime? to = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync(Url(organizationId, from, to), ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<SchedulePlanDto>(IntegrationJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The plan came back empty.");
    }

    private static async Task<(int OrganizationId, int ProjectId)> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"plan-{label}-{Guid.NewGuid():N}", Slug = $"p{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"plan-{label}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return (organization.Id, project.Id);
    }

    private static string ScheduledYaml(string name, string cron, PipelineTriggerType trigger) =>
        $"name: {name}\ntrigger: {trigger.ToString().ToLowerInvariant()}\nschedule: \"{cron}\"\nstages: []";

    private static async Task<int> SeedPipelineAsync(
        AetheusWebApplicationFactory factory, int? projectId, string name, string? cron = null,
        PipelineTriggerType trigger = PipelineTriggerType.Schedule, string? yaml = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new Pipeline
        {
            ProjectId = projectId,
            Name = name,
            TriggerType = trigger,
            YamlDefinition = yaml ?? ScheduledYaml(name, cron ?? string.Empty, trigger)
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private static async Task<int> SeedEnvironmentPipelineAsync(
        AetheusWebApplicationFactory factory, int projectId, string name, string cron)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var environment = new Aetheus.Back.Data.Entities.Environment { Name = name, ProjectId = projectId };
        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline
        {
            EnvironmentId = environment.Id,
            Name = name,
            TriggerType = PipelineTriggerType.Schedule,
            YamlDefinition = ScheduledYaml(name, cron, PipelineTriggerType.Schedule)
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private static async Task SeedRunsAsync(AetheusWebApplicationFactory factory, int pipelineId, int[] minutes)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < minutes.Length; i++)
        {
            var start = Day.AddDays(-10 + i);
            db.PipelineRuns.Add(new PipelineRun
            {
                PipelineId = pipelineId,
                Status = PipelineStatus.Success,
                StartedAt = start,
                CompletedAt = start.AddMinutes(minutes[i])
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
