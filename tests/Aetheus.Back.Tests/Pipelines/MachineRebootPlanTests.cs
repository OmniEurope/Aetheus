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
/// Recette R-11: the reboots a machine decides by itself, as the planner reads them: the real repository, source and
/// reader over an in-memory database (not PostgreSQL: the stored reading, the zones, the rights and the "show nothing
/// rather than guess" rules are what is exercised). Winter 2027: Paris is UTC+1.
/// </summary>
public sealed class MachineRebootPlanTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private const int OrganizationA = 1;
    private const int OrganizationB = 2;

    private readonly AppDbContext _db;
    private readonly ServerActionPlanReader _reader;

    public MachineRebootPlanTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        _reader = new ServerActionPlanReader(
            new ServerActionScheduleRepository(_db), clock, Options.Create(new VulnerabilityWatchOptions()),
            new ServerRebootPlanSource(new ServerRebootRepository(_db), clock));
    }

    public void Dispose() => _db.Dispose();

    private static MachineRebootEntryDto Daily(string cron = "0 2 * * *", string zone = "Europe/Paris") => new()
    {
        Source = MachineRebootSource.UnattendedUpgrades,
        CronExpression = cron,
        TimeZoneId = zone,
        Conditional = true,
        Detail = "/etc/apt/apt.conf.d/50unattended-upgrades"
    };

    private async Task<int> SeedServerAsync(string name, int organizationId, string? json)
    {
        var server = new Server { Name = name, Hostname = name, OrganizationId = organizationId, Status = ServerStatus.Online, MachineRebootJson = json };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return server.Id;
    }

    private Task<int> SeedAsync(string name, int organizationId, params MachineRebootEntryDto[] entries) =>
        SeedServerAsync(name, organizationId, JsonSerializer.Serialize(new MachineRebootDataDto { Collected = true, Entries = [.. entries] }));

    private async Task<List<ScheduledServerActionDto>> ReadAsync(ServerActionScope scope, int organizationId = OrganizationA)
    {
        var actions = await _reader.ReadAsync(organizationId, scope, Day, Day.AddDays(1), TestContext.Current.CancellationToken);
        return [.. actions.Where(action => action.Kind == ServerActionKind.MachineReboot)];
    }

    [Fact]
    public async Task ADailyMachineReboot_IsListedAtItsLocalTime_ReadOnly_WithWhatTheAgentRead()
    {
        var server = await SeedAsync("srv-prod", OrganizationA, Daily());

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(ServerActionKind.MachineReboot, action.Kind);
        Assert.Equal(server, action.ServerId);
        Assert.Equal("srv-prod", action.ServerName);
        Assert.False(action.Movable);
        Assert.Equal("02:00", action.DailyTime);
        Assert.Equal("Europe/Paris", action.DailyTimeZoneId);
        Assert.Equal(MachineRebootSource.UnattendedUpgrades, action.MachineReboot!.Source);
        Assert.True(action.MachineReboot.Conditional);
        // 02:00 in Paris is 01:00 UTC; the day window of 2027-01-04 holds one occurrence, the next one is that very day.
        var occurrence = Assert.Single(action.Occurrences);
        Assert.Equal(new DateTime(2027, 1, 4, 1, 0, 0, DateTimeKind.Utc), occurrence.StartUtc);
        Assert.Equal(occurrence.StartUtc.AddMinutes(ServerRebootOrder.PlannedMinutes), occurrence.EndUtc);
        Assert.Equal(new DateTime(2027, 1, 4, 1, 0, 0, DateTimeKind.Utc), action.NextRunUtc);
        Assert.Equal("machine-reboot:" + server + ":0", action.Id);
    }

    [Fact]
    public async Task ASingleScheduledShutdown_IsListedAtItsInstant()
    {
        var instant = new DateTime(2027, 1, 4, 9, 30, 0, DateTimeKind.Utc);
        await SeedAsync("srv", OrganizationA, new MachineRebootEntryDto
        {
            Source = MachineRebootSource.ScheduledShutdown,
            AtUtc = instant,
            Detail = "/run/systemd/shutdown/scheduled"
        });

        var action = Assert.Single(await ReadAsync(ServerActionScope.All));

        Assert.Equal(instant, Assert.Single(action.Occurrences).StartUtc);
        Assert.Equal(instant, action.NextRunUtc);
        Assert.Null(action.DailyTime);
        Assert.Equal(MachineRebootSource.ScheduledShutdown, action.MachineReboot!.Source);
    }

    [Fact]
    public async Task AWeeklyEntry_ListsOnlyTheOccurrencesOfTheWindow()
    {
        // 2027-01-04 is a Monday; Sunday 03:30 Paris falls outside this day, Monday 04:00 inside.
        await SeedAsync("srv", OrganizationA, Daily("30 3 * * 0"), Daily("0 4 * * 1"));

        var actions = await ReadAsync(ServerActionScope.All);

        Assert.Equal(2, actions.Count);
        Assert.Empty(actions[0].Occurrences);
        Assert.Equal(new DateTime(2027, 1, 4, 3, 0, 0, DateTimeKind.Utc), Assert.Single(actions[1].Occurrences).StartUtc);
        Assert.NotEqual(actions[0].SourceId, actions[1].SourceId);
        Assert.NotEqual(actions[0].Id, actions[1].Id);
        Assert.Null(actions[1].DailyTime);
    }

    [Fact]
    public async Task AnEntryWithNoTime_RebootNowAfterAnUpgrade_IsNeverDrawn()
    {
        await SeedAsync("srv", OrganizationA, new MachineRebootEntryDto { Source = MachineRebootSource.UnattendedUpgrades, Conditional = true });

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Theory]
    [InlineData("0 2 * * *", "Mars/Olympus")]
    [InlineData("0 2 * * *", "")]
    [InlineData("not a cron", "Europe/Paris")]
    [InlineData("0 99 * * *", "Europe/Paris")]
    public async Task AnUnreadableExpressionOrZone_ShowsNothing_NeverAnInventedTime(string cron, string zone)
    {
        await SeedAsync("srv", OrganizationA, Daily(cron, zone));

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
        await SeedServerAsync("srv", OrganizationA, JsonSerializer.Serialize(new MachineRebootDataDto { Collected = false, Entries = [Daily()] }));

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task OnlyTheServersOfTheOrganization_AreListed()
    {
        var mine = await SeedAsync("srv-mine", OrganizationA, Daily());
        var foreign = await SeedAsync("srv-foreign", OrganizationB, Daily());

        Assert.Equal([mine], (await ReadAsync(ServerActionScope.All)).Select(action => action.ServerId));
        Assert.Equal([foreign], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.ServerId));
    }

    [Fact]
    public async Task AServerTheCallerCannotRead_IsNotListed_WithoutBeingNamed()
    {
        var readable = await SeedAsync("srv-readable", OrganizationA, Daily());
        await SeedAsync("srv-hidden", OrganizationA, Daily());

        var actions = await ReadAsync(new ServerActionScope([readable], []));

        Assert.Equal([readable], actions.Select(action => action.ServerId));
        Assert.DoesNotContain(actions, action => action.ServerName == "srv-hidden");
    }

    [Fact]
    public async Task ACallerWhoReadsNoServer_GetsNothing()
    {
        await SeedAsync("srv", OrganizationA, Daily());

        Assert.Empty(await ReadAsync(ServerActionScope.None));
        Assert.Empty(await ReadAsync(new ServerActionScope([], null)));
    }

    [Fact]
    public async Task EvenAnAdministrator_CannotMoveAMachineReboot()
    {
        var server = await SeedAsync("srv", OrganizationA, Daily());

        var action = Assert.Single(await ReadAsync(new ServerActionScope([server], [server])));

        Assert.False(action.Movable);
        Assert.False(ServerActionDefaults.CanBeMoved(ServerActionKind.MachineReboot));
    }

    [Fact]
    public void AMachineReboot_UsesNoRunner_SoItIsNeverCountedInTheOverlaps_ButTheRebootOrderedByAetheusIs()
    {
        Assert.False(ServerActionDefaults.CountsInOverlaps(ServerActionKind.MachineReboot));
        Assert.True(ServerActionDefaults.CountsInOverlaps(ServerActionKind.ServerReboot));
    }

    [Fact]
    public async Task TheRebootsAetheusOrders_AreStillListed_BesideTheMachinesOwn()
    {
        var server = await SeedAsync("srv", OrganizationA, Daily());
        _db.ServerRebootSchedules.Add(new ServerRebootSchedule { ServerId = server, Enabled = true, Hour = 5, Minute = 0, TimeZoneId = "UTC", Version = 1 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actions = await _reader.ReadAsync(OrganizationA, ServerActionScope.All, Day, Day.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(
            [ServerActionKind.ServerReboot, ServerActionKind.MachineReboot],
            actions.Where(action => action.Kind is ServerActionKind.ServerReboot or ServerActionKind.MachineReboot).Select(action => action.Kind));
    }
}
