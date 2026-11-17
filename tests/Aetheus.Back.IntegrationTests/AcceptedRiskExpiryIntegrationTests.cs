// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-24 on PostgreSQL: an accepted risk of the CVE watch carries an expiry, and the job reopens only the
/// acceptances whose expiry has passed (finding open again, decision revoked and kept, audit entry), after which the
/// nightly CVE mail lists the match as open again. The real watch pass, the real decision service and the real job
/// run; the delivery of the mail event is the recorded double of <see cref="CveWatchTestBase"/>, and the audit service
/// is a double that records what the job writes (the audit chain is proved on its own).
/// </summary>
public sealed class AcceptedRiskExpiryIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    private async Task<int> FindingOfAsync(string package)
    {
        await using var db = NewContext();
        return await db.AnalysisFindings.Where(finding => finding.Occurrences.Any(occurrence => occurrence.Symbol == package))
            .Select(finding => finding.Id).SingleAsync(Ct);
    }

    private async Task AcceptAsync(int findingId, int days)
    {
        await using var db = NewContext();
        await new AnalysisFindingDecisionService(new AnalysisRepository(db), Clock, _audit).CreateFindingDecisionAsync(
            findingId,
            new CreateAnalysisFindingDecisionRequest { Status = AnalysisFindingStatus.Accepted, Reason = "accepted until the vendor ships a fix", AcceptanceDays = days },
            "tester", Ct);
    }

    /// <summary>An acceptance from before R-24: no expiry, as the column has always allowed.</summary>
    private async Task AcceptForeverAsync(int findingId, (int OrganizationId, int ProjectId, int InventoryId) owner)
    {
        await using var db = NewContext();
        (await db.AnalysisFindings.SingleAsync(finding => finding.Id == findingId, Ct)).Status = AnalysisFindingStatus.Accepted;
        db.AnalysisFindingDecisions.Add(new AnalysisFindingDecision
        {
            OrganizationId = owner.OrganizationId,
            ProjectId = owner.ProjectId,
            AnalysisFindingId = findingId,
            Status = AnalysisFindingStatus.Accepted,
            Reason = "accepted before the expiry existed",
            CreatedByUsername = "tester",
            CreatedAt = Clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> RunJobAsync()
    {
        await using var db = NewContext();
        return await new AnalysisDecisionExpiry(new AnalysisRepository(db), _audit, Clock, NullLogger<AnalysisDecisionExpiry>.Instance)
            .ReopenExpiredAsync(Ct);
    }

    private async Task<AnalysisFindingStatus> StatusAsync(int findingId)
    {
        await using var db = NewContext();
        return (await db.AnalysisFindings.AsNoTracking().SingleAsync(finding => finding.Id == findingId, Ct)).Status;
    }

    private async Task<List<AnalysisFindingDecision>> DecisionsAsync(int findingId)
    {
        await using var db = NewContext();
        return await db.AnalysisFindingDecisions.AsNoTracking().Where(decision => decision.AnalysisFindingId == findingId).OrderBy(decision => decision.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task TheJob_ReopensOnlyTheAcceptancesWhoseExpiryHasPassed_AndTheNextNightlyMailListsThemAgain()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "libshort", "1.0.0");
        await AddItemAsync(owner, "libextended", "1.0.0");
        await AddItemAsync(owner, "libold", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-1", "LibShort", UpToFix, cve: "CVE-2099-0001"),
            Advisory("GHSA-2", "LibExtended", UpToFix, cve: "CVE-2099-0002"),
            Advisory("GHSA-3", "LibOld", UpToFix, cve: "CVE-2099-0003"));
        await SetRecapHourAsync("08:00");
        await PassAsync();
        var shortOne = await FindingOfAsync("libshort");
        var longOne = await FindingOfAsync("libextended");
        var legacy = await FindingOfAsync("libold");
        await AcceptAsync(shortOne, 30);
        await AcceptAsync(longOne, 180);
        await AcceptForeverAsync(legacy, owner);
        var acceptedAt = Clock.GetUtcNow().UtcDateTime;

        // Day 29: nothing is due.
        AdvanceClock(TimeSpan.FromDays(29));
        Assert.Equal(0, await RunJobAsync());
        Assert.Equal(AnalysisFindingStatus.Accepted, await StatusAsync(shortOne));

        // Day 31: the 30-day acceptance ended, the 180-day one and the one with no expiry stand.
        AdvanceClock(TimeSpan.FromDays(2));
        Assert.Equal(1, await RunJobAsync());
        Assert.Equal(AnalysisFindingStatus.Open, await StatusAsync(shortOne));
        Assert.Equal(AnalysisFindingStatus.Accepted, await StatusAsync(longOne));
        Assert.Equal(AnalysisFindingStatus.Accepted, await StatusAsync(legacy));
        var ended = Assert.Single(await DecisionsAsync(shortOne));
        Assert.Equal(acceptedAt.AddDays(30), ended.ExpiresAt);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, ended.RevokedAt);
        Assert.All(await DecisionsAsync(longOne), decision => Assert.Null(decision.RevokedAt));
        Assert.All(await DecisionsAsync(legacy), decision => Assert.Null(decision.RevokedAt));
        await _audit.Received(1).LogAsync(
            "AnalysisFindingDecisionExpired", nameof(AnalysisFinding), shortOne, Arg.Is<string?>(details => details!.Contains("status=Accepted")), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(
            "AnalysisFindingDecisionExpired", nameof(AnalysisFinding), longOne, Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // A second run changes and says nothing more.
        Assert.Equal(0, await RunJobAsync());
        await _audit.Received(1).LogAsync("AnalysisFindingDecisionExpired", nameof(AnalysisFinding), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // The nightly mail lists the reopened match, and only it: the others are still accepted.
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        var mail = Assert.Single(Sent, sent => sent.EventType == NotificationEventTypes.CveDetected);
        Assert.Contains("libshort", mail.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("libextended", mail.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("libold", mail.Json, StringComparison.Ordinal);

        // Day 181: the 180-day acceptance ends too; the one with no expiry never does.
        AdvanceClock(TimeSpan.FromDays(150));
        Assert.Equal(1, await RunJobAsync());
        Assert.Equal(AnalysisFindingStatus.Open, await StatusAsync(longOne));
        Assert.Equal(AnalysisFindingStatus.Accepted, await StatusAsync(legacy));
    }

    [Fact]
    public async Task TheJob_LeavesAFindingAScanFoundFixed_Fixed()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        var finding = await FindingOfAsync("lib");
        await AcceptAsync(finding, 30);
        await MoveItemAsync(owner, item, "lib", "2.0.0");
        await PassAsync();
        Assert.Equal(AnalysisFindingStatus.Fixed, await StatusAsync(finding));

        AdvanceClock(TimeSpan.FromDays(31));
        await RunJobAsync();

        Assert.Equal(AnalysisFindingStatus.Fixed, await StatusAsync(finding));
        Assert.NotNull(Assert.Single(await DecisionsAsync(finding)).RevokedAt);
    }
}
