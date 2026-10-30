// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// What an <c>on_success:</c> hop came to: the run it started, or the reason it did not start one.
/// Every cause of a refusal carries its own <see cref="Problem"/> so the upstream run and the
/// notification can say what actually happened, rather than one message for several causes.
/// </summary>
/// <param name="Run">The started run; <c>null</c> when the hop was refused.</param>
/// <param name="Problem">Why nothing was started; <c>null</c> when <paramref name="Run"/> is set.</param>
/// <param name="Inner">The coded problems of the refusal <paramref name="Problem"/> wraps (a definition that
/// cannot be prepared), so the upstream run says them in the user's language.</param>
public sealed record PipelineChainedRunOutcome(
    PipelineRunDto? Run, PipelineProblem? Problem, IReadOnlyList<ProblemDto>? Inner = null)
{
    /// <summary>The English sentence of the refusal; <c>null</c> when the hop started a run.</summary>
    public string? Reason => Problem?.English;

    public static PipelineChainedRunOutcome Started(PipelineRunDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(run, null);
    }

    /// <summary>A refusal given as an English sentence only (its code is the generic "not started" one).</summary>
    public static PipelineChainedRunOutcome Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, PipelineProblem.Of(PipelineProblemCodes.DownstreamNotStarted, reason, ("detail", reason)));
    }

    public static PipelineChainedRunOutcome Refused(PipelineProblem problem, IReadOnlyList<ProblemDto>? inner = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(null, problem, inner);
    }
}
