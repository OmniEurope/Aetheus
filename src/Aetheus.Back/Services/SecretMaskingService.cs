// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Services;

/// <summary>
/// Redacts a run's secrets out of anything shown to a human.
///
/// KNOWN LIMIT, stated rather than implied: runtime-registered secrets (those minted during the run,
/// like the deployment bootstrap password or the ephemeral clone token) live ONLY in this process's
/// <see cref="IMemoryCache"/>, with a 30-minute sliding expiration. They are therefore lost when the
/// backend restarts, and unknown to any other instance. Two consequences, both fail-OPEN:
/// a log line masked by the instance that minted the secret can come back unmasked from another
/// instance or after a restart. Vault secrets do not have this problem: they are re-read from the
/// database on a cache miss.
///
/// This is a deliberate single-instance assumption, not an oversight. Removing it means persisting
/// run secrets encrypted (an architecture decision with its own key-lifetime questions), which is why
/// it is recorded here instead of being silently half-fixed.
/// </summary>
public sealed class SecretMaskingService(ISecretMaskingRepository secretRepo, IEncryptionService encryption, IMemoryCache cache) : ISecretMaskingService
{
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(30);

    public async Task<string> MaskAsync(string message, int? pipelineRunId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(message) || pipelineRunId is null)
            return message;

        var secrets = await GetSecretsForRunAsync(pipelineRunId.Value, ct).ConfigureAwait(false);
        var runtimeSecrets = GetRuntimeSecrets(pipelineRunId.Value);
        if (secrets.Count == 0 && runtimeSecrets.Count == 0)
            return message;

        // Mask runtime-registered secrets (e.g. the ephemeral clone token) as well as vault secrets;
        // both are already filtered (length >= 4) and sorted longest-first by their loaders.
        var result = message;
        foreach (var secret in runtimeSecrets)
            result = result.Replace(secret, "***", StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        return result;
    }

    public void EvictCache(int pipelineRunId)
    {
        cache.Remove(CacheKey(pipelineRunId));
        cache.Remove(RuntimeCacheKey(pipelineRunId));
    }

    public void RegisterRuntimeSecret(int pipelineRunId, string secret)
    {
        // F-23 parity: don't register tiny values that would mangle every log line.
        if (string.IsNullOrEmpty(secret) || secret.Length < 4)
            return;

        var key = RuntimeCacheKey(pipelineRunId);
        var list = cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = SlidingExpiration;
            return new List<string>();
        })!;
        if (!list.Contains(secret))
        {
            list.Add(secret);
            // Longest-first so a secret that is a substring of another is masked correctly.
            list.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public IReadOnlyCollection<string> GetRuntimeSecretValues(int pipelineRunId) =>
        GetRuntimeSecrets(pipelineRunId);

    private List<string> GetRuntimeSecrets(int pipelineRunId) =>
        cache.TryGetValue<List<string>>(RuntimeCacheKey(pipelineRunId), out var list) && list is not null
            ? list
            : [];

    private static string CacheKey(int pipelineRunId) => $"secret-masking:{pipelineRunId}";

    private static string RuntimeCacheKey(int pipelineRunId) => $"secret-masking-runtime:{pipelineRunId}";

    private async Task<List<string>> GetSecretsForRunAsync(int pipelineRunId, CancellationToken ct)
    {
        var key = CacheKey(pipelineRunId);

        if (cache.TryGetValue<List<string>>(key, out var cached) && cached is not null)
            return cached;

        // Caches empty secret sets as well as populated ones to avoid repeating vault lookups for
        // every command, failure reason and output variable.
        var pipelineId = await secretRepo.GetPipelineIdForRunAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (pipelineId is null)
            return CacheSecrets(key, []);

        var (yamlDefinition, projectId) = await secretRepo.GetPipelineYamlAndProjectAsync(pipelineId.Value, ct).ConfigureAwait(false);
        if (yamlDefinition is null)
            return CacheSecrets(key, []);

        var vaultNames = YamlParsingHelper.ParseVaultNames(yamlDefinition);
        if (vaultNames.Count == 0)
            return CacheSecrets(key, []);

        var encryptedValues = await secretRepo.GetEncryptedSecretsAsync(vaultNames, projectId, ct).ConfigureAwait(false);

        var decrypted = new List<string>(encryptedValues.Count);
        foreach (var enc in encryptedValues)
        {
            var plain = encryption.DecryptValue(enc);
            // F-23: refuse to mask very short secret values to avoid sweeping false-positives
            // (e.g. a 2-char secret would mangle every log line).
            if (!string.IsNullOrEmpty(plain) && plain.Length >= 4)
                decrypted.Add(plain);
        }

        // Sort longest-first for correct masking
        decrypted.Sort((a, b) => b.Length.CompareTo(a.Length));

        return CacheSecrets(key, decrypted);
    }

    private List<string> CacheSecrets(string key, List<string> secrets)
    {
        cache.Set(key, secrets, new MemoryCacheEntryOptions { SlidingExpiration = SlidingExpiration });
        return secrets;
    }
}
