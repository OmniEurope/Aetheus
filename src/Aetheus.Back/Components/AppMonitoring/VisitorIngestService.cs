// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class VisitorIngestService(
    IAppVisitorRepository visitors,
    TimeProvider timeProvider) : IVisitorIngestService
{
    public async Task RecordAsync(int appId, string visitorId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var dayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var scopedIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"{appId}:{dayUtc:yyyy-MM-dd}:{visitorId}");
        var storedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopedIdentity)))
            .ToLowerInvariant();
        await visitors.RecordAsync(appId, dayUtc, storedHash,
            now.UtcDateTime, ct).ConfigureAwait(false);
    }
}
