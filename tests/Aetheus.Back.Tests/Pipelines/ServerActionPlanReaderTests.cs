// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-8, the server actions of the planner: the real repository and reader over an in-memory database (not
/// PostgreSQL: the organization, rights and averaging rules are what is exercised, not the SQL the provider writes).
/// The scope stands for what the controller reads from the RBAC service: the servers the caller reads and, among them,
/// the ones it administers.
/// </summary>
public sealed class ServerActionPlanReaderTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private const int OrganizationA = 1;
    private const int OrganizationB = 2;

    private readonly AppDbContext _db;
    private readonly ServerActionPlanReader _reader;

    public ServerActionPlanReaderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        _reader = new ServerActionPlanReader(new ServerActionScheduleRepository(_db), new FakeTimeProvider(new DateTimeOffset(Now)), Options.Create(new VulnerabilityWatchOptions()));
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedServerAsync(string name, int organizationId, string? cron)
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new Server { Name = name, Hostname = name, OrganizationId = organizationId, Status = ServerStatus.Online };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(ct);
        _db.RkhunterStates.Add(new RkhunterState { ServerId = server.Id, ScanScheduleCron = cron });
        await _db.SaveChangesAsync(ct);
        return server.Id;
    }

    private async Task SeedScanAsync(int serverId, int minutes, TaskExecutionStatus status = TaskExecutionStatus.Success, int daysAgo = 1)
    {
        var started = Now.AddDays(-daysAgo);
        _db.Tasks.Add(new ServerTask
        {
            ServerId = serverId,
            Name = "RKHunter - scheduled scan",
            Operation = OperationKind.RkhunterScan,
            Status = status,
            StartedAt = started,
            CompletedAt = started.AddMinutes(minutes)
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private Task<List<ScheduledServerActionDto>> ReadAsync(ServerActionScope scope, int organizationId = OrganizationA) =>
        _reader.ReadAsync(organizationId, scope, Day, Day.AddDays(1), TestContext.Current.CancellationToken);

    [Fact]
    public async Task OnlyTheServersOfTheOrganization_AreListed_NeverAnotherOrganizations()
    {
        var mine = await SeedServerAsync("srv-mine", OrganizationA, "30 3 * * *");
        var foreign = await SeedServerAsync("srv-foreign", OrganizationB, "30 3 * * *");

        var actions = await ReadAsync(ServerActionScope.All);

        Assert.Equal([mine], actions.Select(action => action.ServerId));
        Assert.Equal("srv-mine", Assert.Single(actions).ServerName);
        Assert.Equal([foreign], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.ServerId));
    }

    [Fact]
    public async Task AServerTheCallerCannotRead_IsNotListed_WithoutBeingNamed()
    {
        var readable = await SeedServerAsync("srv-readable", OrganizationA, "0 2 * * *");
        await SeedServerAsync("srv-hidden", OrganizationA, "0 2 * * *");

        var actions = await ReadAsync(new ServerActionScope([readable], []));

        Assert.Equal([readable], actions.Select(action => action.ServerId));
        Assert.DoesNotContain(actions, action => action.ServerName == "srv-hidden");
    }

    [Fact]
    public async Task AReaderWithoutTheAdministratorRight_SeesTheAction_ButCannotMoveIt()
    {
        var server = await SeedServerAsync("srv", OrganizationA, "0 2 * * *");

        var action = Assert.Single(await ReadAsync(new ServerActionScope([server], [])));

        Assert.False(action.Movable);
    }

    [Fact]
    public async Task AReadableIdOfAnotherOrganizationsServer_ListsNothingForThisOrganization()
    {
        var foreign = await SeedServerAsync("srv-foreign", OrganizationB, "0 2 * * *");

        Assert.Empty(await ReadAsync(new ServerActionScope([foreign], [foreign])));
    }

    [Fact]
    public async Task ACallerWhoReadsNoServer_GetsNothing()
    {
        await SeedServerAsync("srv", OrganizationA, "0 2 * * *");

        Assert.Empty(await ReadAsync(ServerActionScope.None));
        Assert.Empty(await ReadAsync(new ServerActionScope([], null)));
    }

    [Fact]
    public async Task AServerWithoutASchedule_IsNotListed()
    {
        await SeedServerAsync("srv-none", OrganizationA, null);
        await SeedServerAsync("srv-blank", OrganizationA, string.Empty);

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task Movable_OnlyWhereTheCallerAdministersTheServer()
    {
        var administered = await SeedServerAsync("a-administered", OrganizationA, "0 2 * * *");
        var readOnly = await SeedServerAsync("b-read-only", OrganizationA, "0 2 * * *");

        var actions = await ReadAsync(new ServerActionScope([administered, readOnly], [administered]));

        Assert.Equal([administered, readOnly], actions.Select(action => action.ServerId));
        Assert.True(actions[0].Movable);
        Assert.False(actions[1].Movable);
        Assert.True((await ReadAsync(ServerActionScope.All)).All(action => action.Movable));
    }

    [Fact]
    public async Task ASchedule_IsReadInUtcWithItsFrequency_ItsOccurrencesAndItsNextRun()
    {
        var server = await SeedServerAsync("srv", OrganizationA, "30 3 * * *");

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal($"rkhunter:{server}", action.Id);
        Assert.Equal(ServerActionKind.RkhunterScan, action.Kind);
        Assert.Equal("30 3 * * *", action.CronExpression);
        Assert.Equal(ScheduleFrequency.EveryEvening, action.Frequency);
        Assert.Equal(3, action.Hour);
        Assert.Equal(30, action.Minute);
        var run = Assert.Single(action.Occurrences);
        Assert.Equal(Day.AddHours(3).AddMinutes(30), run.StartUtc);
        // No scan ever completed: the planned duration, said as planned.
        Assert.True(action.IsPlanned);
        Assert.Equal(ServerActionDefaults.RkhunterPlannedMinutes, action.DurationMinutes);
        Assert.Equal(run.StartUtc.AddMinutes(ServerActionDefaults.RkhunterPlannedMinutes), run.EndUtc);
        Assert.Equal(new DateTime(2027, 1, 4, 3, 30, 0, DateTimeKind.Utc), action.NextRunUtc);
    }

    [Fact]
    public async Task AWeeklySchedule_IsDecodedAsWeekly()
    {
        await SeedServerAsync("srv", OrganizationA, "5 2 * * 1");

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(ScheduleFrequency.Weekly, action.Frequency);
        Assert.Equal(1, action.Day);
        // 2027-01-04 is a Monday.
        Assert.Single(action.Occurrences);
    }

    [Fact]
    public async Task ACustomSchedule_IsListedAsCustom_WithoutASingleTime()
    {
        await SeedServerAsync("srv", OrganizationA, "*/20 5 * * *");

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(ScheduleFrequency.Custom, action.Frequency);
        Assert.Null(action.Hour);
        Assert.Equal(3, action.Occurrences.Count);
    }

    [Fact]
    public async Task TheDuration_IsTheAverageOfTheLastSuccessfulScans_NotOfTheFailedOnes()
    {
        var server = await SeedServerAsync("srv", OrganizationA, "0 2 * * *");
        await SeedScanAsync(server, 4);
        await SeedScanAsync(server, 8, daysAgo: 2);
        await SeedScanAsync(server, 90, TaskExecutionStatus.Failed, daysAgo: 3);

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.False(action.IsPlanned);
        Assert.Equal(6, action.DurationMinutes);
    }
}
