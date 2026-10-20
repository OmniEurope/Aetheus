// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Data.Entities;

/// <summary>The resource key is unique so only one active run can hold an environment lock
/// (ADR-049). Terminal holders do not block new owners even if their rows remain.</summary>
public class PipelineResourceLock
{
    [MaxLength(MaxKeyLength)]
    public string Key { get; set; } = string.Empty;

    public int PipelineRunId { get; set; }

    public DateTime AcquiredAt { get; set; }

    public PipelineRun PipelineRun { get; set; } = null!;

    public const int MaxKeyLength = 200;
}
