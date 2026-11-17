// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;

namespace Aetheus.Back.Components.Analysis;

/// <summary>A decision whose expiry passed and that the job ended (recette R-24).</summary>
public sealed record AnalysisDecisionExpired(
    int DecisionId,
    int FindingId,
    int OrganizationId,
    int ProjectId,
    AnalysisFindingStatus Status,
    DateTime ExpiresAt);

/// <summary>
/// One run of the job that ends the decisions whose expiry has passed (recette R-24): the decision is revoked
/// (kept in the history), its finding is open again, and an audit entry says it expired. The CVE mail and the
/// daily recap read the finding's status, so a reopened finding counts as open again in the next one.
/// </summary>
internal sealed class AnalysisDecisionExpiry(
    IAnalysisRepository repository,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<AnalysisDecisionExpiry> logger)
{
    private const int Batch = 200;

    /// <summary>Ends every decision due at this moment and returns how many it ended.</summary>
    public async Task<int> ReopenExpiredAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var total = 0;
        while (true)
        {
            var ended = await repository.EndExpiredFindingDecisionsAsync(now, Batch, ct).ConfigureAwait(false);
            foreach (var item in ended)
                await audit.LogAsync("AnalysisFindingDecisionExpired", nameof(Data.Entities.AnalysisFinding), item.FindingId,
                    $"decision={item.DecisionId};status={item.Status};expired={item.ExpiresAt:O}", ct).ConfigureAwait(false);
            total += ended.Count;
            if (ended.Count < Batch) break;
        }

        if (total > 0)
            logger.LogInformation("Ended {Count} analysis finding decisions whose expiry passed", total);
        return total;
    }
}
