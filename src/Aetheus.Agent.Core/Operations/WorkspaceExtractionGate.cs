// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Serializes the extraction of artifacts into one workspace. The steps of a job run concurrently
/// (ADR-012), so several <c>restore-artifacts</c> steps unpack into the same directory at the same time, and
/// their archives share marker files (<c>.pipeline-artifacts/source-commit</c>, the delivery contract, the
/// provenance manifest). Extraction overwrites a file through a handle that refuses any sharing, so two
/// restores reaching the same marker together made the loser fail with "being used by another process".
/// Taking turns gives the same result a sequential job would have, and the checks that read each archive's
/// own entries run before this gate, so none of them is weakened.
/// </summary>
internal static class WorkspaceExtractionGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Gate> Gates = new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Waits until no other restore is extracting into <paramref name="workspace"/>; dispose the
    /// result once extraction is done.</summary>
    internal static async Task<IDisposable> EnterAsync(string workspace, CancellationToken ct)
    {
        var key = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Gate gate;
        lock (Sync)
        {
            if (!Gates.TryGetValue(key, out gate!))
                Gates[key] = gate = new Gate();
            gate.Users++;
        }

        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            return new Lease(() =>
            {
                gate.Semaphore.Release();
                Leave(key, gate);
            });
        }
        finally
        {
            if (!entered) Leave(key, gate);
        }
    }

    private static void Leave(string key, Gate gate)
    {
        lock (Sync)
        {
            if (--gate.Users == 0) Gates.Remove(key);
        }
    }

    private sealed class Gate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Restores holding or waiting for the gate; the entry leaves the table at zero.</summary>
        internal int Users { get; set; }
    }

    /// <summary>Gives the gate back once, through the callback it is created with, so it does not depend on
    /// the gate that hands it out.</summary>
    private sealed class Lease(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            release();
        }
    }
}
