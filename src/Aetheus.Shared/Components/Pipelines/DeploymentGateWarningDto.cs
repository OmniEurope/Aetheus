// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// R-72 (owner decision of 2026-10-28): one reason the quality gate has against deploying a release. A
/// deployment of that release stays possible, after a confirmation that lists these reasons, as long as
/// the release keeps its artifacts; the confirmed launch records them on the run.
/// </summary>
public enum DeploymentGateWarningKind
{
    /// <summary>The release carries no sealed assurance grade.</summary>
    NoGrade = 0,

    /// <summary>The sealed grade is below the deployment threshold the pipeline declares.</summary>
    BelowMinimum = 1,

    /// <summary>The sealed grade is F (said only when the threshold does not already refuse it).</summary>
    GradedF = 2,

    /// <summary>The Gate rejected the release (a blocking rule was crossed).</summary>
    Rejected = 3,

    /// <summary>The release has a grade but the Gate gave no verdict on it.</summary>
    NoVerdict = 4,

    /// <summary>Blocking tests failed on the candidate.</summary>
    BlockingTests = 5,

    /// <summary>Security findings are still open on the run that published the release.</summary>
    SecurityFindings = 6
}

public sealed record DeploymentGateWarningDto
{
    public DeploymentGateWarningKind Kind { get; init; }

    /// <summary>The release's sealed grade, when the reason is about it.</summary>
    public AnalysisGrade? Grade { get; init; }

    /// <summary>The deployment threshold, for <see cref="DeploymentGateWarningKind.BelowMinimum"/>.</summary>
    public AnalysisGrade? MinimumGrade { get; init; }

    /// <summary>How many tests or findings, for the reasons that count something.</summary>
    public int? Count { get; init; }
}
