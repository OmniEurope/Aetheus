// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Users;

/// <summary>
/// Validates and stores the per-user preferences. The browser stamps each change with its own clock;
/// a date in the server's future is brought back to now, so a device whose clock runs ahead cannot
/// hold a key against every later write. A write older than the stored value is ignored: the server
/// keeps the latest value and the browser cache follows it at its next synchronization.
/// </summary>
public sealed class UserPreferenceService(IUserPreferenceRepository repository, TimeProvider time) : IUserPreferenceService
{
    public async Task<List<UserPreferenceDto>> GetAllAsync(int userId, DateTimeOffset? since, CancellationToken ct = default)
    {
        var rows = await repository.ListAsync(userId, since?.UtcDateTime, ct).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<UserPreferenceDto?> GetAsync(int userId, string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        var row = await repository.FindAsync(userId, key, ct).ConfigureAwait(false);
        return row is null ? null : ToDto(row);
    }

    public async Task<UserPreferenceDto> SaveAsync(int userId, string key, SaveUserPreferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureValidKey(key);
        EnsureValidValue(request.Value);

        var now = time.GetUtcNow().UtcDateTime;
        var updatedAt = request.UpdatedAt == default || request.UpdatedAt.UtcDateTime > now ? now : request.UpdatedAt.UtcDateTime;
        var stored = await repository.FindAsync(userId, key, ct).ConfigureAwait(false);
        if (stored is not null && stored.ChangedAt > updatedAt)
            return ToDto(stored);

        var (count, totalLength) = await repository.GetUsageAsync(userId, key, ct).ConfigureAwait(false);
        if (stored is null && count + 1 > UserPreferenceRules.MaxKeys)
            throw new BadRequestException($"A user keeps at most {UserPreferenceRules.MaxKeys} preferences.");
        if (totalLength + (request.Value?.Length ?? 0) > UserPreferenceRules.MaxTotalLength)
            throw new BadRequestException($"The preferences of a user hold at most {UserPreferenceRules.MaxTotalLength} characters.");

        var row = await repository.UpsertAsync(userId, key, request.Value, updatedAt, ct).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task RemoveAsync(int userId, string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        if (await repository.FindAsync(userId, key, ct).ConfigureAwait(false) is null)
            return;
        await repository.UpsertAsync(userId, key, null, time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
    }

    private static void EnsureValidKey(string key)
    {
        if (!UserPreferenceRules.IsValidKey(key))
            throw new BadRequestException(
                $"A preference key has 1 to {UserPreferenceRules.MaxKeyLength} letters, digits, dots, dashes, underscores or colons and starts with a letter or a digit.");
    }

    private static void EnsureValidValue(string? value)
    {
        if (value is null)
            return;
        if (value.Length > UserPreferenceRules.MaxValueLength)
            throw new BadRequestException($"A preference value holds at most {UserPreferenceRules.MaxValueLength} characters.");
        try
        {
            using var document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            // Mandatory: the value is the browser's JSON, anything else is refused as a bad request.
            throw new BadRequestException("A preference value must be JSON.");
        }
    }

    private static UserPreferenceDto ToDto(UserPreference row) => new()
    {
        Key = row.Key,
        Value = row.Value,
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(row.ChangedAt, DateTimeKind.Utc))
    };
}
