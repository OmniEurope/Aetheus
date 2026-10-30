// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// Query-level behaviour of the AI-tasks repository: the filters, the ordering, the paging windows
/// and the RBAC narrowing the service layer delegates to it. Every case seeds rows that must be
/// excluded as well as rows that must survive, so a dropped predicate fails instead of passing.
/// </summary>
public sealed class AiTaskRepositoryTests : IDisposable
{
    /// <summary>
    /// <see cref="AppDbContext"/> stamps <c>CreatedAt</c> from its <see cref="TimeProvider"/> on
    /// every insert, so seeding the column directly is silently overwritten. Ordering cases move
    /// this clock between saves instead, which is how the production timestamps really appear.
    /// </summary>
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;
    private readonly AiTaskRepository _repository;

    public AiTaskRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options, _clock);
        _repository = new AiTaskRepository(_db);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    /// <summary>Inserts one row as if the wall clock read <paramref name="instant"/>.</summary>
    private async Task SaveAtAsync(DateTime instant, object entity)
    {
        _clock.SetUtcNow(new DateTimeOffset(instant));
        _db.Add(entity);
        await _db.SaveChangesAsync(Ct);
    }

    private static AiRunnerProfile Profile(int id, string name, int organizationId = 1) =>
        new() { Id = id, Name = name, OrganizationId = organizationId, Model = "claude-sonnet-5-5" };

    private static AiTaskDefinition Definition(
        int id, string name, int profileId = 1, int? projectId = null, int? serverId = null,
        bool enabled = true, string? schedule = null) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = profileId,
            ProjectId = projectId,
            ServerId = serverId,
            Enabled = enabled,
            Schedule = schedule
        };

    // ---------- profiles ----------

    [Fact]
    public async Task GetProfilesPageAsync_FiltersOnNameAndReportsTheUnpagedTotal()
    {
        _db.AiRunnerProfiles.AddRange(
            Profile(1, "claude-review"),
            Profile(2, "claude-fix"),
            Profile(3, "codex-review"));
        await SaveAsync();

        var (items, total) = await _repository.GetProfilesPageAsync("claude", 1, 1, Ct, sortBy: "Name");

        Assert.Equal(2, total);
        Assert.Equal(["claude-fix"], items.Select(profile => profile.Name));
    }

    [Fact]
    public async Task GetProfilesPageAsync_OrdersByNameAcrossPages_WhenNameIsRequested()
    {
        _db.AiRunnerProfiles.AddRange(Profile(1, "gamma"), Profile(2, "alpha"), Profile(3, "beta"));
        await SaveAsync();

        var first = await _repository.GetProfilesPageAsync(null, 1, 2, Ct, sortBy: "Name");
        var second = await _repository.GetProfilesPageAsync(null, 2, 2, Ct, sortBy: "Name");

        Assert.Equal(["alpha", "beta"], first.Items.Select(profile => profile.Name));
        Assert.Equal(["gamma"], second.Items.Select(profile => profile.Name));
        Assert.Equal(3, first.Total);
    }

    [Fact]
    public async Task GetProfilesPageAsync_SortsByModelBeforePaging()
    {
        _db.AiRunnerProfiles.AddRange(
            new AiRunnerProfile { Id = 1, Name = "alpha", OrganizationId = 1, Model = "a" },
            new AiRunnerProfile { Id = 2, Name = "beta", OrganizationId = 1, Model = "z" },
            new AiRunnerProfile { Id = 3, Name = "gamma", OrganizationId = 1, Model = "m" });
        await SaveAsync();

        var first = await _repository.GetProfilesPageAsync(null, 1, 2, Ct,
            sortBy: "Model", sortDescending: true);
        var second = await _repository.GetProfilesPageAsync(null, 2, 2, Ct,
            sortBy: "Model", sortDescending: true);

        Assert.Equal(["beta", "gamma"], first.Items.Select(profile => profile.Name));
        Assert.Equal(["alpha"], second.Items.Select(profile => profile.Name));
    }

    [Fact]
    public async Task GetProfilesForOrganizationsAsync_KeepsOnlyTheRequestedOrganizations()
    {
        _db.AiRunnerProfiles.AddRange(
            Profile(1, "zulu", organizationId: 7),
            Profile(2, "alpha", organizationId: 7),
            Profile(3, "other", organizationId: 8));
        await SaveAsync();

        var profiles = await _repository.GetProfilesForOrganizationsAsync([7], Ct);

        Assert.Equal(["alpha", "zulu"], profiles.Select(profile => profile.Name));
    }

    [Fact]
    public async Task GetOrganizationProfilesByPriorityAsync_IsScopedToTheOrganization_LowestPriorityThenIdFirst()
    {
        var late = Profile(1, "late", organizationId: 7);
        late.Priority = 30;
        var tieB = Profile(3, "tie-b", organizationId: 7);
        tieB.Priority = 10;
        var tieA = Profile(2, "tie-a", organizationId: 7);
        tieA.Priority = 10;
        var foreign = Profile(4, "foreign", organizationId: 8);
        foreign.Priority = 1;
        _db.AiRunnerProfiles.AddRange(late, tieB, tieA, foreign);
        await SaveAsync();

        var profiles = await _repository.GetOrganizationProfilesByPriorityAsync(7, Ct);
        var none = await _repository.GetOrganizationProfilesByPriorityAsync(9, Ct);

        Assert.Equal(["tie-a", "tie-b", "late"], profiles.Select(profile => profile.Name));
        Assert.Empty(none);
    }

    [Fact]
    public async Task GetProfilesPageAsync_DefaultsToThePriorityOrder_AndSortsOnPriorityOnRequest()
    {
        var first = Profile(1, "zulu");
        first.Priority = 5;
        var second = Profile(2, "alpha");
        second.Priority = 20;
        _db.AiRunnerProfiles.AddRange(second, first);
        await SaveAsync();

        var byDefault = await _repository.GetProfilesPageAsync(null, 1, 10, Ct);
        var descending = await _repository.GetProfilesPageAsync(null, 1, 10, Ct, sortBy: "Priority", sortDescending: true);

        Assert.Equal(["zulu", "alpha"], byDefault.Items.Select(profile => profile.Name));
        Assert.Equal(["alpha", "zulu"], descending.Items.Select(profile => profile.Name));
    }

    [Fact]
    public async Task AddProfileAsync_ThenFindProfileAsync_RoundTripsThroughTheStore()
    {
        await _repository.AddProfileAsync(Profile(1, "claude"), Ct);

        var found = await _repository.FindProfileAsync(1, Ct);

        Assert.Equal("claude", found!.Name);
    }

    [Fact]
    public async Task RemoveProfileAsync_DeletesTheRow()
    {
        var profile = Profile(1, "claude");
        await _repository.AddProfileAsync(profile, Ct);

        await _repository.RemoveProfileAsync(profile, Ct);

        Assert.Null(await _repository.FindProfileAsync(1, Ct));
    }

    [Fact]
    public async Task ProfileHasDefinitionsAsync_SeesOnlyItsOwnDefinitions()
    {
        _db.AiTaskDefinitions.Add(Definition(1, "review", profileId: 4));
        await SaveAsync();

        Assert.True(await _repository.ProfileHasDefinitionsAsync(4, Ct));
        Assert.False(await _repository.ProfileHasDefinitionsAsync(5, Ct));
    }

    // ---------- definitions ----------

    [Fact]
    public async Task GetDefinitionsPageAsync_NarrowsToTheRequestedProjectAndServer()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "project-scoped", projectId: 10),
            Definition(2, "server-scoped", serverId: 20),
            Definition(3, "elsewhere", projectId: 11));
        await SaveAsync();

        var byProject = await _repository.GetDefinitionsPageAsync(
            null, 1, 10, 10, null, null, null, Ct);
        var byServer = await _repository.GetDefinitionsPageAsync(
            null, 1, 10, null, 20, null, null, Ct);

        Assert.Equal(["project-scoped"], byProject.Items.Select(definition => definition.Name));
        Assert.Equal(["server-scoped"], byServer.Items.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsPageAsync_AppliesTheAccessibleScopesAsAUnion()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "allowed-project", projectId: 10),
            Definition(2, "allowed-server", serverId: 20),
            Definition(3, "denied-project", projectId: 99),
            Definition(4, "unscoped"));
        await SaveAsync();

        var (items, total) = await _repository.GetDefinitionsPageAsync(
            null, 1, 10, null, null, [10], [20], Ct);

        Assert.Equal(2, total);
        Assert.Equal(["allowed-project", "allowed-server"], items.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsPageAsync_TreatsAMissingScopeListAsAnEmptyAllowance()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "project-only", projectId: 10),
            Definition(2, "server-only", serverId: 20));
        await SaveAsync();

        var (items, _) = await _repository.GetDefinitionsPageAsync(
            null, 1, 10, null, null, [10], null, Ct);

        Assert.Equal(["project-only"], items.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsPageAsync_SearchesTheNameAndPagesTheOrderedResult()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "review-c"),
            Definition(2, "review-a"),
            Definition(3, "review-b"),
            Definition(4, "deploy"));
        await SaveAsync();

        var (items, total) = await _repository.GetDefinitionsPageAsync(
            "review", 2, 2, null, null, null, null, Ct);

        Assert.Equal(3, total);
        Assert.Equal(["review-c"], items.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsPageAsync_SortsByEnabledBeforePaging()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "alpha", enabled: false),
            Definition(2, "beta", enabled: true),
            Definition(3, "gamma", enabled: true));
        await SaveAsync();

        var first = await _repository.GetDefinitionsPageAsync(null, 1, 2,
            null, null, null, null, Ct, sortBy: "Enabled", sortDescending: true);
        var second = await _repository.GetDefinitionsPageAsync(null, 2, 2,
            null, null, null, null, Ct, sortBy: "Enabled", sortDescending: true);

        Assert.Equal(["beta", "gamma"], first.Items.Select(definition => definition.Name));
        Assert.Equal(["alpha"], second.Items.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsPageAsync_MaterializesTheProfileProjectServerAndTriggers()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 1 });
        _db.Servers.Add(new Server { Id = 20, Name = "runner" });
        _db.AiTaskDefinitions.Add(new AiTaskDefinition
        {
            Id = 1,
            Name = "review",
            ProfileId = 1,
            ProjectId = 10,
            ServerId = 20,
            Triggers = [new AiTaskTrigger { Id = 1, EventType = "push" }]
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, _) = await _repository.GetDefinitionsPageAsync(
            null, 1, 10, null, null, null, null, Ct);

        var definition = Assert.Single(items);
        Assert.Equal("claude", definition.Profile.Name);
        Assert.Equal("aetheus", definition.Project!.Name);
        Assert.Equal("runner", definition.Server!.Name);
        Assert.Equal(["push"], definition.Triggers.Select(trigger => trigger.EventType));
    }

    [Fact]
    public async Task FindDefinitionAsync_LoadsTheGraphAndReturnsNullForAnUnknownId()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.Add(new AiTaskDefinition
        {
            Id = 1,
            Name = "review",
            ProfileId = 1,
            Triggers = [new AiTaskTrigger { Id = 1, EventType = "release" }]
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var found = await _repository.FindDefinitionAsync(1, Ct);

        Assert.Equal("claude", found!.Profile.Name);
        Assert.Equal(["release"], found.Triggers.Select(trigger => trigger.EventType));
        Assert.Null(await _repository.FindDefinitionAsync(2, Ct));
    }

    [Fact]
    public async Task AddDefinitionAsync_ThenRemoveDefinitionAsync_RoundTripsThroughTheStore()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        await SaveAsync();
        var definition = Definition(1, "review");
        await _repository.AddDefinitionAsync(definition, Ct);
        Assert.NotNull(await _repository.FindDefinitionAsync(1, Ct));

        await _repository.RemoveDefinitionAsync(definition, Ct);

        Assert.Null(await _repository.FindDefinitionAsync(1, Ct));
    }

    [Fact]
    public async Task GetScheduledDefinitionsAsync_RequiresBothEnabledAndASchedule()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(1, "scheduled", schedule: "0 * * * *"),
            Definition(2, "disabled", enabled: false, schedule: "0 * * * *"),
            Definition(3, "manual"));
        await SaveAsync();

        var scheduled = await _repository.GetScheduledDefinitionsAsync(Ct);

        Assert.Equal(["scheduled"], scheduled.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsForEventAsync_MatchesTheTriggerTypeOnEnabledDefinitionsOnly()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            new AiTaskDefinition
            {
                Id = 1,
                Name = "on-push",
                ProfileId = 1,
                ProjectId = 5,
                Triggers = [new AiTaskTrigger { Id = 1, EventType = "push" }]
            },
            new AiTaskDefinition
            {
                Id = 2,
                Name = "on-push-disabled",
                ProfileId = 1,
                ProjectId = 5,
                Enabled = false,
                Triggers = [new AiTaskTrigger { Id = 2, EventType = "push" }]
            },
            new AiTaskDefinition
            {
                Id = 3,
                Name = "on-release",
                ProfileId = 1,
                ProjectId = 5,
                Triggers = [new AiTaskTrigger { Id = 3, EventType = "release" }]
            });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var matched = await _repository.GetDefinitionsForEventAsync("push", 5, null, Ct);

        Assert.Equal(["on-push"], matched.Select(definition => definition.Name));
    }

    [Fact]
    public async Task GetDefinitionsForEventAsync_OnlyTheEventOwnersDefinitionsAcrossOrganizations()
    {
        // Two organizations, each with a server and its own AI definition on the same event type.
        _db.AiRunnerProfiles.AddRange(Profile(1, "org-one", organizationId: 1), Profile(2, "org-two", organizationId: 2));
        _db.Servers.AddRange(
            new Server { Id = 10, Name = "server-a", OrganizationId = 1 },
            new Server { Id = 20, Name = "server-b", OrganizationId = 2 });
        _db.AiTaskDefinitions.AddRange(
            new AiTaskDefinition
            {
                Id = 1,
                Name = "server-a-alert",
                ProfileId = 1,
                ServerId = 10,
                Triggers = [new AiTaskTrigger { Id = 1, EventType = "alert.triggered" }]
            },
            new AiTaskDefinition
            {
                Id = 2,
                Name = "server-b-alert",
                ProfileId = 2,
                ServerId = 20,
                Triggers = [new AiTaskTrigger { Id = 2, EventType = "alert.triggered" }]
            },
            new AiTaskDefinition
            {
                Id = 3,
                Name = "project-alert",
                ProfileId = 2,
                ProjectId = 30,
                Triggers = [new AiTaskTrigger { Id = 3, EventType = "alert.triggered" }]
            });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var forServerA = await _repository.GetDefinitionsForEventAsync("alert.triggered", null, 10, Ct);
        var forServerB = await _repository.GetDefinitionsForEventAsync("alert.triggered", null, 20, Ct);
        var forProject = await _repository.GetDefinitionsForEventAsync("alert.triggered", 30, null, Ct);
        var unowned = await _repository.GetDefinitionsForEventAsync("alert.triggered", null, null, Ct);

        Assert.Equal(["server-a-alert"], forServerA.Select(definition => definition.Name));
        Assert.Equal(["server-b-alert"], forServerB.Select(definition => definition.Name));
        Assert.Equal(["project-alert"], forProject.Select(definition => definition.Name));
        Assert.Empty(unowned);
    }

    // ---------- execution target resolution ----------

    [Fact]
    public async Task ResolveExecutionServerAsync_PrefersThePinnedServer()
    {
        _db.Servers.Add(new Server { Id = 20, Name = "pinned" });
        await SaveAsync();

        var server = await _repository.ResolveExecutionServerAsync(
            Definition(1, "review", serverId: 20), Ct);

        Assert.Equal("pinned", server!.Name);
    }

    [Fact]
    public async Task ResolveExecutionServerAsync_PicksTheOnlineRunnerOfTheProject()
    {
        // The project row exists (its servers are those of its own organization, RBAC audit finding 2).
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 0 });
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "offline-runner", Status = ServerStatus.Offline, PipelineRunnerEnabled = true },
            new Server { Id = 2, Name = "online-plain", Status = ServerStatus.Online },
            new Server { Id = 3, Name = "online-runner", Status = ServerStatus.Online, PipelineRunnerEnabled = true });
        _db.ProjectServers.AddRange(
            new ProjectServer { Id = 1, ProjectId = 10, ServerId = 1 },
            new ProjectServer { Id = 2, ProjectId = 10, ServerId = 2 },
            new ProjectServer { Id = 3, ProjectId = 10, ServerId = 3 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var server = await _repository.ResolveExecutionServerAsync(
            Definition(1, "review", projectId: 10), Ct);

        Assert.Equal("online-runner", server!.Name);
    }

    [Fact]
    public async Task ResolveExecutionServerAsync_ReturnsNullWhenNoProjectServerIsOnline()
    {
        _db.Servers.Add(new Server { Id = 1, Name = "offline", Status = ServerStatus.Offline });
        _db.ProjectServers.Add(new ProjectServer { Id = 1, ProjectId = 10, ServerId = 1 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var server = await _repository.ResolveExecutionServerAsync(
            Definition(1, "review", projectId: 10), Ct);

        Assert.Null(server);
    }

    [Fact]
    public async Task GetProjectOrganizationIdAsync_AndGetServerOrganizationIdAsync_ReadTheOwner()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Servers.Add(new Server { Id = 20, Name = "runner", OrganizationId = 8 });
        await SaveAsync();

        Assert.Equal(7, await _repository.GetProjectOrganizationIdAsync(10, Ct));
        Assert.Equal(8, await _repository.GetServerOrganizationIdAsync(20, Ct));
        Assert.Null(await _repository.GetProjectOrganizationIdAsync(11, Ct));
        Assert.Null(await _repository.GetServerOrganizationIdAsync(21, Ct));
    }

    [Fact]
    public async Task GetPrimaryProjectRepositoryAsync_SkipsEmptyReposAndTakesTheLowestId()
    {
        _db.GitInternalRepos.AddRange(
            new GitInternalRepo { Id = 1, ProjectId = 10, Name = "empty", IsEmpty = true },
            new GitInternalRepo { Id = 2, ProjectId = 10, Name = "primary", IsEmpty = false },
            new GitInternalRepo { Id = 3, ProjectId = 10, Name = "secondary", IsEmpty = false },
            new GitInternalRepo { Id = 4, ProjectId = 11, Name = "other-project", IsEmpty = false });
        await SaveAsync();

        var repository = await _repository.GetPrimaryProjectRepositoryAsync(10, Ct);

        Assert.Equal("primary", repository!.Name);
    }

    [Fact]
    public async Task GetActiveRunCountAsync_CountsOnlyUnfinishedAiRunsOfThatServer()
    {
        _db.Tasks.AddRange(
            new ServerTask { Id = 1, ServerId = 20, Operation = OperationKind.AiRun, Status = TaskExecutionStatus.Pending },
            new ServerTask { Id = 2, ServerId = 20, Operation = OperationKind.AiRun, Status = TaskExecutionStatus.Assigned },
            new ServerTask { Id = 3, ServerId = 20, Operation = OperationKind.AiRun, Status = TaskExecutionStatus.Running },
            new ServerTask { Id = 4, ServerId = 20, Operation = OperationKind.AiRun, Status = TaskExecutionStatus.Success },
            new ServerTask { Id = 5, ServerId = 20, Operation = OperationKind.None, Status = TaskExecutionStatus.Running },
            new ServerTask { Id = 6, ServerId = 21, Operation = OperationKind.AiRun, Status = TaskExecutionStatus.Running });
        await SaveAsync();

        Assert.Equal(3, await _repository.GetActiveRunCountAsync(20, Ct));
    }

    [Fact]
    public async Task AddTaskAsync_PersistsAndReturnsTheTaskWithItsServerLoaded()
    {
        _db.Servers.Add(new Server { Id = 20, Name = "runner" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var stored = await _repository.AddTaskAsync(
            new ServerTask { Id = 1, ServerId = 20, Name = "ai-run" }, Ct);

        Assert.Equal("runner", stored.Server!.Name);
        Assert.Equal("ai-run", (await _repository.FindTaskAsync(1, Ct))!.Name);
    }

    // ---------- results ----------

    [Fact]
    public async Task FindResultByTaskAsync_LooksTheResultUpByItsOwningTask()
    {
        await _repository.AddResultAsync(
            new AiRunResult { Id = 1, ServerTaskId = 42, ProfileName = "claude" }, Ct);

        Assert.Equal(1, (await _repository.FindResultByTaskAsync(42, Ct))!.Id);
        Assert.Null(await _repository.FindResultByTaskAsync(43, Ct));
    }

    [Fact]
    public async Task FindResultAsync_LoadsTheDefinitionAndThePipelineOfTheRun()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.Add(Definition(5, "review"));
        _db.Pipelines.Add(new Pipeline { Id = 3, Name = "build" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 9, PipelineId = 3 });
        _db.AiRunResults.Add(new AiRunResult
        {
            Id = 1,
            ServerTaskId = 42,
            AiTaskDefinitionId = 5,
            PipelineRunId = 9,
            ProfileName = "claude"
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var result = await _repository.FindResultAsync(1, Ct);

        Assert.Equal("review", result!.AiTaskDefinition!.Name);
        Assert.Equal("build", result.PipelineRun!.Pipeline.Name);
    }

    [Fact]
    public async Task GetResultsPageAsync_FiltersByDefinitionAndRunThenOrdersNewestFirst()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(Definition(5, "five"), Definition(6, "six"));
        _db.Pipelines.Add(new Pipeline { Id = 3, Name = "build" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 9, PipelineId = 3 });
        await SaveAsync();
        await SaveAtAsync(Origin, new AiRunResult { Id = 1, AiTaskDefinitionId = 5, ProfileName = "a" });
        await SaveAtAsync(Origin.AddMinutes(5), new AiRunResult { Id = 2, AiTaskDefinitionId = 5, ProfileName = "b" });
        await SaveAtAsync(Origin.AddMinutes(10), new AiRunResult { Id = 3, AiTaskDefinitionId = 6, ProfileName = "c" });
        await SaveAtAsync(Origin.AddMinutes(15), new AiRunResult { Id = 4, PipelineRunId = 9, ProfileName = "d" });
        _db.ChangeTracker.Clear();

        var byDefinition = await _repository.GetResultsPageAsync(5, null, 1, 10, Ct);
        var byRun = await _repository.GetResultsPageAsync(null, 9, 1, 10, Ct);

        Assert.Equal(2, byDefinition.Total);
        Assert.Equal([2, 1], byDefinition.Items.Select(result => result.Id));
        Assert.Equal([4], byRun.Items.Select(result => result.Id));
    }

    [Fact]
    public async Task GetResultsPageAsync_PagesTheUnfilteredHistory()
    {
        await SaveAtAsync(Origin, new AiRunResult { Id = 1, ProfileName = "a" });
        await SaveAtAsync(Origin.AddMinutes(5), new AiRunResult { Id = 2, ProfileName = "b" });
        await SaveAtAsync(Origin.AddMinutes(10), new AiRunResult { Id = 3, ProfileName = "c" });
        _db.ChangeTracker.Clear();

        var (items, total) = await _repository.GetResultsPageAsync(null, null, 2, 2, Ct);

        Assert.Equal(3, total);
        Assert.Equal([1], items.Select(result => result.Id));
    }

    [Fact]
    public async Task GetConsumptionByProfileAsync_AggregatesRunsFailuresAndDurationSinceTheCutoff()
    {
        // The clock only moves forward, so the row that must fall outside the window is inserted first.
        var since = Origin.AddHours(1);
        await SaveAtAsync(Origin, new AiRunResult { Id = 4, ProfileName = "alpha", Succeeded = true, DurationMs = 999 });
        await SaveAtAsync(since, new AiRunResult { Id = 1, ProfileName = "zulu", Succeeded = true, DurationMs = 100 });
        await SaveAtAsync(since.AddHours(1), new AiRunResult { Id = 2, ProfileName = "zulu", Succeeded = false, DurationMs = 300 });
        await SaveAtAsync(since.AddHours(2), new AiRunResult { Id = 3, ProfileName = "alpha", Succeeded = true, DurationMs = 50 });
        _db.ChangeTracker.Clear();

        var consumption = await _repository.GetConsumptionByProfileAsync(since, null, Ct);

        Assert.Equal(["alpha", "zulu"], consumption.Select(row => row.ProfileName));
        Assert.Equal(1, consumption[0].RunCount);
        Assert.Equal(50, consumption[0].DurationMs);
        Assert.Equal(2, consumption[1].RunCount);
        Assert.Equal(1, consumption[1].FailedCount);
        Assert.Equal(400, consumption[1].DurationMs);
    }

    [Fact]
    public async Task GetConsumptionByProfileAsync_NarrowsToTheProjectOfTheDefinition()
    {
        _db.AiRunnerProfiles.Add(Profile(1, "claude"));
        _db.AiTaskDefinitions.AddRange(
            Definition(5, "in-project", projectId: 10),
            Definition(6, "other-project", projectId: 11));
        _db.AiRunResults.AddRange(
            new AiRunResult { Id = 1, AiTaskDefinitionId = 5, ProfileName = "kept", DurationMs = 10 },
            new AiRunResult { Id = 2, AiTaskDefinitionId = 6, ProfileName = "dropped", DurationMs = 10 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var consumption = await _repository.GetConsumptionByProfileAsync(Origin, 10, Ct);

        Assert.Equal(["kept"], consumption.Select(row => row.ProfileName));
    }

    [Fact]
    public async Task GetRunPipelineIdAsync_ReturnsThePipelineOrNullForAnUnknownRun()
    {
        _db.Pipelines.Add(new Pipeline { Id = 3, Name = "build" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 9, PipelineId = 3 });
        await SaveAsync();

        Assert.Equal(3, await _repository.GetRunPipelineIdAsync(9, Ct));
        Assert.Null(await _repository.GetRunPipelineIdAsync(10, Ct));
    }

    [Fact]
    public async Task SaveChangesAsync_FlushesAMutationMadeOnATrackedEntity()
    {
        var profile = Profile(1, "claude");
        await _repository.AddProfileAsync(profile, Ct);
        _db.ChangeTracker.Clear();

        var tracked = await _repository.FindProfileAsync(1, Ct);
        tracked!.Name = "claude-renamed";
        await _repository.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal("claude-renamed", (await _repository.FindProfileAsync(1, Ct))!.Name);
    }

    public void Dispose() => _db.Dispose();
}
