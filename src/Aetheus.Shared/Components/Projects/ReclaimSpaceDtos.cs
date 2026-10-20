// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

/// <summary> The tables whose disk space « Récupérer la place » gives back, their size now, the one
/// running, and the results so far. A purge frees rows, not disk: only <c>VACUUM FULL</c> does.
/// </summary>
public sealed record ReclaimSpaceStatusDto
{
    public int ActivePipelineRuns { get; init; }
    public string? RunningTable { get; init; }
    public List<ReclaimSpaceTableDto> Tables { get; init; } = [];
    public List<ReclaimSpaceResultDto> History { get; init; } = [];
}

/// <summary>A table space can be reclaimed from and its size on disk now (null without PostgreSQL).</summary>
public sealed record ReclaimSpaceTableDto(string Table, long? Bytes);

/// <summary>One « Récupérer la place » of one table, measured before and after.</summary>
public sealed record ReclaimSpaceResultDto
{
    public string Table { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long? BytesBefore { get; init; }
    public long? BytesAfter { get; init; }
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
}

/// <summary>The table to give space back from, one of <see cref="ReclaimSpaceStatusDto.Tables"/>.</summary>
public sealed record ReclaimSpaceRequest
{
    [Required]
    [StringLength(64)]
    public string Table { get; init; } = string.Empty;
}
