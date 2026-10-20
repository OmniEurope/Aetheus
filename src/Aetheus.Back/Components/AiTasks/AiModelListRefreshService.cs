// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// Recette R3-7: reads, about once a week, the models each API profile's key can use
/// (<c>GET /v1/models</c> at Anthropic, <c>GET {base}/models</c> at OpenAI and the vendors serving its
/// API) and keeps the list on the profile, where the profile dialog offers it in place of the
/// application's fixed list. The pass wakes every <see cref="CheckInterval"/>, does nothing outside the
/// night window (<see cref="NightStartHourUtc"/> to <see cref="NightEndHourUtc"/>, UTC), and reads
/// only the profiles not attempted in the last <see cref="RecheckAfter"/>, so each profile is read about once
/// a week (in the first night window after 6.5 days, so on the same weekday), through <see cref="AiModelListReader"/> (also behind the profile dialog's "read now"
/// action). A failure keeps the previous list and records the reason, API key masked; the key is
/// never logged. Only the leader instance (the live blue-green colour) runs it.
/// </summary>
public sealed class AiModelListRefreshService(
    IServiceScopeFactory scopeFactory,
    AiModelListReader reader,
    TimeProvider timeProvider,
    ILogger<AiModelListRefreshService> logger,
    IPostgresLeaderLease? leaderLease = null)
    : PeriodicBackgroundService(CheckInterval, leaderLease, "aetheus:ai-model-list-refresh", timeProvider)
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(156); // 6.5 days: a weekly read lands in the same night window each week
    internal const int NightStartHourUtc = 1;
    internal const int NightEndHourUtc = 5;

    /// <summary>True between <see cref="NightStartHourUtc"/> included and <see cref="NightEndHourUtc"/>
    /// excluded, UTC.</summary>
    internal static bool IsNight(DateTime utcNow) =>
        utcNow.Hour >= NightStartHourUtc && utcNow.Hour < NightEndHourUtc;

    protected override Task ExecuteIterationAsync(CancellationToken ct) => RefreshAsync(ct);

    protected override void LogIterationError(Exception exception) =>
        logger.LogError(exception, "Error during the weekly AI model list refresh");

    /// <summary>One pass at the clock's time: nothing outside the night window.</summary>
    internal async Task<int> RefreshAsync(CancellationToken ct)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        return IsNight(now) ? await RefreshDueProfilesAsync(now, ct).ConfigureAwait(false) : 0;
    }

    /// <summary>Reads the list of every profile due at <paramref name="now"/>, saving each one as it
    /// goes; returns how many lists were read successfully.</summary>
    internal async Task<int> RefreshDueProfilesAsync(DateTime now, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiTaskRepository>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var profiles = await repo.GetProfilesDueForModelListAsync(now - RecheckAfter, ct).ConfigureAwait(false);
        var read = 0;
        foreach (var profile in profiles)
        {
            // The repository already keeps to the supported providers; a profile it would let through
            // otherwise keeps the application's fixed list, untouched.
            if (!AiProviderCatalog.TryParse(profile.Provider, out var provider)
                || string.IsNullOrWhiteSpace(profile.ApiKeyEncrypted))
                continue;
            if (await reader.ReadIntoAsync(profile, provider, encryption, now, ct).ConfigureAwait(false))
                read++;
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        if (profiles.Count > 0)
            logger.LogInformation("AI model list refresh read {Read} of {Due} profiles", read, profiles.Count);
        return read;
    }

    /// <summary>The stored list, or empty when none was read or the column does not hold a JSON array
    /// of strings.</summary>
    internal static List<string> ReadModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Mandatory: a damaged column falls back to the fixed list instead of breaking the profile read.
            return [];
        }
    }
}
