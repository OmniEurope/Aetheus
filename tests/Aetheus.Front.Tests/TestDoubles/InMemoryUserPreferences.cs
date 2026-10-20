// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// The user preferences of a test (R1-66): JSON values in memory, serialized the way OmniPreferences
/// serializes them, so a component test reads back what the component wrote and can seed what the
/// server would hold.
/// </summary>
internal sealed class InMemoryUserPreferences : IUserPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Every key read, in order, so a test can count the reads of one key.</summary>
    public List<string> Reads { get; } = [];

    public event Func<string, Task>? Changed;

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        Reads.Add(key);
        return Task.FromResult(Values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Values[key] = JsonSerializer.Serialize(value, JsonOptions);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>Seeds a value as if the server held it.</summary>
    public void Seed<T>(string key, T value) => Values[key] = JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>The value written under <paramref name="key"/>, read as <typeparamref name="T"/>.</summary>
    public T? Read<T>(string key) => Values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;

    /// <summary>Raises <see cref="Changed"/> as a change from another device would.</summary>
    public Task RaiseChangedAsync(string key) => Changed is { } handlers ? handlers(key) : Task.CompletedTask;
}
