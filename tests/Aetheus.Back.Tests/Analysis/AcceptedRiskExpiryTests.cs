// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-24: accepting the risk of a CVE-watch finding takes a length (30, 90 or 180 days), the server
/// derives the expiry from it and refuses any other value. Findings of other scanners keep their free date.
/// </summary>
public sealed class AcceptedRiskExpiryTests : IDisposable
{
    private static readonly DateTime NowUtc = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(NowUtc));
    private readonly AppDbContext _db;
    private readonly AnalysisFindingDecisionService _decisions;

    public AcceptedRiskExpiryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options, _clock);
        _decisions = new AnalysisFindingDecisionService(new AnalysisRepository(_db), _clock, Substitute.For<IAuditService>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task AddFindingAsync(int id, string scannerKey)
    {
        _db.AnalysisFindings.Add(new AnalysisFinding
        {
            Id = id,
            OrganizationId = 7,
            ProjectId = 10,
            Fingerprint = $"fp-{id}",
            RuleId = "CVE-2026-0001",
            Category = AnalysisCategory.Dependencies,
            Severity = AnalysisSeverity.High,
            Status = AnalysisFindingStatus.Open,
            Title = "vulnerable package",
            Message = "a package is affected",
            Occurrences =
            [
                new AnalysisFindingOccurrence { AnalysisReportId = 1, ScannerKey = scannerKey, ToolName = scannerKey, LocationHash = $"loc-{id}" }
            ]
        });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private static CreateAnalysisFindingDecisionRequest Accept(int? days, DateTime? expiresAt = null) => new()
    {
        Status = AnalysisFindingStatus.Accepted,
        Reason = "accepted until the vendor ships a fix",
        AcceptanceDays = days,
        ExpiresAt = expiresAt
    };

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task Accepting_ACveWatchFinding_StoresTheExpiryOfTheChosenLength(int days)
    {
        await AddFindingAsync(1, AcceptedRiskDuration.CveWatchScannerKey);

        var decision = await _decisions.CreateFindingDecisionAsync(1, Accept(days), "alice", Ct);

        Assert.Equal(NowUtc.AddDays(days), decision.ExpiresAt);
        var stored = await _db.AnalysisFindingDecisions.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(NowUtc.AddDays(days), stored.ExpiresAt);
        Assert.Equal(AnalysisFindingStatus.Accepted, (await _db.AnalysisFindings.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(91)]
    [InlineData(365)]
    [InlineData(-30)]
    public async Task Accepting_ACveWatchFinding_RefusesAMissingOrUnknownLength(int? days)
    {
        await AddFindingAsync(1, AcceptedRiskDuration.CveWatchScannerKey);

        await Assert.ThrowsAsync<BadRequestException>(() => _decisions.CreateFindingDecisionAsync(1, Accept(days), "alice", Ct));

        Assert.Empty(await _db.AnalysisFindingDecisions.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(AnalysisFindingStatus.Open, (await _db.AnalysisFindings.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Accepting_ACveWatchFinding_RefusesAFreeDateInsteadOfALength()
    {
        await AddFindingAsync(1, AcceptedRiskDuration.CveWatchScannerKey);

        await Assert.ThrowsAsync<BadRequestException>(
            () => _decisions.CreateFindingDecisionAsync(1, Accept(null, NowUtc.AddDays(10)), "alice", Ct));
        await Assert.ThrowsAsync<BadRequestException>(
            () => _decisions.CreateFindingDecisionAsync(1, Accept(90, NowUtc.AddDays(10)), "alice", Ct));
    }

    [Fact]
    public async Task OtherScanners_KeepTheFreeOptionalExpiry_AndRefuseAnUnknownLength()
    {
        await AddFindingAsync(2, "opengrep");

        var free = await _decisions.CreateFindingDecisionAsync(2, Accept(null), "alice", Ct);
        Assert.Null(free.ExpiresAt);

        await Assert.ThrowsAsync<BadRequestException>(() => _decisions.CreateFindingDecisionAsync(2, Accept(45), "alice", Ct));

        var withLength = await _decisions.CreateFindingDecisionAsync(2, Accept(30), "alice", Ct);
        Assert.Equal(NowUtc.AddDays(30), withLength.ExpiresAt);
    }

    [Theory]
    [InlineData(AnalysisFindingStatus.FalsePositive)]
    [InlineData(AnalysisFindingStatus.Mitigated)]
    public async Task OnlyAnAcceptanceTakesALength(AnalysisFindingStatus status)
    {
        await AddFindingAsync(1, AcceptedRiskDuration.CveWatchScannerKey);

        await Assert.ThrowsAsync<BadRequestException>(() => _decisions.CreateFindingDecisionAsync(
            1, new CreateAnalysisFindingDecisionRequest { Status = status, Reason = "not an acceptance", AcceptanceDays = 30 }, "alice", Ct));

        var decision = await _decisions.CreateFindingDecisionAsync(
            1, new CreateAnalysisFindingDecisionRequest { Status = status, Reason = "not an acceptance" }, "alice", Ct);
        Assert.Null(decision.ExpiresAt);
    }

    public void Dispose() => _db.Dispose();
}
