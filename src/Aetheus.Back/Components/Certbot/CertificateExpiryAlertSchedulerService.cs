// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Certbot;

/// <summary>
/// Recette R-69: checks the certificates against the configured alert lead times. Only the leader
/// instance (the live blue-green colour) runs it, and the alert of a lead time is marked before it is
/// sent, so it leaves once per certificate and expiry date.
/// </summary>
public sealed class CertificateExpiryAlertSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<CertificateExpiryAlertSchedulerService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null)
    : PeriodicBackgroundService(CheckInterval, leaderLease, "aetheus:certificate-expiry-alerts", timeProvider)
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    protected override Task ExecuteIterationAsync(CancellationToken ct) => CheckAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during the certificate expiry alert check");

    internal async Task<int> CheckAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CertificateExpiryAlertService>().RunAsync(ct).ConfigureAwait(false);
    }
}
