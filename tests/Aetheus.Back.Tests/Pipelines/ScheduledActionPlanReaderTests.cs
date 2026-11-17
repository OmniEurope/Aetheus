// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.AppBackups;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-8, the AI tasks and the backups of the planner: the real repository and reader over an in-memory database
/// (the organization, rights and averaging rules are what is exercised, not the SQL the provider writes; the real
/// PostgreSQL proof is the HTTP integration test). The scope stands for what the controller reads from the RBAC service.
/// </summary>
public sealed class ScheduledActionPlanReaderTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private const int OrganizationA = 1;
    private const int OrganizationB = 2;

    private readonly AppDbContext _db;
    private readonly ServerActionPlanReader _reader;

    public ScheduledActionPlanReaderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        _reader = new ServerActionPlanReader(new ServerActionScheduleRepository(_db), new FakeTimeProvider(new DateTimeOffset(Now)), Options.Create(new VulnerabilityWatchOptions()));
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedProjectAsync(string name, int organizationId)
    {
        var project = new Project { Name = name, OrganizationId = organizationId };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return project.Id;
    }

    private async Task<int> SeedServerAsync(string name, int organizationId)
    {
        var server = new Server { Name = name, Hostname = name, OrganizationId = organizationId, Status = ServerStatus.Online };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return server.Id;
    }

    private async Task<int> SeedAiTaskAsync(
        string name, string? schedule, int? projectId = null, int? serverId = null, bool enabled = true)
    {
        var task = new AiTaskDefinition { Name = name, ProfileId = 1, Schedule = schedule, ProjectId = projectId, ServerId = serverId, Enabled = enabled };
        _db.AiTaskDefinitions.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return task.Id;
    }

    private async Task SeedAiRunAsync(int taskId, int minutes, bool succeeded = true, int daysAgo = 1)
    {
        _db.AiRunResults.Add(new AiRunResult
        {
            AiTaskDefinitionId = taskId,
            Succeeded = succeeded,
            DurationMs = minutes * 60_000L,
            CreatedAt = Now.AddDays(-daysAgo)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> SeedBackupAsync(string name, string cron, int projectId, int serverId, bool enabled = true)
    {
        var policy = new BackupPolicy { Name = name, ScheduleCron = cron, ProjectId = projectId, ServerId = serverId, Enabled = enabled };
        _db.BackupPolicies.Add(policy);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return policy.Id;
    }

    private async Task SeedBackupRunAsync(int policyId, int serverId, int minutes, BackupRunStatus status = BackupRunStatus.Succeeded, int daysAgo = 1)
    {
        var started = Now.AddDays(-daysAgo);
        _db.BackupRuns.Add(new BackupRun
        {
            BackupPolicyId = policyId,
            ServerId = serverId,
            Status = status,
            StartedAt = started,
            CompletedAt = started.AddMinutes(minutes)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<List<ScheduledServerActionDto>> ReadAsync(ServerActionScope scope, int organizationId = OrganizationA) =>
        _reader.ReadAsync(organizationId, scope, Day, Day.AddDays(1), TestContext.Current.CancellationToken);

    private static ServerActionScope Scope(
        int[]? readableProjects = null, int[]? writableProjects = null, int[]? readableServers = null, int[]? writableServers = null) =>
        new(readableServers ?? [], [])
        {
            ReadableProjectIds = readableProjects ?? [],
            WritableProjectIds = writableProjects ?? [],
            WritableServerIds = writableServers ?? []
        };

    // --- AI tasks ---

    [Fact]
    public async Task AnAiTask_IsListedWithItsSixFieldSchedule_ItsOccurrencesAndAPlannedHour()
    {
        var project = await SeedProjectAsync("aetheus", OrganizationA);
        var task = await SeedAiTaskAsync("nightly review", "0 30 3 * * *", projectId: project);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal($"ai:{task}", action.Id);
        Assert.Equal(ServerActionKind.AiTask, action.Kind);
        Assert.Equal(task, action.SourceId);
        Assert.Equal("nightly review", action.SourceName);
        Assert.Equal(project, action.ProjectId);
        Assert.Equal("aetheus", action.ProjectName);
        Assert.Equal(0, action.ServerId);
        Assert.Equal("0 30 3 * * *", action.CronExpression);
        Assert.Equal(ScheduleFrequency.EveryEvening, action.Frequency);
        Assert.Equal(3, action.Hour);
        Assert.Equal(30, action.Minute);
        var run = Assert.Single(action.Occurrences);
        Assert.Equal(Day.AddHours(3).AddMinutes(30), run.StartUtc);
        // Never ran: one hour, as for a pipeline, and said as planned.
        Assert.True(action.IsPlanned);
        Assert.Equal(ScheduleDefaults.PlannedMinutes, action.DurationMinutes);
        Assert.Equal(run.StartUtc.AddHours(1), run.EndUtc);
        Assert.Equal(Day.AddHours(3).AddMinutes(30), action.NextRunUtc);
    }

    [Fact]
    public async Task AnAiTaskOfAServer_NamesItsServer()
    {
        var server = await SeedServerAsync("srv-ai", OrganizationA);
        await SeedAiTaskAsync("log triage", "0 0 4 * * *", serverId: server);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(server, action.ServerId);
        Assert.Equal("srv-ai", action.ServerName);
        Assert.Null(action.ProjectId);
    }

    [Fact]
    public async Task TheDurationOfAnAiTask_IsTheAverageOfItsLastSuccessfulRuns_NotOfTheFailedOnes()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var task = await SeedAiTaskAsync("t", "0 0 2 * * *", projectId: project);
        await SeedAiRunAsync(task, 10);
        await SeedAiRunAsync(task, 20, daysAgo: 2);
        await SeedAiRunAsync(task, 300, succeeded: false, daysAgo: 3);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.False(action.IsPlanned);
        Assert.Equal(15, action.DurationMinutes);
    }

    [Fact]
    public async Task OnlyTheAiTasksOfTheOrganization_AreListed_NeverAnotherOrganizations()
    {
        var mineProject = await SeedProjectAsync("mine", OrganizationA);
        var foreignProject = await SeedProjectAsync("foreign", OrganizationB);
        var foreignServer = await SeedServerAsync("srv-foreign", OrganizationB);
        var mine = await SeedAiTaskAsync("mine", "0 0 2 * * *", projectId: mineProject);
        var foreign = await SeedAiTaskAsync("foreign-project-task", "0 0 2 * * *", projectId: foreignProject);
        var foreignOnServer = await SeedAiTaskAsync("foreign-server-task", "0 0 2 * * *", serverId: foreignServer);

        var listed = await ReadAsync(ServerActionScope.All);

        Assert.Equal([mine], listed.Select(action => action.SourceId));
        Assert.Equal([foreign, foreignOnServer], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.SourceId).Order());
    }

    [Fact]
    public async Task ReadableIdsOfAnotherOrganization_ListNoAiTaskForThisOrganization()
    {
        var foreignProject = await SeedProjectAsync("foreign", OrganizationB);
        var foreignServer = await SeedServerAsync("srv-foreign", OrganizationB);
        await SeedAiTaskAsync("foreign-project-task", "0 0 2 * * *", projectId: foreignProject);
        await SeedAiTaskAsync("foreign-server-task", "0 0 2 * * *", serverId: foreignServer);

        var scope = Scope(readableProjects: [foreignProject], writableProjects: [foreignProject], readableServers: [foreignServer], writableServers: [foreignServer]);

        Assert.Empty(await ReadAsync(scope));
    }

    [Fact]
    public async Task AnAiTaskTheCallerCannotRead_IsNotListed_WithoutBeingNamed()
    {
        var readable = await SeedProjectAsync("readable", OrganizationA);
        var hidden = await SeedProjectAsync("hidden", OrganizationA);
        var hiddenServer = await SeedServerAsync("srv-hidden", OrganizationA);
        await SeedAiTaskAsync("visible", "0 0 2 * * *", projectId: readable);
        await SeedAiTaskAsync("secret-project", "0 0 2 * * *", projectId: hidden);
        await SeedAiTaskAsync("secret-server", "0 0 2 * * *", serverId: hiddenServer);

        var actions = await ReadAsync(Scope(readableProjects: [readable]));

        Assert.Equal(["visible"], actions.Select(action => action.SourceName));
    }

    [Fact]
    public async Task AReadableServer_ShowsItsAiTasksButNotTheProjectsOnes()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedAiTaskAsync("of-project", "0 0 2 * * *", projectId: project);
        await SeedAiTaskAsync("of-server", "0 0 3 * * *", serverId: server);

        var actions = await ReadAsync(Scope(readableServers: [server]));

        Assert.Equal(["of-server"], actions.Select(action => action.SourceName));
    }

    [Fact]
    public async Task AnAiTask_IsMovableOnlyWithTheWriteRightOnItsOwner()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedAiTaskAsync("a-project-writer", "0 0 2 * * *", projectId: project);
        await SeedAiTaskAsync("b-server-writer", "0 0 3 * * *", serverId: server);

        var readOnly = await ReadAsync(Scope(readableProjects: [project], readableServers: [server]));
        var projectWriter = await ReadAsync(Scope(readableProjects: [project], writableProjects: [project], readableServers: [server]));
        var serverWriter = await ReadAsync(Scope(readableProjects: [project], readableServers: [server], writableServers: [server]));

        Assert.All(readOnly, action => Assert.False(action.Movable));
        Assert.Equal([true, false], projectWriter.Select(action => action.Movable));
        Assert.Equal([false, true], serverWriter.Select(action => action.Movable));
        // A write right on the server never moves a task owned by a project, nor the reverse.
        Assert.All(await ReadAsync(ServerActionScope.All), action => Assert.True(action.Movable));
    }

    [Fact]
    public async Task ADisabledAiTask_OrOneWithoutASchedule_IsNotListed()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        await SeedAiTaskAsync("disabled", "0 0 2 * * *", projectId: project, enabled: false);
        await SeedAiTaskAsync("none", null, projectId: project);
        await SeedAiTaskAsync("blank", string.Empty, projectId: project);

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    // --- Backups ---

    [Fact]
    public async Task ABackup_IsListedWithItsFiveFieldCron_ItsProjectAndItsTargetServer()
    {
        var project = await SeedProjectAsync("shop", OrganizationA);
        var server = await SeedServerAsync("srv-db", OrganizationA);
        var policy = await SeedBackupAsync("orders-db", "15 1 * * *", project, server);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal($"backup:{policy}", action.Id);
        Assert.Equal(ServerActionKind.AppBackup, action.Kind);
        Assert.Equal("orders-db", action.SourceName);
        Assert.Equal(project, action.ProjectId);
        Assert.Equal("shop", action.ProjectName);
        Assert.Equal(server, action.ServerId);
        Assert.Equal("srv-db", action.ServerName);
        Assert.Equal("15 1 * * *", action.CronExpression);
        Assert.Equal(ScheduleFrequency.EveryEvening, action.Frequency);
        Assert.Equal(1, action.Hour);
        Assert.Equal(15, action.Minute);
        var run = Assert.Single(action.Occurrences);
        Assert.Equal(Day.AddHours(1).AddMinutes(15), run.StartUtc);
        Assert.True(action.IsPlanned);
        Assert.Equal(ScheduleDefaults.PlannedMinutes, action.DurationMinutes);
    }

    [Fact]
    public async Task TheDurationOfABackup_IsTheAverageOfItsLastSuccessfulRuns_NotOfTheFailedOnes()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        var policy = await SeedBackupAsync("b", "0 2 * * *", project, server);
        await SeedBackupRunAsync(policy, server, 6);
        await SeedBackupRunAsync(policy, server, 10, daysAgo: 2);
        await SeedBackupRunAsync(policy, server, 500, BackupRunStatus.Failed, daysAgo: 3);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.False(action.IsPlanned);
        Assert.Equal(8, action.DurationMinutes);
    }

    [Fact]
    public async Task OnlyTheBackupsOfTheOrganization_AreListed_NeverAnotherOrganizations()
    {
        var mineProject = await SeedProjectAsync("mine", OrganizationA);
        var foreignProject = await SeedProjectAsync("foreign", OrganizationB);
        var server = await SeedServerAsync("srv", OrganizationA);
        var mine = await SeedBackupAsync("mine", "0 2 * * *", mineProject, server);
        var foreign = await SeedBackupAsync("foreign", "0 2 * * *", foreignProject, server);

        Assert.Equal([mine], (await ReadAsync(ServerActionScope.All)).Select(action => action.SourceId));
        Assert.Equal([foreign], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.SourceId));
        Assert.Empty(await ReadAsync(Scope(readableProjects: [foreignProject], writableProjects: [foreignProject])));
    }

    [Fact]
    public async Task ABackupOfAProjectTheCallerCannotRead_IsNotListed_WithoutBeingNamed()
    {
        var readable = await SeedProjectAsync("readable", OrganizationA);
        var hidden = await SeedProjectAsync("hidden", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedBackupAsync("visible", "0 2 * * *", readable, server);
        await SeedBackupAsync("secret", "0 3 * * *", hidden, server);

        var actions = await ReadAsync(Scope(readableProjects: [readable]));

        Assert.Equal(["visible"], actions.Select(action => action.SourceName));
        // The servers' rights do not open a project's backups.
        Assert.Empty(await ReadAsync(Scope(readableServers: [server])));
    }

    [Fact]
    public async Task ABackup_IsMovableOnlyWithTheWriteRightOnItsProject()
    {
        var writable = await SeedProjectAsync("a-writable", OrganizationA);
        var readOnly = await SeedProjectAsync("b-read-only", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedBackupAsync("a", "0 2 * * *", writable, server);
        await SeedBackupAsync("b", "0 3 * * *", readOnly, server);

        var actions = await ReadAsync(Scope(readableProjects: [writable, readOnly], writableProjects: [writable]));

        Assert.Equal([true, false], actions.Select(action => action.Movable));
    }

    [Fact]
    public async Task ADisabledBackup_IsNotListed()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedBackupAsync("off", "0 2 * * *", project, server, enabled: false);
        await SeedBackupAsync("blank", string.Empty, project, server);

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task AScopeBuiltFromTheServersAlone_ShowsNeitherAiTasksOfProjectsNorBackups()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        await SeedBackupAsync("b", "0 2 * * *", project, server);
        await SeedAiTaskAsync("ai", "0 0 2 * * *", projectId: project);

        Assert.Empty(await ReadAsync(new ServerActionScope([server], [server])));
        Assert.Empty(await ReadAsync(ServerActionScope.None));
    }

    [Fact]
    public async Task EverySourceCountsInTheSameOrderOfTheList_ScansThenAiTasksThenBackups()
    {
        var project = await SeedProjectAsync("p", OrganizationA);
        var server = await SeedServerAsync("srv", OrganizationA);
        _db.RkhunterStates.Add(new RkhunterState { ServerId = server, ScanScheduleCron = "0 1 * * *" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await SeedAiTaskAsync("ai", "0 0 2 * * *", projectId: project);
        await SeedBackupAsync("b", "0 3 * * *", project, server);

        var actions = await ReadAsync(ServerActionScope.All);

        Assert.Equal([ServerActionKind.RkhunterScan, ServerActionKind.AiTask, ServerActionKind.AppBackup], actions.Select(action => action.Kind));
        Assert.Equal(3, actions.Select(action => action.Id).Distinct().Count());
    }
}
