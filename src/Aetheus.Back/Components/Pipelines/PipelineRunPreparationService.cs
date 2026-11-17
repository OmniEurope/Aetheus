// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Works out what a run would execute, before anything is persisted: which branch and commit the
/// pipeline is pinned to, which YAML is authoritative (git-first for a project-owned pipeline), what
/// the template resolves that YAML to, and whether the result is legal to run at all.
/// </summary>
public interface IPipelineRunPreparationService
{
    /// <summary>Resolves a runnable snapshot of <paramref name="pipelineId"/>, or <c>null</c> when the
    /// pipeline does not exist.</summary>
    /// <param name="yamlOverride">Bypasses the git-first read (dry-run / preflight of unsaved YAML).</param>
    /// <param name="commitOverride">Pins the snapshot to a commit instead of the branch head.</param>
    /// <param name="workspaceCommitOverride">With a <c>source:</c> block, pins the workspace to this commit of
    /// the source repository instead of its branch head; ignored without one.</param>
    /// <exception cref="BadRequestException">The branch is invalid, the definition is illegal, or the
    /// pipeline needs a workspace it cannot be given.</exception>
    Task<PipelineRunPreparation?> PrepareRunAsync(
        int pipelineId, string? sourceBranch, string? yamlOverride, string? commitOverride, CancellationToken ct,
        string? workspaceCommitOverride = null);

    /// <summary>The parameters a launch dialog should ask for, read from the same authoritative
    /// definition the trigger will use.</summary>
    Task<List<PipelineRunParameterDto>> GetRunParametersAsync(
        int pipelineId, string? sourceBranch = null, CancellationToken ct = default);
}

