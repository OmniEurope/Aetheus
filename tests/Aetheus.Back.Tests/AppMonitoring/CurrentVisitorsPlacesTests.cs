// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// Recette R1-68 (decision of 2026-10-13): one person signed in at two places counts as two sessions in
/// « Current visitors », and two accounts are two sessions too. The person is still one visitor in the
/// daily, weekly and monthly uniques. From pseudonymized event to the count the Supervision shows.
/// </summary>
public sealed class CurrentVisitorsPlacesTests : IDisposable
{
    private const string Secret = "unit-test-secret-never-sent-to-browser";
    private const string Home = "203.0.113.24";
    private const string Office = "198.51.100.7";
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneAccountAtTwoPlaces_AndASecondAccount_AreThreeCurrentVisitors_ButTwoPeople()
    {
        var app = new MonitoredApp { Id = 7, AnalyticsSiteId = "site-7", AnalyticsEnabled = true, AnalyticsPseudonymKeyVersion = 1 };
        AppWebAnalyticsIngestEvent[] events =
        [
            Event(app, "visitor-a", Home, Now.AddMinutes(-2)),
            // A second tab of the same account at the same place is the same session.
            Event(app, "visitor-a", Home, Now.AddMinutes(-1)),
            Event(app, "visitor-a", Office, Now.AddMinutes(-1)),
            Event(app, "visitor-b", Home, Now.AddMinutes(-1)),
        ];

        await new AppWebAnalyticsRepository(_db).RecordAsync(app.Id, events, 30, Now, Ct);
        var counts = await new AppMonitoringRepository(_db).GetActiveVisitorCountsAsync([app.Id], Now.AddMinutes(-5), Ct);

        Assert.Equal(3, counts[app.Id]);
        Assert.Equal(2, events.Select(e => e.DailyPseudonym).Distinct().Count());
        Assert.Equal(2, events.Select(e => e.AuthenticatedPseudonym).Distinct().Count());
    }

    [Fact]
    public void TheSessionOfAnAccount_DependsOnThePlace_NotTheAccountAlone()
    {
        var app = new MonitoredApp { Id = 7, AnalyticsSiteId = "site-7", AnalyticsPseudonymKeyVersion = 1 };
        var home = Event(app, "visitor-a", Home, Now);
        var homeLater = Event(app, "visitor-a", Home, Now.AddDays(3));
        var office = Event(app, "visitor-a", Office, Now);

        Assert.Equal(home.SessionPseudonym, homeLater.SessionPseudonym);
        Assert.NotEqual(home.SessionPseudonym, office.SessionPseudonym);
        Assert.Equal(home.AuthenticatedPseudonym, office.AuthenticatedPseudonym);
        Assert.Equal(home.MonthlyPseudonym, office.MonthlyPseudonym);
        Assert.DoesNotContain(Home, home.SessionPseudonym, StringComparison.Ordinal);
    }

    public void Dispose() => _db.Dispose();

    private static AppWebAnalyticsIngestEvent Event(MonitoredApp app, string account, string address, DateTime at) =>
        PublicWebAnalyticsPseudonymizer.Create(app, Secret, new PublicWebAnalyticsEventRequest
        {
            SchemaVersion = 1,
            EventId = Guid.NewGuid(),
            OccurredAtUtc = at,
            Kind = "page_view",
            Route = "/",
            AuthenticatedUserId = account
        }, address);
}
