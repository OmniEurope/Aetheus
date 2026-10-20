// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// Reads a stored API profile for a run: whether it can run at all, and the task variables that carry
/// it to the agent. The variables travel inside the task environment, which is encrypted at rest
/// (<c>TaskEnvProtection</c>) and only ever handed to the agent the task is assigned to.
/// </summary>
internal static class AiProfileRuntime
{
    /// <summary>Why the profile cannot run, or null. A profile written by the CLI design has no
    /// provider: it reads as "to reconfigure" and is refused, never guessed.</summary>
    public static string? Problem(AiRunnerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!AiProviderCatalog.TryParse(profile.Provider, out _)
            || string.IsNullOrWhiteSpace(profile.Model)
            || string.IsNullOrWhiteSpace(profile.ApiKeyEncrypted))
            return $"AI runner profile '{profile.Name}' must be reconfigured as an API profile (provider, model and API key).";
        return null;
    }

    public static bool NeedsReconfiguration(AiRunnerProfile profile) => Problem(profile) is not null;

    /// <summary>The decrypted key, for the run's own masking. Call only after <see cref="Problem"/>.</summary>
    public static string ApiKey(AiRunnerProfile profile, IEncryptionService encryption) =>
        encryption.DecryptValue(profile.ApiKeyEncrypted);

    /// <summary>The base URL a run of this profile calls: the stored one, or the provider default when the
    /// profile left it empty.</summary>
    public static string BaseUrl(AiRunnerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        AiProviderCatalog.TryParse(profile.Provider, out var provider);
        return string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? AiProviderCatalog.DefaultBaseUrl(provider)
            : profile.BaseUrl;
    }

    public static Dictionary<string, string> Environment(
        AiRunnerProfile profile, IEncryptionService encryption)
    {
        AiProviderCatalog.TryParse(profile.Provider, out var provider);
        var baseUrl = BaseUrl(profile);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AETHEUS_AI_PROFILE_NAME"] = profile.Name,
            ["AETHEUS_AI_SENDS_DATA_EXTERNALLY"] = profile.SendsDataExternally ? "true" : "false",
            ["AETHEUS_AI_PROVIDER"] = AiProviderCatalog.Token(provider),
            ["AETHEUS_AI_BASE_URL"] = baseUrl,
            ["AETHEUS_AI_MODEL"] = profile.Model,
            ["AETHEUS_AI_API_KEY"] = ApiKey(profile, encryption),
            ["AETHEUS_AI_EFFORT"] = profile.ReasoningEffort,
            ["AETHEUS_AI_MAX_OUTPUT_TOKENS"] = profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            ["AETHEUS_AI_MAX_OUTPUT_BYTES"] = profile.MaxOutputBytes.ToString(CultureInfo.InvariantCulture)
        };
    }
}
