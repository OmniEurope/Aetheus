// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Queue-time inputs for a pipeline run. <see cref="SourceBranch"/> overrides the pipeline's
/// configured branch for this run only; it never changes the saved definition.
/// </summary>
[JsonConverter(typeof(PipelineRunRequestJsonConverter))]
public sealed record PipelineRunRequest
{
    public const int MaxParameterCount = 64;
    public const int MaxIdempotencyKeyLength = 64;

    [BoundedDictionary(MaxParameterCount, 128, 4096)]
    public Dictionary<string, string>? Parameters { get; init; }

    [StringLength(255)]
    public string? SourceBranch { get; init; }

    [StringLength(MaxIdempotencyKeyLength)]
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// "Run to end": every step of the run continues on error, so one run exercises everything that
    /// can still run. Accepted only when the platform setting <see cref="PipelineRunToEndSetting.Key"/>
    /// allows it; a run launched this way that had a failure ends Failed and publishes no release.
    /// </summary>
    public bool RunToEnd { get; init; }

    /// <summary>
    /// R-72: deploy the release named by <c>candidateVersion</c> despite what the quality gate holds
    /// against it (no grade, a grade below the threshold, a rejection). Never skips the artifact check:
    /// a release without a kept artifact is still refused. Requires Write on the project, on top of the
    /// rights of a launch, and is recorded on the run and in the audit log. Approval stages still apply.
    /// </summary>
    public bool GateOverride { get; init; }
}
