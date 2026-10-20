// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>An expiring exception or decision is notified once: recording the notice stamps the first send
/// only, a second recording keeps that first stamp, an unknown id changes nothing, and an unknown kind is
/// refused. These cases run on the non-relational provider; the relational bulk update is exercised by the
/// integration suite.</summary>
public sealed class AnalysisGovernanceExpirationNoticeTests : IDisposable
{
    private static readonly DateTime FirstSend = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondSend = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db;
    private readonly AnalysisGovernanceRepository _repository;

    public AnalysisGovernanceExpirationNoticeTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, TimeProvider.System);
        _repository = new AnalysisGovernanceRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int ExceptionId, int DecisionId)> SeedAsync()
    {
        var policyException = new AnalysisPolicyException { OrganizationId = 1, ProjectId = 1, Reason = "r", ExpiresAt = FirstSend.AddDays(3) };
        var decision = new AnalysisFindingDecision { OrganizationId = 1, ProjectId = 1, AnalysisFindingId = 1, Reason = "r", ExpiresAt = FirstSend.AddDays(3) };
        _db.AddRange(policyException, decision);
        await _db.SaveChangesAsync(Ct);
        return (policyException.Id, decision.Id);
    }

    [Fact]
    public async Task AnException_IsStampedOnTheFirstNotice_AndKeepsThatStamp()
    {
        var (exceptionId, decisionId) = await SeedAsync();

        await _repository.MarkExpirationNotificationSentAsync("exception", exceptionId, FirstSend, Ct);
        await _repository.MarkExpirationNotificationSentAsync("exception", exceptionId, SecondSend, Ct);

        Assert.Equal(FirstSend, (await _db.AnalysisPolicyExceptions.SingleAsync(Ct)).ExpirationNotificationSentAt);
        Assert.Null((await _db.AnalysisFindingDecisions.SingleAsync(Ct)).ExpirationNotificationSentAt);
        Assert.NotEqual(0, decisionId);
    }

    [Fact]
    public async Task ADecision_IsStampedOnTheFirstNotice_AndKeepsThatStamp()
    {
        var (exceptionId, decisionId) = await SeedAsync();

        await _repository.MarkExpirationNotificationSentAsync("decision", decisionId, FirstSend, Ct);
        await _repository.MarkExpirationNotificationSentAsync("decision", decisionId, SecondSend, Ct);

        Assert.Equal(FirstSend, (await _db.AnalysisFindingDecisions.SingleAsync(Ct)).ExpirationNotificationSentAt);
        Assert.Null((await _db.AnalysisPolicyExceptions.SingleAsync(Ct)).ExpirationNotificationSentAt);
        Assert.NotEqual(0, exceptionId);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("decision")]
    public async Task AnUnknownId_ChangesNothing(string kind)
    {
        await SeedAsync();

        await _repository.MarkExpirationNotificationSentAsync(kind, 9999, FirstSend, Ct);

        Assert.Null((await _db.AnalysisPolicyExceptions.SingleAsync(Ct)).ExpirationNotificationSentAt);
        Assert.Null((await _db.AnalysisFindingDecisions.SingleAsync(Ct)).ExpirationNotificationSentAt);
    }

    [Fact]
    public async Task AnUnknownKind_IsRefused()
    {
        var (exceptionId, _) = await SeedAsync();

        var refusal = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.MarkExpirationNotificationSentAsync("waiver", exceptionId, FirstSend, Ct));

        Assert.Equal("kind", refusal.ParamName);
    }
}