/// <summary>
/// The preparation half of the engine, extracted from <see cref="PipelineRunService"/>. Everything here
/// happens before a <see cref="PipelineRun"/> row exists: it reads, resolves and validates, and the only
/// way it can fail is by throwing or returning <c>null</c>. Nothing it does is observable if the caller
/// walks away, which is what makes it separable from the run engine.
/// </summary>
public sealed class PipelineRunPreparationService(
    IPipelineRepository repo,
    IPipelineGitService pipelineGit,
    IPipelineTemplateResolver templateResolver,
    IPipelineRunParameterResolver parameterResolver,
    IPipelineWorkspaceSourceResolver workspaceSources,
    IConfiguration configuration,
    ILogger<PipelineRunPreparationService> logger) : IPipelineRunPreparationService
{
    /// <summary>
    /// A pipeline made only of `trigger` steps orchestrates other runs and never checks anything out, so
    /// it needs no workspace - and therefore no System:Prepare stage. The run engine asks the same
    /// question when it decides whether that stage must have completed.
    /// </summary>
    public static bool RequiresWorkspace(PipelineYamlDefinition definition)
        => YamlParsingHelper.FlattenJobs(definition)
            .Any(stage => stage.Steps.Any(step => !string.Equals(step.Type, "trigger", StringComparison.OrdinalIgnoreCase)));

    public async Task<PipelineRunPreparation?> PrepareRunAsync(
        int id, string? sourceBranch, string? yamlOverride, string? commitOverride, CancellationToken ct,
        string? workspaceCommitOverride = null)
    {
        var timing = new PhaseTiming(TimeProvider.System);
        var pipeline = await repo.FindPipelineAsync(id, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        var mirroredDefinition = YamlParsingHelper.ParseAndValidate(pipeline.YamlDefinition, logger);
        var selectedBranch = SelectBranch(sourceBranch, mirroredDefinition, pipeline);
        var effectiveProjectId = pipeline.ProjectId
            ?? await repo.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false);
        // Recette R-51 (ADR-058): a deleted project waiting for its server cleanup must not deploy again what
        // the cleanup removes.
        if (effectiveProjectId is { } projectId && await repo.IsProjectDeletionPendingAsync(projectId, ct).ConfigureAwait(false))
            throw new ConflictException(
                $"Pipeline '{pipeline.Name}' belongs to a deleted project that waits for its server cleanup: it can no longer run.");
        timing.Mark("pipeline-and-project");
        var source = await ResolvePipelineSourceAsync(pipeline, effectiveProjectId, selectedBranch, ct).ConfigureAwait(false);
        timing.Mark("source");
        var (branch, commitHash) = PinRevision(pipeline, source, selectedBranch, commitOverride);
        timing.Mark("pin-revision");
        var repositoryUrl = ResolveRepositoryUrl(source, pipeline);
        var definitionYaml = await ResolveDefinitionYamlAsync(
            pipeline, effectiveProjectId, branch, commitHash, yamlOverride, ct).ConfigureAwait(false);
        timing.Mark("definition-yaml");
        // Keys this backend does not know are skipped by the parser; the run says so.
        var definitionWarnings = PipelineYamlDiagnostics.UnknownPropertyStoredWarnings(definitionYaml, logger);
        RejectNearMissKeys(definitionYaml);
        var organizationId = await repo.GetPipelineOrganizationIdAsync(id, ct).ConfigureAwait(false);
        var resolution = await templateResolver.ResolveAsync(definitionYaml, organizationId ?? 0, parameters: null, ct)
            .ConfigureAwait(false);
        // R-63: the production environment's server is the default of the deployment's server, when the YAML names none.
        var definition = await PipelineProductionServerDefault.ApplyAsync(repo, resolution.Definition, effectiveProjectId, ct)
            .ConfigureAwait(false);
        timing.Mark("templates-and-server-default");
        var effectiveStages = YamlParsingHelper.FlattenJobs(definition);
        ValidatePreparedDefinition(definition, effectiveStages);
        // R-11: an imported external definition is checked again at every run: a catalogue template it extends may have changed.
        if (pipeline.IsExternalImport)
            ThrowIfValidationErrors(PipelineExternalImportPolicy.Validate(definition));
        var targetIds = await parameterResolver.ResolveCandidateTargetServerIdsAsync(
            definition, organizationId, effectiveProjectId, ct).ConfigureAwait(false);
        var preparation = await ApplyWorkspaceSourceAsync(new PipelineRunPreparation
        {
            PipelineId = id,
            Pipeline = pipeline,
            BranchName = branch,
            CommitHash = commitHash,
            RepositoryUrl = repositoryUrl,
            YamlSnapshot = resolution.Yaml,
            Definition = definition,
            EffectiveStages = effectiveStages,
            EffectiveProjectId = effectiveProjectId,
            TargetServerIds = targetIds,
            DefinitionWarnings = definitionWarnings
        }, source?.RepositoryId, workspaceCommitOverride, ct).ConfigureAwait(false);
        ValidateWorkspaceRequirements(definition, preparation.RepositoryUrl, preparation.CommitHash);
        timing.Mark("targets-and-workspace");
        var prepared = preparation with
        {
            ReleaseSeries = await ReadReleaseSeriesAsync(preparation, source?.RepositoryId, ct).ConfigureAwait(false)
        };
        timing.Mark("release-series");
        // R-17: the manual trigger prepares the run (source reads) before the launcher starts its own clock.
        logger.Log(timing.LevelFor(configuration), "Preparation timing of pipeline {PipelineId}: {Timing}", id, timing.Describe());
        return prepared;
    }

    /// <summary>
    /// R-33: a definition that declares <c>APPLICATION_VERSION_PREFIX</c> (a pipeline the setup wizard generates)
    /// names its series itself, which wins over the repository; a malformed prefix refuses the launch, and an empty one
    /// (R-18) names none, so the repository's series below applies.
    /// Otherwise, the series of the commit the run releases: the <c>major.minor</c> of the <c>VersionPrefix</c> of
    /// its <c>Directory.Build.props</c>, read at the pinned commit so the number is counted in the same
    /// series the app-version function composes from the same file. A workspace taken from another
    /// repository (<c>source:</c> block) is not read here and counts in the empty series.
    /// </summary>
    private async Task<string> ReadReleaseSeriesAsync(
        PipelineRunPreparation preparation, int? sourceRepositoryId, CancellationToken ct)
    {
        if (PipelineApplicationVersion.DeclaredSeries(preparation.Definition) is { } declared)
            return declared;
        if (preparation.EffectiveProjectId is not { } projectId
            || preparation.Definition.Source is not null
            || !IsGitCommitHash(preparation.CommitHash))
            return ReleaseSeries.None;
        var props = await pipelineGit.ReadVersionPropsAtRevisionAsync(
            projectId, preparation.CommitHash!, ct, sourceRepositoryId ?? preparation.Pipeline.SourceRepositoryId)
            .ConfigureAwait(false);
        return ReleaseSeries.FromVersionProps(props);
    }

    /// <summary>The branch asked for, else the one the mirrored definition, the pipeline or its project
    /// names, in that order; refused when it is not a valid branch name.</summary>
    private static string? SelectBranch(string? sourceBranch, PipelineYamlDefinition? mirroredDefinition, Pipeline pipeline)
    {
        var branch = sourceBranch
            ?? mirroredDefinition?.SourceBranch
            ?? pipeline.SourceBranch
            ?? pipeline.Project?.DefaultBranch;
        if (!string.IsNullOrWhiteSpace(branch) && !PipelineBranchValidator.IsValid(branch))
            throw new BadRequestException("The selected source branch is invalid.");
        return branch;
    }

    /// <summary>The branch and commit the run is pinned to: the commit override, else the project
    /// repository's commit (an internal repository, or the mirror of an external one).</summary>
    private static (string? Branch, string? CommitHash) PinRevision(
        Pipeline pipeline, PipelineSourceDto? source, string? branch, string? commitOverride)
    {
        if (pipeline.IsExternalImport) return PinImportedRevision(pipeline, source, commitOverride);
        ThrowIfExternalAddressWithoutMirror(pipeline, source);
        return (branch, commitOverride ?? source?.CommitHash);
    }

    /// <summary>
    /// Recette R-4 (decision of 2026-11-10, option B): a project that names a repository address but has no repository in
    /// Aetheus (no internal repository, no mirror) used to be read straight from that address (ls-remote, a fetch of the
    /// pipeline file, an agent checkout of the raw URL). Every read of an external repository now goes through a project
    /// mirror, so such a project is refused with a message that says to create the mirror, never read from the address.
    /// </summary>
    private static void ThrowIfExternalAddressWithoutMirror(Pipeline pipeline, PipelineSourceDto? source)
    {
        if (source is null && ExternalRepositoryAddress.IsUnmirrored(pipeline.Project?.RepositoryUrl))
            throw ExternalRepositoryMirrorRequirement.Refusal();
    }

    /// <summary>
    /// R-11 security review, finding 2 (decision of 2026-11-10): an imported pipeline runs the code of the commit it was
    /// imported at, never the mirror's current head, so the reviewed definition and the code it builds are the same
    /// revision. New code is taken only by an explicit re-import, which moves both. A commit override other than that
    /// commit is refused, and so is a run whose mirror cannot be resolved (the workspace would otherwise fall back to
    /// another repository URL).
    /// </summary>
    private static (string? Branch, string? CommitHash) PinImportedRevision(
        Pipeline pipeline, PipelineSourceDto? source, string? commitOverride)
    {
        if (commitOverride is not null
            && !string.Equals(commitOverride, pipeline.ExternalImportCommit, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException(
                $"Pipeline '{pipeline.Name}' was imported at commit {pipeline.ExternalImportCommit}: it runs that commit only. "
                + "Import it again to run newer code.");
        if (source is null || source.RepositoryId != pipeline.SourceRepositoryId)
            throw new BadRequestException(
                $"Pipeline '{pipeline.Name}' was imported from an external repository mirror that can no longer be read.");
        return (pipeline.SourceBranch ?? source.Branch, pipeline.ExternalImportCommit);
    }

    /// <summary>A misspelled known key (a guard such as approval_timeout_minutes) is refused, never skipped.</summary>
    private void RejectNearMissKeys(string definitionYaml)
    {
        var nearMissKeys = PipelineYamlDiagnostics.NearMissKeyProblems(definitionYaml, logger);
        if (nearMissKeys.Count > 0)
            throw PipelineProblem.Refusal(nearMissKeys);
    }

    /// <summary>A source block separates the definition repository and revision from the
    /// checked-out workspace repository and commit. Without a source block, preparation is
    /// unchanged.</summary>
    private async Task<PipelineRunPreparation> ApplyWorkspaceSourceAsync(
        PipelineRunPreparation preparation, int? definitionRepositoryId, string? workspaceCommit, CancellationToken ct)
    {
        if (preparation.Definition.Source is not { } workspaceSource) return preparation;
        if (preparation.EffectiveProjectId is not { } projectId)
            throw new BadRequestException("A source: block needs a pipeline that belongs to a project.");

        var workspace = await workspaceSources.ResolveAsync(
            projectId, workspaceSource, preparation.Pipeline.SourceRepositoryId ?? definitionRepositoryId,
            preparation.CommitHash, ct, workspaceCommit).ConfigureAwait(false);
        return preparation with
        {
            BranchName = workspace.Branch,
            CommitHash = workspace.CommitHash,
            RepositoryUrl = MirrorCloneUrl.Rehome(configuration, workspace.CloneUrl),
            DefinitionCommitHash = preparation.CommitHash,
            DefinitionBranchName = preparation.BranchName
        };
    }

    public async Task<List<PipelineRunParameterDto>> GetRunParametersAsync(
        int pipelineId, string? sourceBranch = null, CancellationToken ct = default)
    {
        var pipeline = await repo.FindPipelineAsync(pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null) return [];

        // Read the authoritative (git-first) definition the same way the trigger does, so the dialog
        // reflects what will actually run for a project-owned pipeline.
        var yaml = pipeline.YamlDefinition;
        // R-11: an imported pipeline runs its stored, reviewed definition, never the mirror's current file.
        if (pipeline.ProjectId is { } projectId && !pipeline.IsExternalImport)
        {
            var branch = sourceBranch ?? pipeline.SourceBranch ?? pipeline.Project?.DefaultBranch;
            // R-4: the launch dialog of a project that names an external address without a mirror says so, as the launch
            // does, rather than offering the parameters of a definition nothing will read.
            if (ExternalRepositoryAddress.IsUnmirrored(pipeline.Project?.RepositoryUrl))
                ThrowIfExternalAddressWithoutMirror(
                    pipeline, await ResolvePipelineSourceAsync(pipeline, projectId, branch, ct).ConfigureAwait(false));
            var gitYaml = await pipelineGit.ReadProjectPipelineYamlAsync(projectId, pipeline.Name, ct,
                branch, pipeline.SourceRepositoryId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(gitYaml)) yaml = gitYaml;
        }

        var organizationId = await repo.GetPipelineOrganizationIdAsync(pipelineId, ct).ConfigureAwait(false) ?? 0;
        var resolution = await templateResolver.ResolveAsync(yaml, organizationId, parameters: null, ct)
            .ConfigureAwait(false);
        // R-63: the production environment's server is the default of the deployment's server, when the YAML names none.
        var definition = await PipelineProductionServerDefault.ApplyAsync(repo, resolution.Definition, pipeline.ProjectId, ct)
            .ConfigureAwait(false);

        // R-33: the version a launch without one takes is shown in its empty field.
        var proposedVersion = await PipelineApplicationVersion.ProposeAsync(repo, definition, pipeline.ProjectId, ct)
            .ConfigureAwait(false);
        return definition.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new PipelineRunParameterDto
            {
                Name = p.Name,
                DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Name : p.DisplayName!,
                Type = p.Type,
                Default = p.Default,
                Required = p.Required,
                Description = p.Description,
                DisplayNameFr = string.IsNullOrWhiteSpace(p.DisplayNameFr) ? null : p.DisplayNameFr,
                DescriptionFr = string.IsNullOrWhiteSpace(p.DescriptionFr) ? null : p.DescriptionFr,
                AllowedValues = p.AllowedValues,
                Advanced = p.Advanced,
                Placeholder = string.Equals(p.Name, PipelineApplicationVersion.ParameterName, StringComparison.OrdinalIgnoreCase)
                    ? proposedVersion
                    : null
            })
            .ToList();
    }

    private async Task<PipelineSourceDto?> ResolvePipelineSourceAsync(
        Pipeline pipeline,
        int? projectId,
        string? branch,
        CancellationToken ct) => projectId is not { } sourceProjectId
        ? null
        : await pipelineGit.GetPipelineSourceAsync(
            sourceProjectId, pipeline.Name, ct, branch, pipeline.SourceRepositoryId).ConfigureAwait(false);

    private string? ResolveRepositoryUrl(PipelineSourceDto? source, Pipeline pipeline)
    {
        if (!string.IsNullOrWhiteSpace(source?.CloneUrl))
            return MirrorCloneUrl.Rehome(configuration, source.CloneUrl);
        // R-4: an external address is never handed to the agent as the workspace to clone; only Aetheus's own clone URLs are.
        var projectUrl = pipeline.Project?.RepositoryUrl;
        return ExternalRepositoryAddress.IsUnmirrored(projectUrl) ? null : MirrorCloneUrl.Rehome(configuration, projectUrl);
    }

    private async Task<string> ResolveDefinitionYamlAsync(
        Pipeline pipeline,
        int? projectId,
        string? branch,
        string? commitHash,
        string? yamlOverride,
        CancellationToken ct)
    {
        // R-11: an imported pipeline runs its stored, reviewed definition (a dry run of unsaved YAML included), never
        // the mirror's current file: what runs is what a user with WRITE on the project imported.
        if (pipeline.IsExternalImport) return pipeline.YamlDefinition;
        if (yamlOverride is not null || projectId is not { } gitProjectId)
            return yamlOverride ?? pipeline.YamlDefinition;
        var gitYaml = !string.IsNullOrWhiteSpace(commitHash)
            ? await pipelineGit.ReadProjectPipelineYamlAtRevisionAsync(
                gitProjectId, pipeline.Name, commitHash, ct, pipeline.SourceRepositoryId).ConfigureAwait(false)
            : await pipelineGit.ReadProjectPipelineYamlAsync(
                gitProjectId, pipeline.Name, ct, branch, pipeline.SourceRepositoryId).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(gitYaml) ? pipeline.YamlDefinition : gitYaml;
    }

    private static void ValidatePreparedDefinition(
        PipelineYamlDefinition definition,
        IReadOnlyList<PipelineStageDefinition> stages)
    {
        ThrowIfValidationErrors(ValidateMatrixValues(stages));
        ThrowIfValidationErrors(ValidateIsolationDefinitions(definition.Isolation, stages));
        ThrowIfValidationErrors(ValidateIsolationLimits(stages));
        ThrowIfValidationErrors(ValidateExecutionRoles(stages));
        ThrowIfValidationErrors(PipelineAnalysisGateOrderingValidator.Validate(stages));
    }

    private static void ThrowIfValidationErrors(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0) throw new BadRequestException(string.Join(" ", errors));
    }

    private static void ValidateWorkspaceRequirements(
        PipelineYamlDefinition definition,
        string? repositoryUrl,
        string? commitHash)
    {
        if (!RequiresWorkspace(definition)) return;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            throw new BadRequestException(
                "This pipeline requires a workspace, but no canonical source repository is configured.");
        if (!IsGitCommitHash(commitHash))
            throw new BadRequestException(
                "This pipeline requires a workspace, but its source repository could not be pinned to an immutable commit.");
    }
}
