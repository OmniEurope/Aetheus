// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data;

/// <summary>ADR-060: a run's public identifier carries its start, the moment the run is created, so the
/// order of the unique index is chronological for new rows and backfilled ones alike.</summary>
internal static class PipelineRunUid
{
    /// <summary>A UUID v7 cannot carry a time before 1970. A row without a real start takes the first
    /// instant it can hold rather than stopping the backfill on that row for good.</summary>
    internal static Guid From(DateTime startedAtUtc)
    {
        var startedAt = new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc));
        return Guid.CreateVersion7(startedAt < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : startedAt);
    }
}
