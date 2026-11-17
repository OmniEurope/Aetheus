// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The reasons an artifact source cannot be resolved (<see cref="PipelineArtifactTaskFactory"/>,
/// <see cref="PipelineReleaseArtifactRules"/>): each one a coded <see cref="PipelineProblem"/> whose English
/// sentence is the one earlier builds stored. The failed step's run warning carries the problem as the cause
/// of its sentence, so the run page says it in the user's language.
/// </summary>
internal static class PipelineArtifactProblems
{
    public static PipelineProblem SourceRequired() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactSourceRequired,
        "'release' or both 'artifact' and 'artifact_source_pipeline' are required.");

    public static PipelineProblem NeverProduced(string artifact, string pipeline) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactNeverProduced,
        $"artifact '{artifact}' was never produced by a successful {pipeline} run.",
        ("artifact", artifact), ("pipeline", pipeline));

    public static PipelineProblem NotFoundAtCommit(string artifact, string pipeline, string? commit) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactNotFoundAtCommit,
        $"artifact '{artifact}' was not found on a successful {pipeline} run at commit {commit}.",
        ("artifact", artifact), ("pipeline", pipeline), ("commit", commit));

    public static PipelineProblem NotFoundOnRun(string artifact, string pipeline, int runId) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactNotFoundOnRun,
        $"artifact '{artifact}' was not found on successful {pipeline} run {runId}.",
        ("artifact", artifact), ("pipeline", pipeline), ("run", runId));

    public static PipelineProblem SelectorInvalid(string selector, string sameCommit, string latestSuccessful) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactSelectorInvalid,
        $"'artifact_source_selector' must be '{sameCommit}' or '{latestSuccessful}', not '{selector}'.",
        ("selector", selector), ("sameCommit", sameCommit), ("latestSuccessful", latestSuccessful));

    public static PipelineProblem NoVerifiedCommit() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactRunNoVerifiedCommit, "the current run has no verified source commit.");

    public static PipelineProblem NoProject() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactRunNoProject, "the current run is not attached to a project.");

    public static PipelineProblem SourceRunOtherProject() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactSourceRunOtherProject, "the source run belongs to another project.");

    public static PipelineProblem SourceRunOtherCommit() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactSourceRunOtherCommit, "the source run belongs to another source commit.");

    public static PipelineProblem SourceRunNotSucceeded() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactSourceRunNotSucceeded,
        "the source run did not succeed; its optional producer artifact is unavailable.");

    public static PipelineProblem ReleaseWithSourcePipeline() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactReleaseWithSourcePipeline,
        "'release' cannot be combined with 'artifact_source_pipeline'.");

    public static PipelineProblem ReleaseNotRetained(string selector) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactReleaseNotRetained,
        $"release '{selector}' has no retained artifact in this project.",
        ("release", selector));

    public static PipelineProblem TargetDirectoryUnsafe() => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactTargetDirectoryUnsafe, "target_directory must be a safe relative path.");

    public static PipelineProblem InvalidSha256(string artifact) => PipelineProblem.Of(
        PipelineProblemCodes.ArtifactInvalidSha256,
        $"artifact '{artifact}' has no valid authoritative SHA-256.", ("artifact", artifact));

    /// <summary>A reason that has no code of its own (a step setting refused after the artifact was found, the
    /// resolver gave no reason): the English sentence stays in the stored problem; the run page, in either language, says the
    /// generic translation.</summary>
    public static PipelineProblem Unresolved(string english = "the requested artifact could not be resolved.") =>
        PipelineProblem.Of(PipelineProblemCodes.ArtifactUnresolved, english, ("detail", english));

    /// <summary>Whether the artifact is simply absent from a producer run (the cases an optional producer artifact
    /// may be skipped for): it was never produced, or the source run did not succeed.</summary>
    public static bool IsProducerArtifactUnavailable(PipelineProblem? problem) =>
        problem?.Code is PipelineProblemCodes.ArtifactNeverProduced
            or PipelineProblemCodes.ArtifactNotFoundAtCommit
            or PipelineProblemCodes.ArtifactNotFoundOnRun
            or PipelineProblemCodes.ArtifactSourceRunNotSucceeded;
}
