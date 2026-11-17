// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-27: the jobs a machine schedules by itself, as the planner reads them: the real repository, source and reader
/// over an in-memory database (not PostgreSQL: the stored reading, the zones, the rights and the "show nothing rather than
/// guess" rules are what is exercised). Winter 2027: Paris is UTC+1.
/// </summary>
public sealed class MachineJobPlanTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private const int OrganizationA = 1;
    private const int OrganizationB = 2;
    private const string Paris = "Europe/Paris";

    private readonly AppDbContext _db;
    private readonly ServerActionPlanReader _reader;

    public MachineJobPlanTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        _reader = new ServerActionPlanReader(
            new ServerActionScheduleRepository(_db), clock, Options.Create(new VulnerabilityWatchOptions()),
            new ServerRebootPlanSource(new ServerRebootRepository(_db), clock));
    }

    public void Dispose() => _db.Dispose();

    private static MachineJobEntryDto Cron(string cron = "0 2 * * *", string? zone = Paris, string name = "backup.sh") => new()
    {
        Source = MachineJobSource.CronDirectory,
        Name = name,
        User = "root",
        CronExpression = cron,
        TimeZoneId = zone,
        Detail = "/etc/cron.d/backup"
    };

    private async Task<int> SeedServerAsync(string name, int organizationId, string? json)
    {
        var server = new Server { Name = name, Hostname = name, OrganizationId = organizationId, Status = ServerStatus.Online, MachineJobsJson = json };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return server.Id;
    }

    private Task<int> SeedAsync(string name, int organizationId, params MachineJobEntryDto[] jobs) =>
        SeedServerAsync(name, organizationId, JsonSerializer.Serialize(new MachineJobsDataDto { Collected = true, TimeZoneId = Paris, Jobs = [.. jobs] }));

    private async Task<List<ScheduledServerActionDto>> ReadAsync(ServerActionScope scope, int organizationId = OrganizationA)
    {
        var actions = await _reader.ReadAsync(organizationId, scope, Day, Day.AddDays(1), TestContext.Current.CancellationToken);
        return [.. actions.Where(action => action.Kind == ServerActionKind.MachineJob)];
    }

    [Fact]
    public async Task ADailyJob_IsListedAtItsLocalTime_ReadOnly_WithWhatTheAgentRead()
    {
        var server = await SeedAsync("srv-prod", OrganizationA, Cron());

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(ServerActionKind.MachineJob, action.Kind);
        Assert.Equal(server, action.ServerId);
        Assert.Equal("srv-prod", action.ServerName);
        Assert.Equal("backup.sh", action.SourceName);
        Assert.False(action.Movable);
        Assert.Equal("02:00", action.DailyTime);
        Assert.Equal(Paris, action.DailyTimeZoneId);
        Assert.Equal(MachineJobSource.CronDirectory, action.MachineJob!.Source);
        Assert.Equal("root", action.MachineJob.User);
        // 02:00 in Paris is 01:00 UTC.
        var occurrence = Assert.Single(action.Occurrences);
        Assert.Equal(new DateTime(2027, 1, 4, 1, 0, 0, DateTimeKind.Utc), occurrence.StartUtc);
        Assert.Equal(occurrence.StartUtc.AddMinutes(ServerActionDefaults.MachineJobPlannedMinutes), occurrence.EndUtc);
        Assert.Equal(new DateTime(2027, 1, 4, 1, 0, 0, DateTimeKind.Utc), action.NextRunUtc);
        Assert.Equal("machine-job:" + server + ":0", action.Id);
    }

    [Fact]
    public async Task AJobWithoutAZoneOfItsOwn_IsReadInTheMachineZoneTheAgentSent()
    {
        await SeedAsync("srv", OrganizationA, Cron(zone: null));

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(new DateTime(2027, 1, 4, 1, 0, 0, DateTimeKind.Utc), Assert.Single(action.Occurrences).StartUtc);
        Assert.Equal(Paris, action.MachineJob!.TimeZoneId);
    }

    [Fact]
    public async Task ATimerWithOnlyANextInstant_IsListedAtThatInstant()
    {
        var instant = new DateTime(2027, 1, 4, 9, 30, 0, DateTimeKind.Utc);
        await SeedAsync("srv", OrganizationA, new MachineJobEntryDto
        {
            Source = MachineJobSource.SystemdTimer,
            Name = "apt-daily.timer",
            AtUtc = instant,
            Detail = "apt-daily.timer"
        });

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(instant, Assert.Single(action.Occurrences).StartUtc);
        Assert.Equal(instant, action.NextRunUtc);
        Assert.Null(action.DailyTime);
        Assert.Equal(MachineJobSource.SystemdTimer, action.MachineJob!.Source);
    }

    [Fact]
    public async Task SeveralJobsOfAServer_EachHaveTheirOwnRowAndId()
    {
        await SeedAsync("srv", OrganizationA, Cron("30 3 * * 0", name: "a"), Cron("0 4 * * 1", name: "b"));

        var actions = await ReadAsync(ServerActionScope.All);

        Assert.Equal(2, actions.Count);
        Assert.Empty(actions[0].Occurrences);
        Assert.Equal(new DateTime(2027, 1, 4, 3, 0, 0, DateTimeKind.Utc), Assert.Single(actions[1].Occurrences).StartUtc);
        Assert.NotEqual(actions[0].SourceId, actions[1].SourceId);
        Assert.NotEqual(actions[0].Id, actions[1].Id);
    }

    [Theory]
    [InlineData("0 2 * * *", "Mars/Olympus")]
    [InlineData("not a cron", Paris)]
    [InlineData("0 99 * * *", Paris)]
    public async Task AnUnreadableExpressionOrZone_ShowsNothing_NeverAnInventedTime(string cron, string zone)
    {
        await SeedAsync("srv", OrganizationA, Cron(cron, zone));

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task AJobWithNeitherAnExpressionNorAnInstant_ShowsNothing()
    {
        await SeedAsync("srv", OrganizationA, new MachineJobEntryDto { Source = MachineJobSource.SystemdTimer, Name = "x.timer", Detail = "x.timer" });

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    public async Task AServerWithNoReadableReading_ListsNothing(string? json)
    {
        await SeedServerAsync("srv", OrganizationA, json);

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task AReadingOfAHostWithNothingToRead_ListsNothing()
    {
        await SeedServerAsync("srv", OrganizationA, JsonSerializer.Serialize(new MachineJobsDataDto { Collected = false, Jobs = [Cron()] }));

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task OnlyTheServersOfTheOrganization_AreListed()
    {
        var mine = await SeedAsync("srv-mine", OrganizationA, Cron());
        var foreign = await SeedAsync("srv-foreign", OrganizationB, Cron());

        Assert.Equal([mine], (await ReadAsync(ServerActionScope.All)).Select(action => action.ServerId));
        Assert.Equal([foreign], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.ServerId));
    }

    [Fact]
    public async Task AServerTheCallerCannotRead_IsNotListed_WithoutBeingNamed()
    {
        var readable = await SeedAsync("srv-readable", OrganizationA, Cron());
        await SeedAsync("srv-hidden", OrganizationA, Cron());

        var actions = await ReadAsync(new ServerActionScope([readable], []));

        Assert.Equal([readable], actions.Select(action => action.ServerId));
        Assert.DoesNotContain(actions, action => action.ServerName == "srv-hidden");
    }

    [Fact]
    public async Task ACallerWhoReadsNoServer_GetsNothing()
    {
        await SeedAsync("srv", OrganizationA, Cron());

        Assert.Empty(await ReadAsync(ServerActionScope.None));
        Assert.Empty(await ReadAsync(new ServerActionScope([], null)));
    }

    [Fact]
    public async Task EvenAnAdministrator_CannotMoveAMachineJob()
    {
        var server = await SeedAsync("srv", OrganizationA, Cron());

        var action = Assert.Single(await ReadAsync(new ServerActionScope([server], [server])));

        Assert.False(action.Movable);
        Assert.False(ServerActionDefaults.CanBeMoved(ServerActionKind.MachineJob));
    }

    [Fact]
    public void AMachineJob_UsesNoRunner_SoItIsNeverCountedInTheOverlaps()
    {
        Assert.False(ServerActionDefaults.CountsInOverlaps(ServerActionKind.MachineJob));
        Assert.Equal(ServerActionDefaults.MachineJobPlannedMinutes, ServerActionDefaults.PlannedMinutes(ServerActionKind.MachineJob));
    }

    [Fact]
    public async Task TheMachinesOwnRebootsAndJobs_AreListedTogether_EachInItsOwnKind()
    {
        var server = await SeedAsync("srv", OrganizationA, Cron());
        (await _db.Servers.FindAsync([server], TestContext.Current.CancellationToken))!.MachineRebootJson = JsonSerializer.Serialize(new MachineRebootDataDto
        {
            Collected = true,
            Entries = [new MachineRebootEntryDto { Source = MachineRebootSource.Cron, CronExpression = "0 5 * * *", TimeZoneId = Paris, Detail = "/etc/crontab" }]
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actions = await _reader.ReadAsync(OrganizationA, ServerActionScope.All, Day, Day.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(
            [ServerActionKind.MachineReboot, ServerActionKind.MachineJob],
            actions.Where(action => action.Kind is ServerActionKind.MachineReboot or ServerActionKind.MachineJob).Select(action => action.Kind));
    }
}
