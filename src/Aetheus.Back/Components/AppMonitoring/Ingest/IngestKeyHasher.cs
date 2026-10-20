// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.AppMonitoring.Ingest;

/// <summary>
/// Generates and hashes OTLP ingestion keys. Uses the same keyed HMAC-SHA256 primitive as every other
/// DB-resolved token in the codebase (ServerToken / RegistrationToken / RefreshToken) so the plaintext
/// is never stored - only <see cref="Data.Entities.MonitoredApp.IngestKeyHash"/>.
/// </summary>
public sealed class IngestKeyHasher(IOptions<JwtOptions> jwtOptions)
{
    private readonly string _signingKey = jwtOptions.Value.SigningKey;

    public string Generate() => AuthTokenHelper.GenerateSecureToken();

    public string Hash(string key) => AuthTokenHelper.HashToken(_signingKey, key);

    /// <summary>Derives deployment ingestion keys deterministically so all backend instances retain
    /// the same key within a run. A distinct purpose label separates derivation from the
    /// access-token use of the same secret.</summary>
    public string DeriveForRun(int appId, int pipelineRunId) =>
        AuthTokenHelper.HashToken(
            _signingKey,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"aetheus:ingest-key:deploy-run:v1:{appId}:{pipelineRunId}"));
}
