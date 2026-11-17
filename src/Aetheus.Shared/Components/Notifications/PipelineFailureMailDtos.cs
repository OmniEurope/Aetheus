// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Notifications;

/// <summary>
/// Whether the current user is e-mailed when one pipeline fails. <see cref="IsEnabled"/> is the effective
/// answer: the user's saved choice for the pipeline, else <see cref="DefaultEnabled"/>, which is false for a
/// pipeline started by a schedule (a nightly) and true for any other. The in-app notification of a failure
/// does not depend on it.
/// </summary>
public sealed record PipelineFailureMailPreferenceDto
{
    public int PipelineId { get; init; }
    public bool IsEnabled { get; init; }
    public bool DefaultEnabled { get; init; }
    public bool IsSaved { get; init; }

    /// <summary>The pipeline is started by a schedule, which is why its failure mails are off by default.</summary>
    public bool IsScheduled { get; init; }
}

/// <summary>Body of <c>PUT /api/notifications/me/pipelines/{pipelineId}/failure-mail</c>.</summary>
public sealed record SetPipelineFailureMailPreferenceRequest
{
    public bool IsEnabled { get; init; }
}

/// <summary>A pipeline for which the current user saved a failure-mail choice, for the list in their settings.</summary>
public sealed record PipelineFailureMailChoiceDto
{
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public bool DefaultEnabled { get; init; }
    public bool IsScheduled { get; init; }
}
