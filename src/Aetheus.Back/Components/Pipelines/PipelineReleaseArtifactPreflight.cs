// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The retained-release half of the launch preflight. A <c>restore-artifacts</c> step with a
/// <c>release:</c> selector (the QA rollback proof restores V-1 through <c>previous-deployed</c>)
/// resolves against releases that already exist at launch: nothing the run itself produces can
/// change the answer, so a restore that cannot succeed is known before CI starts.
/// </summary>
public interface IPipelineReleaseArtifactPreflight
{
    /// <summary>One refusal per release restore that the dispatch would fail.</summary>
    Task<IReadOnlyList<PipelineProblem>> FindProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int projectId,
        CancellationToken ct);
}

/// <summary>
/// Applies <see cref="PipelineReleaseArtifactRules"/>, the rules the dispatch applies, to the run's
/// source commit. It judges only what the launch knows: a selector or artifact name that still holds a
/// reference, or a commit-relative selector without a verified commit, is left to the step. The one
/// thing that could change the answer during the run is another deployment or publication of this
/// project, which the refusal names as the way out.
/// </summary>
public sealed class PipelineReleaseArtifactPreflight(IArtifactRepository artifacts, IArtifactStorageService storage)
    : IPipelineReleaseArtifactPreflight
{
    public async Task<IReadOnlyList<PipelineProblem>> FindProblemsAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolvedVariables);
        var commit = resolvedVariables.GetValueOrDefault(PipelineRunService.SourceCommitVariable)
            ?? resolvedVariables.GetValueOrDefault("BUILD_SOURCEVERSION");
        var problems = new List<PipelineProblem>();
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            var variables = StageVariables(stage, resolvedVariables);
            foreach (var step in stage.Steps.Where(IsReleaseRestore))
            {
                var selector = PipelineRunHelpers.SubstituteVariables(step.Release!, variables).Trim();
                var name = PipelineRunHelpers.SubstituteVariables(step.Artifact ?? string.Empty, variables).Trim();
                if (!IsKnown(selector) || !IsKnown(name)
                    || PipelineReleaseArtifactRules.IsCommitRelative(selector) && !PipelineRunHelpers.IsGitCommitHash(commit))
                    continue;
                var reason = await FindReasonAsync(step, selector, name, projectId, commit, ct).ConfigureAwait(false);
                if (reason is not null)
                    problems.Add(PipelineProblem.Of(
                        reason.Code,
                        $"Stage '{stage.Name}', step '{step.Name}' of pipeline '{definition.Name}': {reason.English}",
                        [("stage", stage.Name), ("step", step.Name), ("pipeline", definition.Name), .. reason.Parameters]));
            }
        }
        return problems;
    }

    /// <summary>Why one restore would fail, before the stage, step and pipeline it belongs to are added.</summary>
    private sealed record Reason(string Code, string English, params (string Name, object? Value)[] Parameters);

    private async Task<Reason?> FindReasonAsync(
        PipelineStepDefinition step, string selector, string name, int projectId, string? commit, CancellationToken ct)
    {
        var (artifact, error) = await PipelineReleaseArtifactRules.FindAsync(
            artifacts, projectId, commit, selector, name, ct).ConfigureAwait(false);
        if (artifact is not null)
        {
            if (artifact.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit))
                return new Reason(
                    PipelineProblemCodes.ReleaseArtifactInvalidSha256,
                    $"the retained artifact '{artifact.Name}' of release '{selector}' has no valid SHA-256, "
                    + "so the restore would be refused. Republish the release that carries it.",
                    ("artifact", artifact.Name), ("release", selector));
            return await FindMissingPayloadAsync(artifact, selector, ct).ConfigureAwait(false);
        }

        var skippable = step.AllowMissing
            && PipelineReleaseArtifactRules.IsBootstrapSelector(selector)
            && error?.Contains(PipelineReleaseArtifactRules.NoRetainedArtifact, StringComparison.Ordinal) == true
            && !await PipelineReleaseArtifactRules.IsRequiredByPriorContractAsync(
                artifacts, selector, projectId, commit, step.Artifact, ct).ConfigureAwait(false);
        if (skippable) return null;

        var why = step.AllowMissing
            ? " allow_missing does not apply: the retained release was sealed with a contract that requires it."
            : string.Empty;
        return new Reason(
            step.AllowMissing ? PipelineProblemCodes.ReleaseArtifactRequiredByContract : PipelineProblemCodes.ReleaseArtifactNotRetained,
            $"release '{selector}' has no retained artifact '{name}' in this project.{why} "
            + "The step would fail. Deploy or publish a release that retains this artifact before launching.",
            ("release", selector), ("artifact", name));
    }

    /// <summary>
    /// The row alone does not prove the restore: the agent downloads the payload file, and a row whose
    /// file is gone answers 404 in the middle of the run. That is refused here, never skipped through
    /// <c>allow_missing</c>: the release exists, so a missing V-1 payload is a fault, not a bootstrap.
    /// </summary>
    private async Task<Reason?> FindMissingPayloadAsync(PipelineArtifact artifact, string selector, CancellationToken ct)
    {
        await using (var payload = storage.OpenArtifact(artifact.FilePath))
        {
            if (payload is not null) return null;
        }
        var version = await artifacts.FindNewestReleaseVersionAsync(artifact.Id, ct).ConfigureAwait(false);
        var release = version is null ? $"release '{selector}'" : $"release '{selector}' (version {version})";
        return new Reason(
            version is null ? PipelineProblemCodes.ReleaseArtifactPayloadMissing : PipelineProblemCodes.ReleaseVersionArtifactPayloadMissing,
            $"{release} resolves to artifact #{artifact.Id} '{artifact.Name}', whose payload file is missing "
            + "from the artifact storage, so the restore would fail with a 404 during the run. "
            + "allow_missing does not apply: the release exists, only its file is gone. "
            + "Deploy again a release that retains this artifact, or restore the file in the artifact storage, before launching.",
            ("release", selector), ("version", version), ("artifactId", artifact.Id), ("artifact", artifact.Name));
    }

    private static bool IsReleaseRestore(PipelineStepDefinition step) =>
        string.Equals(step.Type, "restore-artifacts", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(step.Release)
        && string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline);

    private static bool IsKnown(string value) => !value.Contains("$(", StringComparison.Ordinal);

    private static Dictionary<string, string> StageVariables(
        PipelineStageDefinition stage, IReadOnlyDictionary<string, string> resolvedVariables)
    {
        var variables = new Dictionary<string, string>(resolvedVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stage.Variables) variables[key] = value;
        return variables;
    }
}
