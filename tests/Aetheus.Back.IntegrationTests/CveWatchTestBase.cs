// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// What the vulnerability watch tests of lot 3 share: projects and inventories seeded in PostgreSQL, advisories
/// put through the real parser and feed repository, and one whole pass of the watch (matching, publication of
/// the findings, announcement) with the clock moved by hand. The only double is the notification service,
/// which records what leaves instead of delivering it.
/// </summary>
public abstract class CveWatchTestBase(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    protected const string Cve = "CVE-2099-0001";
    protected const string UpToFix = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.0.0"}]}]""";
    protected const string Unreadable = """[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"not a version"}]}]""";

    private static readonly OsvRecordParser Parser = new(new HashSet<string>(["NuGet"], StringComparer.Ordinal));

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
    private int _stamp;

    protected TimeProvider Clock => _clock;

    protected void AdvanceClock(TimeSpan by) => _clock.Advance(by);

    /// <summary>Every notification event the watch raised, in order: its type and the JSON it carried.</summary>
    protected List<(string EventType, string Json)> Sent { get; } = [];

    /// <summary>When set, sending the next event throws it, once.</summary>
    protected Exception? FailNextSend { get; set; }

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    protected INotificationService NewNotifications()
    {
        var notifications = Substitute.For<INotificationService>();
        notifications.SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (FailNextSend is { } failure)
            {
                FailNextSend = null;
                throw failure;
            }

            lock (Sent) Sent.Add((call.ArgAt<string>(0), JsonSerializer.Serialize(call.ArgAt<object>(1))));
            return Task.CompletedTask;
        });
        return notifications;
    }

    /// <summary>One whole pass, as the host wires it: matching of the changed advisories and inventories, then
    /// the handlers of the event it raises, the publication of the findings before the announcement.</summary>
    protected async Task PassAsync() => await PassAsync(advisoriesOnly: false);

    /// <summary>The same, stopping after the matching of the changed advisories: the event is raised once, not twice.</summary>
    protected async Task PassAsync(bool advisoriesOnly)
    {
        await using var db = NewContext();
        var watch = new CveWatchReportRepository(db);
        var publisher = new CveWatchReportPublisher(
            new AnalysisRepository(db), watch, new DbTransactionScope(db), Clock, Options.Create(new AnalysisPlatformOptions()));
        var handlers = new ServiceCollection()
            .AddSingleton<IDomainEventHandler<VulnerabilityMatchesChangedEvent>>(
                new CveWatchPublication(watch, publisher, NullLogger<CveWatchPublication>.Instance))
            .AddSingleton<IDomainEventHandler<VulnerabilityMatchesChangedEvent>>(
                new CveWatchAnnouncement(new CveWatchAnnouncementRepository(db), NewNotifications(), Clock, NullLogger<CveWatchAnnouncement>.Instance))
            .BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(handlers, Substitute.For<IBackgroundTaskQueue>(), NullLogger<DomainEventDispatcher>.Instance);
        var service = new VulnerabilityMatchService(
            new VulnerabilityMatchRepository(db), Clock, NullLogger<VulnerabilityMatchService>.Instance, dispatcher);
        await service.MatchChangedAdvisoriesAsync(Ct);
        if (!advisoriesOnly)
            await service.MatchChangedInventoriesAsync(null, Ct);
    }

    /// <summary>The nightly mail, as the scheduler runs it: the recap's hour and time zone decide whether it is due, the day is claimed,
    /// then the real digest reads the open matches and raises the event. Returns why it did or did not send.</summary>
    private protected async Task<DailyRecapScheduleOutcome> NightAsync()
    {
        await using var db = NewContext();
        var digest = new CveWatchDigest(
            new CveWatchAnnouncementRepository(db), new CveWatchHostAnnouncementRepository(db), NewNotifications(), Clock, NullLogger<CveWatchDigest>.Instance);
        return await new CveWatchDigestSchedule(new DailyRecapRepository(db), digest, Clock, NullLogger<CveWatchDigestSchedule>.Instance)
            .RunScheduledAsync(Ct);
    }

    /// <summary>Moves the recap's send time (HH:mm, Europe/Paris unless the zone is set too), which the nightly mail follows.</summary>
    protected async Task SetRecapHourAsync(string sendTime, string timeZone = "Europe/Paris")
    {
        await using var db = NewContext();
        await new DailyRecapRepository(db).SaveSettingRowsAsync(new Dictionary<string, string>
        {
            [DailyRecapSettingKeys.SendTime] = sendTime,
            [DailyRecapSettingKeys.TimeZone] = timeZone
        }, Clock.GetUtcNow().UtcDateTime, Ct);
    }

    protected async Task ApplyAsync(params string[] documents)
    {
        await using var db = NewContext();
        var repository = new VulnerabilityFeedRepository(db);
        var state = await repository.GetOrCreateStateAsync("osv:test", Ct);
        var records = new List<OsvAdvisoryRecord>();
        foreach (var document in documents)
            records.Add((await Parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(document)), Ct))!);
        await repository.ApplyBatchAsync(records, state, Ct);
    }

    /// <summary>Advisories already read into records, as the runtime importers hand them to the feed repository.</summary>
    protected async Task ApplyRecordsAsync(params OsvAdvisoryRecord[] records)
    {
        await using var db = NewContext();
        var repository = new VulnerabilityFeedRepository(db);
        await repository.ApplyBatchAsync(records, await repository.GetOrCreateStateAsync("runtime:test", Ct), Ct);
    }

    /// <param name="severity">The label the source publishes (<c>CRITICAL</c>, <c>HIGH</c>, <c>MODERATE</c>), or null
    /// for an advisory that publishes none: the feed then counts it High, flagged as not published.</param>
    protected static string Advisory(
        string id, string package, string ranges, string? severity = "HIGH", int revision = 0, bool withdrawn = false, string cve = Cve) =>
        "{\"id\":\"" + id + "\",\"aliases\":[\"" + cve + "\"],\"modified\":\"2026-10-01T10:00:" + (10 + revision).ToString("00") + "Z\""
        + (withdrawn ? ",\"withdrawn\":\"2026-10-01T11:00:00Z\"" : string.Empty)
        + (severity is null ? string.Empty : ",\"database_specific\":{\"severity\":\"" + severity + "\"}")
        + ",\"affected\":[{\"package\":{\"name\":\"" + package + "\",\"ecosystem\":\"NuGet\"},\"ranges\":" + ranges + "}]}";

    protected async Task<(int OrganizationId, int ProjectId, int InventoryId)> SeedProjectAsync(string name)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = name.ToLowerInvariant(), Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var inventory = new ProjectInventory
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            Origin = ProjectInventoryOrigin.Sbom,
            ReadAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            ChangedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)
        };
        db.ProjectInventories.Add(inventory);
        await db.SaveChangesAsync(Ct);
        return (organization.Id, project.Id, inventory.Id);
    }

    /// <summary>Adds an item the way a refresh does: a new row, and the inventory marked as changed.</summary>
    protected Task<int> AddItemAsync((int OrganizationId, int ProjectId, int InventoryId) owner, string package, string version) =>
        AddItemAsync(owner, "NuGet", package, version);

    /// <summary>The same for any ecosystem of the inventory, <c>runtime</c> and <c>docker</c> included.</summary>
    protected async Task<int> AddItemAsync(
        (int OrganizationId, int ProjectId, int InventoryId) owner, string ecosystem, string package, string version)
    {
        await using var db = NewContext();
        var item = new ProjectInventoryItem
        {
            OrganizationId = owner.OrganizationId,
            ProjectId = owner.ProjectId,
            ProjectInventoryId = owner.InventoryId,
            Ecosystem = ecosystem,
            PackageName = package,
            Version = version,
            PackageUrl = $"pkg:{ecosystem}/{package}@{version}",
            Kind = ecosystem switch
            {
                "runtime" => ProjectInventoryItemKind.Runtime,
                "docker" => ProjectInventoryItemKind.BaseImage,
                _ => ProjectInventoryItemKind.Dependency
            }
        };
        db.ProjectInventoryItems.Add(item);
        (await db.ProjectInventories.SingleAsync(row => row.Id == owner.InventoryId, Ct)).ChangedAt =
            new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc).AddMinutes(++_stamp);
        await db.SaveChangesAsync(Ct);
        return item.Id;
    }

    /// <summary>A version bump as a refresh stores it: the old item goes, a new one comes.</summary>
    protected async Task<int> MoveItemAsync((int OrganizationId, int ProjectId, int InventoryId) owner, int oldItem, string package, string version)
    {
        await using (var db = NewContext())
        {
            db.ProjectInventoryItems.Remove(await db.ProjectInventoryItems.SingleAsync(row => row.Id == oldItem, Ct));
            await db.SaveChangesAsync(Ct);
        }

        return await AddItemAsync(owner, package, version);
    }

    protected async Task<List<VulnerabilityMatch>> MatchesAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.VulnerabilityMatches.AsNoTracking().Where(match => match.ProjectId == projectId).OrderBy(match => match.Id).ToListAsync(Ct);
    }
}
