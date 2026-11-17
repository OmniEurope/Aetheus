// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Generates and hashes OTLP ingestion keys. Uses the keyed HMAC-SHA256 hashing of every other DB-resolved
/// token (ServerToken / RegistrationToken / RefreshToken) under its own derived key (R-53), so the plaintext
/// is never stored - only <see cref="Data.Entities.MonitoredApp.IngestKeyHash"/>.
/// </summary>
public sealed class IngestKeyHasher(TokenHashKeys keys)
{
    public string Generate() => AuthTokenHelper.GenerateSecureToken();

    public string Hash(string key) => keys.Hash(TokenHashPurpose.IngestKey, key);

    /// <summary>The hashes a stored ingestion key may carry: <see cref="Hash"/> first, then the legacy one.</summary>
    /// <remarks>R-53 legacy fallback. Before R-53 this class read the signing key through
    /// <c>IOptions&lt;JwtOptions&gt;</c>, which nothing configures: the options instance was a default one
    /// whose key is empty, so every stored ingestion key hash was an HMAC under an empty key. That exact
    /// hash is the legacy candidate. Remove it once every monitored application has been redeployed (a
    /// deployment rotates the key and stores the new hash) and the overlap window
    /// (<c>AppMonitoring:IngestKeyOverlapDays</c>) has elapsed, or at the latest after the maximum ingestion
    /// key lifetime.</remarks>
    public string[] HashCandidates(string key) =>
        [Hash(key), Convert.ToBase64String(HMACSHA256.HashData(Array.Empty<byte>(), Encoding.UTF8.GetBytes(key)))];

    /// <summary>Derives deployment ingestion keys deterministically so all backend instances retain
    /// the same key within a run. The derivation has its own key (R-53), separate from the hashing key.</summary>
    public string DeriveForRun(int appId, int pipelineRunId) =>
        keys.Hash(
            TokenHashPurpose.IngestKeyDerivation,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"aetheus:ingest-key:deploy-run:v1:{appId}:{pipelineRunId}"));
}
