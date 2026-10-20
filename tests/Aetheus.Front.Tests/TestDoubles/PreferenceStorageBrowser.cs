// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// The browser of a test that runs the real OmniPreferences (R1-66): OE's <c>omni-preferences.js</c> is
/// played over a dictionary standing for <c>localStorage</c> (get, set, entries, watch, unwatch), so the
/// cache keeps what it is given as a browser would; every other call goes to bUnit's runtime.
/// </summary>
internal sealed class PreferenceStorageBrowser(IJSRuntime inner) : IJSRuntime
{
    private const string PreferencesModule = "./_content/OmniEurope.Blazor/omni-preferences.js";

    private readonly StorageModule _module = new();

    /// <summary>The cache entries, keyed as the script keys them (<c>omni:pref:&lt;scope&gt;:&lt;key&gt;</c>).</summary>
    public ConcurrentDictionary<string, string> Storage => _module.Storage;

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        identifier == "import" && args is [PreferencesModule]
            ? ValueTask.FromResult((TValue)(object)_module)
            : inner.InvokeAsync<TValue>(identifier, cancellationToken, args);

    private sealed class StorageModule : IJSObjectReference
    {
        private int _watchers;

        public ConcurrentDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            args ??= [];
            object? answer = identifier switch
            {
                "get" => Storage.TryGetValue((string)args[0]!, out var value) ? value : null,
                "set" => Store((string)args[0]!, (string)args[1]!),
                "entries" => Storage.Where(pair => pair.Key.StartsWith((string)args[0]!, StringComparison.Ordinal))
                    .Select(pair => new[] { pair.Key, pair.Value }).ToArray(),
                "watch" => Interlocked.Increment(ref _watchers),
                "unwatch" => null,
                _ => throw new InvalidOperationException($"Unexpected call {identifier}."),
            };
            return ValueTask.FromResult((TValue)answer!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private object? Store(string key, string value)
        {
            Storage[key] = value;
            return null;
        }
    }
}
