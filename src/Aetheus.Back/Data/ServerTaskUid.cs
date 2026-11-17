// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data;

/// <summary>ADR-060: a task's public identifier carries its creation time, so the order of the unique
/// index is chronological for new rows and backfilled ones alike.</summary>
internal static class ServerTaskUid
{
    internal static Guid From(DateTime createdAtUtc) =>
        Guid.CreateVersion7(new DateTimeOffset(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)));
}
