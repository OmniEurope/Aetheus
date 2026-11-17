// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Tells whether the version of a runtime or a base image is out of support, for the recommendation. One read of the
/// stored end-of-life cycles serves a whole page (no network, no read per row), and only when a row names a runtime
/// or an image the cycles cover. The item is mapped to its cycle by <see cref="EndOfLifeCycleResolver"/>; a version
/// whose cycle is not known is "not known to be", never out of support.
/// </summary>
internal sealed class CveWatchEndOfLife(IEndOfLifeCycleSource cycles, TimeProvider timeProvider)
{
    public async Task<EndOfLifeLookup> LoadAsync(IEnumerable<CveWatchMatchDto> rows, CancellationToken ct)
    {
        var products = rows.Select(row => EndOfLifeCycleResolver.ProductOf(row.Ecosystem, row.PackageName))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var stored = products.Count == 0 ? [] : await cycles.GetCyclesAsync(products, ct).ConfigureAwait(false);
        return new EndOfLifeLookup(stored, DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
    }
}

/// <summary>The cycles one read brought back, and the day they are judged on.</summary>
internal sealed class EndOfLifeLookup(IReadOnlyCollection<VulnerabilityEndOfLifeCycle> cycles, DateOnly today)
{
    public bool HasEnded(CveWatchMatchDto row) =>
        EndOfLifeCycleResolver.Find(row.Ecosystem, row.PackageName, row.Version, cycles) is { } cycle
        && EndOfLifeCycleResolver.HasEnded(cycle, today);
}
