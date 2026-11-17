// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Moves a remediation to its next state and writes that move to the platform's audit: there is no transition without its line.</summary>
internal sealed class CveRemediationLedger(
    CveRemediationRepository repository, IAuditService audit, TimeProvider timeProvider, ILogger<CveRemediationLedger> logger)
{
    private const string EntityType = nameof(VulnerabilityRemediation);
    private const int MaxReasonLength = 2048;

    public async Task MoveAsync(VulnerabilityRemediation remediation, VulnerabilityRemediationStatus status, string detail, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        remediation.Status = status;
        remediation.UpdatedAt = now;
        if (status is VulnerabilityRemediationStatus.Merged or VulnerabilityRemediationStatus.Abandoned)
            remediation.EndedAt = now;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        await RecordAsync(remediation, $"Remediation{status}", detail, ct).ConfigureAwait(false);
    }

    /// <summary>Who ends the run, when the event and a read both try. Not bound to any request: once taken, the end of the attempt has to follow.</summary>
    public Task<bool> TryClaimRunEndAsync(VulnerabilityRemediation remediation) =>
        repository.TryClaimRunEndAsync(remediation, timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>The end of a failed attempt. It takes no token: a caller that left never keeps an attempt active, which would block the next one.</summary>
    public async Task FailAsync(VulnerabilityRemediation remediation, string detail)
    {
        var reason = detail.Length <= MaxReasonLength ? detail : detail[..MaxReasonLength];
        await repository.WriteFailureAsync(remediation, reason, timeProvider.GetUtcNow().UtcDateTime).ConfigureAwait(false);
        await RecordAsync(remediation, $"Remediation{VulnerabilityRemediationStatus.Failed}", detail, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What a person reads of an exception. A refusal of the platform says why itself; anything else stays in the log, and the
    /// reason names only its kind, as it goes back to the caller.</summary>
    public string ReasonOf(VulnerabilityRemediation remediation, Exception exception)
    {
        if (exception is InvalidOperationException or BadRequestException or ConflictException) return exception.Message;
        logger.LogError(exception, "Remediation {RemediationId} stopped on an unexpected error", remediation.Id);
        return exception is OperationCanceledException
            ? "The attempt was interrupted before its end."
            : $"The attempt stopped on an unexpected error ({exception.GetType().Name}).";
    }

    public Task RecordAsync(VulnerabilityRemediation remediation, string action, string detail, CancellationToken ct) =>
        audit.LogAsync(action, EntityType, remediation.Id, $"match {remediation.VulnerabilityMatchId}, for {remediation.RequestedByUsername}: {detail}", ct);

    /// <summary>A refusal changes no row, and is still an action someone took.</summary>
    public Task RecordRefusalAsync(long matchId, CveRemediationFallbackDto fallback, CancellationToken ct) =>
        audit.LogAsync("RemediationRefused", EntityType, null, $"match {matchId}: {fallback.Reason}{(fallback.Detail.Length == 0 ? string.Empty : $": {fallback.Detail}")}", ct);
}
