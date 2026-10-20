// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// Recette R3-7: reads one API profile's model list at its provider, with the profile's own key and
/// base URL, and records the outcome on the tracked entity (the caller saves). Shared by the nightly
/// pass (<see cref="AiModelListRefreshService"/>) and the "read now" action of the profile dialog, so
/// both read, store and fail the same way. A failure keeps the previous list and records the reason,
/// API key masked; the key is never logged.
/// </summary>
public sealed class AiModelListReader(IHttpClientFactory httpClientFactory, ILogger<AiModelListReader> logger)
{
    /// <summary>Backend client of the read: SSRF-guarded at connect time, no redirects.</summary>
    public const string HttpClientName = "ai-provider-models";

    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Reads the list of <paramref name="profile"/> at <paramref name="now"/>; true when a list
    /// was read and stored.</summary>
    public async Task<bool> ReadIntoAsync(
        AiRunnerProfile profile, AiProvider provider, IEncryptionService encryption, DateTime now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(encryption);
        profile.ModelListCheckedAt = now;
        string? apiKey = null;
        AiModelListOutcome outcome;
        try
        {
            apiKey = encryption.DecryptValue(profile.ApiKeyEncrypted);
            var baseUrlText = AiProfileRuntime.BaseUrl(profile);
            outcome = AiProviderCatalog.IsValidBaseUrl(baseUrlText, out var baseUrl)
                ? await new AiProviderClient(httpClientFactory.CreateClient(HttpClientName))
                    .ListModelsAsync(new AiProviderSettings(provider, baseUrl!, apiKey), RequestTimeout, ct)
                    .ConfigureAwait(false)
                : new AiModelListOutcome(null, "The AI base URL of the profile is not a valid https URL.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mandatory: a key that no longer decrypts, or the SSRF guard refusing the host, is this
            // profile's failure to record, not a reason to skip the others. Only the type and the
            // masked message are kept: the key never reaches a log or the database.
            outcome = new AiModelListOutcome(null, AiProviderClient.Mask($"{ex.GetType().Name}: {ex.Message}", apiKey));
        }

        if (outcome.Models is { } models)
        {
            profile.AvailableModelsJson = JsonSerializer.Serialize(models);
            profile.AvailableModelsFetchedAt = now;
            profile.ModelListError = null;
            return true;
        }
        var error = outcome.Error ?? "The AI provider did not list its models.";
        profile.ModelListError = error.Length > 1000 ? error[..1000] : error;
        logger.LogWarning("AI model list refresh failed for profile {ProfileId}: {Error}", profile.Id, profile.ModelListError);
        return false;
    }
}
