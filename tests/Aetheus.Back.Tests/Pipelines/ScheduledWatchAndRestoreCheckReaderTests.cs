// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-8, the restore-check cadence of the backups and the CVE watch in the planner: the real repository and reader
/// over an in-memory database (the organization, rights, interval and last-pass rules are what is exercised; the real
/// PostgreSQL proof of who sees the CVE watch is the HTTP integration test). The CVE watch is movable, site-wide, listed on
/// request of the scope (a site administrator): one pass a day at the configured local time (04:00 Europe/Paris by default),
/// computed from the configuration, never from the last pass.
/// </summary>
public sealed class ScheduledWatchAndRestoreCheckReaderTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private const int OrganizationA = 1;
    private const int OrganizationB = 2;

    private readonly AppDbContext _db;

    public ScheduledWatchAndRestoreCheckReaderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private ServerActionPlanReader Reader(VulnerabilityWatchOptions? watch = null) =>
        new(new ServerActionScheduleRepository(_db), new FakeTimeProvider(new DateTimeOffset(Now)), Options.Create(watch ?? new VulnerabilityWatchOptions()));

    private Task<List<ScheduledServerActionDto>> ReadAsync(
        ServerActionScope scope, int organizationId = OrganizationA, VulnerabilityWatchOptions? watch = null, DateTime? from = null, DateTime? to = null) =>
        Reader(watch).ReadAsync(organizationId, scope, from ?? Day, to ?? (from ?? Day).AddDays(1), TestContext.Current.CancellationToken);

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

    private async Task<int> SeedPolicyAsync(
        string name, int projectId, int serverId, string? restoreCheckCron, string cron = "0 2 * * *", bool enabled = true)
    {
        var policy = new BackupPolicy
        {
            Name = name,
            ScheduleCron = cron,
            RestoreCheckCron = restoreCheckCron,
            ProjectId = projectId,
            ServerId = serverId,
            Enabled = enabled
        };
        _db.BackupPolicies.Add(policy);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return policy.Id;
    }

    private static ServerActionScope Projects(int[] readable, int[]? writable = null) =>
        new([], []) { ReadableProjectIds = readable, WritableProjectIds = writable ?? readable };

    private static ServerActionScope WithCveWatch() => new([], []) { SeesCveWatch = true };

    // --- Restore-check cadence ---

    [Fact]
    public async Task ARestoreCheckCadence_IsListedAsItsOwnReadOnlyAction_BesideItsBackup()
    {
        var project = await SeedProjectAsync("shop", OrganizationA);
        var server = await SeedServerAsync("srv-db", OrganizationA);
        var policy = await SeedPolicyAsync("orders", project, server, "0 5 * * 3", cron: "0 2 * * *");

        var actions = await ReadAsync(ServerActionScope.All);

        var backup = Assert.Single(actions, action => action.Kind == ServerActionKind.AppBackup);
        var check = Assert.Single(actions, action => action.Kind == ServerActionKind.RestoreCheck);
        Assert.Equal($"backup:{policy}", backup.Id);
        Assert.Equal($"restore:{policy}", check.Id);
        Assert.Equal("0 5 * * 3", check.CronExpression);
        Assert.Equal("orders", check.SourceName);
        Assert.Equal(server, check.ServerId);
        Assert.Equal("srv-db", check.ServerName);
        Assert.Equal(project, check.ProjectId);
        Assert.Equal(ScheduleFrequency.Weekly, check.Frequency);
        Assert.Equal(5, check.Hour);
        Assert.False(check.Movable);
        Assert.True(check.IsPlanned);
        Assert.Equal(ServerActionDefaults.RestoreCheckPlannedMinutes, check.DurationMinutes);
        Assert.Empty(check.Occurrences);
    }

    [Fact]
    public async Task ARestoreCheckThatFiresInTheWindow_HasItsOccurrencesWithThePlannedDuration()
    {
        var project = await SeedProjectAsync("shop", OrganizationA);
        var server = await SeedServerAsync("srv-db", OrganizationA);
        await SeedPolicyAsync("orders", project, server, "15 4 * * *");

        var check = Assert.Single(await ReadAsync(ServerActionScope.All), action => action.Kind == ServerActionKind.RestoreCheck);

        var run = Assert.Single(check.Occurrences);
        Assert.Equal(Day.AddHours(4).AddMinutes(15), run.StartUtc);
        Assert.Equal(run.StartUtc.AddMinutes(ServerActionDefaults.RestoreCheckPlannedMinutes), run.EndUtc);
        Assert.Equal(Day.AddHours(4).AddMinutes(15), check.NextRunUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task APolicyWithoutARestoreCheckCadence_ListsNoRestoreCheck(string? cadence)
    {
        var project = await SeedProjectAsync("shop", OrganizationA);
        var server = await SeedServerAsync("srv-db", OrganizationA);
        await SeedPolicyAsync("orders", project, server, cadence);

        var actions = await ReadAsync(ServerActionScope.All);

        Assert.DoesNotContain(actions, action => action.Kind == ServerActionKind.RestoreCheck);
        Assert.Single(actions, action => action.Kind == ServerActionKind.AppBackup);
    }

    [Fact]
    public async Task ADisabledPolicy_ListsNoRestoreCheck()
    {
        var project = await SeedProjectAsync("shop", OrganizationA);
        var server = await SeedServerAsync("srv-db", OrganizationA);
        await SeedPolicyAsync("orders", project, server, "0 5 * * 1", enabled: false);

        Assert.Empty(await ReadAsync(ServerActionScope.All));
    }

    [Fact]
    public async Task TheRestoreChecks_StayInTheirOrganization_AndFollowTheReadRightOnTheirProject()
    {
        var mine = await SeedProjectAsync("mine", OrganizationA);
        var hidden = await SeedProjectAsync("hidden", OrganizationA);
        var foreign = await SeedProjectAsync("foreign", OrganizationB);
        var server = await SeedServerAsync("srv", OrganizationA);
        var foreignServer = await SeedServerAsync("srv-foreign", OrganizationB);
        await SeedPolicyAsync("mine", mine, server, "0 5 * * 1");
        await SeedPolicyAsync("hidden", hidden, server, "0 5 * * 1");
        await SeedPolicyAsync("foreign", foreign, foreignServer, "0 5 * * 1");

        var visible = await ReadAsync(Projects([mine, foreign]));

        Assert.Equal(["mine", "mine"], visible.Select(action => action.SourceName));
        Assert.DoesNotContain(visible, action => action.SourceName is "hidden" or "foreign");
        Assert.Empty(await ReadAsync(ServerActionScope.None));
        Assert.Equal(["foreign", "foreign"], (await ReadAsync(ServerActionScope.All, OrganizationB)).Select(action => action.SourceName));
    }

    // --- CVE watch: one pass a day at a fixed local time (recette R-3) ---

    private static VulnerabilityWatchOptions Daily(string time = "04:00", string zone = "Europe/Paris", bool enabled = true) =>
        new() { Enabled = enabled, PassTime = time, PassTimeZone = zone };

    private async Task SeedPassAsync(
        string source, DateTime startedAt, TimeSpan duration, VulnerabilityFeedPassTrigger trigger = VulnerabilityFeedPassTrigger.Scheduled, bool succeeded = true)
    {
        _db.VulnerabilityFeedPasses.Add(new VulnerabilityFeedPass
        {
            Source = source,
            Trigger = trigger,
            StartedAt = startedAt,
            EndedAt = startedAt + duration,
            Succeeded = succeeded
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheCveWatch_IsNeverListed_ForAScopeThatDoesNotAskForIt()
    {
        Assert.Empty(await ReadAsync(ServerActionScope.All));
        Assert.Empty(await ReadAsync(ServerActionScope.None));
    }

    [Fact]
    public async Task TheCveWatch_IsListedMovable_WithOneDailyPassAt0400Paris_AndNeverOnePerHour()
    {
        var watch = Assert.Single(await ReadAsync(WithCveWatch()));

        Assert.Equal("cve:0", watch.Id);
        Assert.Equal(ServerActionKind.CveWatch, watch.Kind);
        Assert.Equal("04:00", watch.DailyTime);
        Assert.Equal("Europe/Paris", watch.DailyTimeZoneId);
        Assert.True(watch.Movable);
        Assert.Equal(string.Empty, watch.CronExpression);
        Assert.Equal(ScheduleFrequency.Custom, watch.Frequency);
        Assert.Null(watch.Hour);
        // January: Paris is UTC+1, so 04:00 is 03:00Z. The next one is the one of 2027-01-04, the window's only one.
        var expected = new DateTime(2027, 1, 4, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, watch.NextRunUtc);
        var occurrence = Assert.Single(watch.Occurrences);
        Assert.Equal(expected, occurrence.StartUtc);
        Assert.False(watch.OccurrencesTruncated);
    }

    [Fact]
    public async Task TheCveWatch_HasOnePassPerDayOverAWeek_NotTwentyFourPerDay()
    {
        var watch = Assert.Single(await ReadAsync(WithCveWatch(), to: Day.AddDays(7)));

        Assert.Equal(7, watch.Occurrences.Count);
        Assert.All(watch.Occurrences, occurrence => Assert.Equal(3, occurrence.StartUtc.Hour));
    }

    [Fact]
    public async Task TheCveWatch_IsListedWithItsTime_EvenWithNoPassEverRecorded()
    {
        // Nothing seeded: the schedule comes from the configuration, not from a recorded pass.
        var watch = Assert.Single(await ReadAsync(WithCveWatch()));

        Assert.NotNull(watch.NextRunUtc);
        Assert.Single(watch.Occurrences);
    }

    [Fact]
    public async Task TheCveWatch_KeepsItsLocalTimeAcrossTheSpringForwardChange_SoItsUtcInstantMoves()
    {
        // Europe/Paris springs forward on 2027-03-28 at 02:00: 04:00 is 03:00Z before, 02:00Z from that day.
        var from = new DateTime(2027, 3, 27, 0, 0, 0, DateTimeKind.Utc);

        var watch = Assert.Single(await ReadAsync(WithCveWatch(), from: from, to: from.AddDays(3)));

        Assert.Equal(
            [new DateTime(2027, 3, 27, 3, 0, 0, DateTimeKind.Utc), new DateTime(2027, 3, 28, 2, 0, 0, DateTimeKind.Utc), new DateTime(2027, 3, 29, 2, 0, 0, DateTimeKind.Utc)],
            watch.Occurrences.Select(occurrence => occurrence.StartUtc));
    }

    [Fact]
    public async Task TheCveWatch_FollowsTheTimeAndZoneTheServiceReallyWaitsFor()
    {
        var watch = Assert.Single(await ReadAsync(WithCveWatch(), watch: Daily("03:30", "UTC")));

        Assert.Equal("03:30", watch.DailyTime);
        Assert.Equal("UTC", watch.DailyTimeZoneId);
        Assert.Equal(Day.AddHours(3).AddMinutes(30), Assert.Single(watch.Occurrences).StartUtc);
    }

    [Fact]
    public async Task TheCveWatch_ListsTheTimeAnAdministratorMovedItTo_NotTheConfiguredOne()
    {
        var source = Substitute.For<IVulnerabilityPassScheduleSource>();
        source.GetAsync(Arg.Any<CancellationToken>()).Returns(new VulnerabilityPassSchedule(
            new TimeOnly(22, 30), TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris")));
        var reader = new ServerActionPlanReader(
            new ServerActionScheduleRepository(_db), new FakeTimeProvider(new DateTimeOffset(Now)), Options.Create(new VulnerabilityWatchOptions()),
            passSource: source);

        var watch = Assert.Single(await reader.ReadAsync(OrganizationA, WithCveWatch(), Day, Day.AddDays(1), TestContext.Current.CancellationToken));

        Assert.Equal("22:30", watch.DailyTime);
        Assert.Equal("Europe/Paris", watch.DailyTimeZoneId);
        // January: Paris is UTC+1, so 22:30 is 21:30Z.
        Assert.Equal(Day.AddHours(21).AddMinutes(30), Assert.Single(watch.Occurrences).StartUtc);
        Assert.True(watch.Movable);
    }

    [Fact]
    public async Task TheCveWatch_Disabled_IsNotListed() =>
        Assert.Empty(await ReadAsync(WithCveWatch(), watch: Daily(enabled: false)));

    [Fact]
    public async Task TheCveWatchDuration_IsTheLatestSuccessfulPassOfEachFeedSummed_OfflineImportsAndFailuresLeftOut()
    {
        await SeedPassAsync("kev", Now.AddDays(-2), TimeSpan.FromMinutes(40));
        await SeedPassAsync("kev", Now.AddDays(-1), TimeSpan.FromSeconds(90));
        await SeedPassAsync("osv:NuGet", Now.AddDays(-1), TimeSpan.FromSeconds(60));
        // An offline import of a huge dump and a failure that waited for its timeout are no duration of a scheduled pass.
        await SeedPassAsync("osv:npm", Now.AddHours(-3), TimeSpan.FromHours(20), VulnerabilityFeedPassTrigger.Import);
        await SeedPassAsync("eol:dotnet", Now.AddHours(-2), TimeSpan.FromMinutes(30), succeeded: false);

        var measured = Assert.Single(await ReadAsync(WithCveWatch()));

        Assert.Equal(3, measured.DurationMinutes);
        Assert.False(measured.IsPlanned);
        Assert.Equal(measured.Occurrences[0].StartUtc.AddMinutes(3), measured.Occurrences[0].EndUtc);
    }

    [Fact]
    public async Task TheCveWatchDuration_WithNoSuccessfulPass_IsThePlannedOne()
    {
        await SeedPassAsync("kev", Now.AddDays(-1), TimeSpan.FromMinutes(10), succeeded: false);

        var planned = Assert.Single(await ReadAsync(WithCveWatch()));

        Assert.True(planned.IsPlanned);
        Assert.Equal(ServerActionDefaults.CveWatchPlannedMinutes, planned.DurationMinutes);
    }

    [Fact]
    public async Task TheCveWatch_IsOneBlockOfTheSiteNotOfAnOrganization_ListedForAnyOrganizationAnAdminReads()
    {
        Assert.Single(await ReadAsync(WithCveWatch(), OrganizationA));
        Assert.Single(await ReadAsync(WithCveWatch(), OrganizationB));
    }

}
