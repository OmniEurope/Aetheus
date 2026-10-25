// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

internal sealed class ScannerResourceBudget(long memoryBytes, double cpus)
{
    internal const long CandidateMemoryBytes = 6656L * 1024 * 1024;
    internal const double CandidateCpus = 7.0;
    private const long HeavyMemoryBytes = 2L * 1024 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _changed = new(0, int.MaxValue);
    private long _usedMemoryBytes;
    private double _usedCpus;
    private bool _heavyRunning;

    internal static ScannerResourceBudget Shared { get; } = new(CandidateMemoryBytes, CandidateCpus);

    internal async Task<IAsyncDisposable> AcquireAsync(ScannerManifestEntry scanner, CancellationToken ct)
    {
        var requestedMemory = ParseMemory(scanner.Memory);
        var requestedCpus = ParseCpus(scanner.Cpus);
        var heavy = requestedMemory >= HeavyMemoryBytes;
        if (requestedMemory > memoryBytes || requestedCpus > cpus)
            throw new InvalidDataException(
                $"Scanner '{scanner.Key}' requests more than the candidate resource budget.");

        while (true)
        {
            lock (_sync)
            {
                if (_usedMemoryBytes + requestedMemory <= memoryBytes
                    && _usedCpus + requestedCpus <= cpus
                    && (!heavy || !_heavyRunning))
                {
                    _usedMemoryBytes += requestedMemory;
                    _usedCpus += requestedCpus;
                    _heavyRunning |= heavy;
                    return new Reservation(() => Release(requestedMemory, requestedCpus, heavy));
                }
            }
            await _changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal static long ParseMemory(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var suffixLength = normalized.EndsWith("gb", StringComparison.Ordinal) ? 2 : 1;
        var suffix = normalized[^suffixLength..];
        if (!double.TryParse(normalized[..^suffixLength], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
            throw new InvalidDataException("Scanner memory must be a positive m, mb, g or gb value.");
        var factor = suffix switch
        {
            "m" or "mb" => 1024d * 1024,
            "g" or "gb" => 1024d * 1024 * 1024,
            _ => throw new InvalidDataException("Scanner memory must be expressed in m, mb, g or gb.")
        };
        return checked((long)Math.Ceiling(amount * factor));
    }

    private static double ParseCpus(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException("Scanner CPUs must be a positive number.");

    private void Release(long releasedMemory, double releasedCpus, bool heavy)
    {
        lock (_sync)
        {
            _usedMemoryBytes -= releasedMemory;
            _usedCpus -= releasedCpus;
            if (heavy) _heavyRunning = false;
        }
        _changed.Release();
    }

    /// <summary>Gives the resources back once, through the callback it is created with, so it does not depend
    /// on the budget that hands it out.</summary>
    private sealed class Reservation(Action release) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
            return ValueTask.CompletedTask;
        }
    }
}
