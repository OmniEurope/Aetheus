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
    IGitCliService gitCli,
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
        var pipeline = await repo.FindPipelineAsync(id, ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        var mirroredDefinition = YamlParsingHelper.ParseAndValidate(pipeline.YamlDefinition, logger);
        var branch = sourceBranch
            ?? mirroredDefinition?.SourceBranch
            ?? pipeline.SourceBranch
            ?? pipeline.Project?.DefaultBranch;
        if (!string.IsNullOrWhiteSpace(branch) && !PipelineBranchValidator.IsValid(branch))
            throw new BadRequestException("The selected source branch is invalid.");
        var effectiveProjectId = pipeline.ProjectId
            ?? await repo.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false);
        var source = await ResolvePipelineSourceAsync(pipeline, effectiveProjectId, branch, ct).ConfigureAwait(false);
        var commitHash = commitOverride ?? source?.CommitHash;
        if (source is null && pipeline.Project?.RepositoryUrl is { Length: > 0 } externalUrl
            && commitOverride is null)
        {
            // A project may point straight at a public HTTPS repository without an internal mirror.
            // Pin its selected branch before creating a run; the agent then checks out this exact SHA.
            var remote = await gitCli.ResolveBranchCommitAsync(externalUrl, branch, ct).ConfigureAwait(false);
            branch = remote.Branch;
            commitHash = remote.Commit;
        }
        var repositoryUrl = ResolveRepositoryUrl(source, pipeline);
        var definitionYaml = await ResolveDefinitionYamlAsync(
            pipeline, effectiveProjectId, branch, commitHash, yamlOverride, source is null, ct).ConfigureAwait(false);
        // Recette R2-041: keys this backend does not know are skipped by the parser; the run says so.
        var definitionWarnings = PipelineYamlDiagnostics.UnknownPropertyWarnings(definitionYaml, logger);
        // A misspelled known key (a guard such as approval_timeout_minutes) is refused, never skipped.
        var nearMissKeys = PipelineYamlDiagnostics.NearMissKeyErrors(definitionYaml, logger);
        if (nearMissKeys.Count > 0)
            throw new BadRequestException(string.Join(" ", nearMissKeys));
        var organizationId = await repo.GetPipelineOrganizationIdAsync(id, ct).ConfigureAwait(false);
        var resolution = await templateResolver.ResolveAsync(definitionYaml, organizationId ?? 0, parameters: null, ct)
            .ConfigureAwait(false);
        definitionYaml = resolution.Yaml;
        var definition = resolution.Definition;
        var effectiveStages = YamlParsingHelper.FlattenJobs(definition);
        ValidatePreparedDefinition(definition, effectiveStages);
        var targetIds = await parameterResolver.ResolveCandidateTargetServerIdsAsync(
            definition, organizationId, effectiveProjectId, ct).ConfigureAwait(false);
        var preparation = await ApplyWorkspaceSourceAsync(new PipelineRunPreparation
        {
            PipelineId = id,
            Pipeline = pipeline,
            BranchName = branch,
            CommitHash = commitHash,
            RepositoryUrl = repositoryUrl,
            YamlSnapshot = definitionYaml,
            Definition = definition,
            EffectiveStages = effectiveStages,
            EffectiveProjectId = effectiveProjectId,
            TargetServerIds = targetIds,
            DefinitionWarnings = definitionWarnings
        }, source?.RepositoryId, workspaceCommitOverride, ct).ConfigureAwait(false);
        ValidateWorkspaceRequirements(definition, preparation.RepositoryUrl, preparation.CommitHash);
        return preparation;
    }

    /// <summary>
    /// Recette R-534: a <c>source:</c> block moves the workspace to another repository of the project.
    /// The definition keeps the repository and the revision it was read at, now recorded beside the
    /// workspace's; the branch, commit and clone URL of the run become those of what it checks out.
    /// Without the block the preparation is returned as it is.
    /// </summary>
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
        if (pipeline.ProjectId is { } projectId)
        {
            var branch = sourceBranch ?? pipeline.SourceBranch ?? pipeline.Project?.DefaultBranch;
            var gitYaml = await pipelineGit.ReadProjectPipelineYamlAsync(projectId, pipeline.Name, ct,
                branch, pipeline.SourceRepositoryId).ConfigureAwait(false);
            if (pipeline.Project?.RepositoryUrl is { Length: > 0 } url
                && await ResolvePipelineSourceAsync(pipeline, projectId, branch, ct).ConfigureAwait(false) is null)
            {
                var remote = await gitCli.ResolveBranchCommitAsync(url, branch, ct).ConfigureAwait(false);
                gitYaml = await gitCli.ReadPipelineYamlAsync(url, remote.Commit, pipeline.Name, ct).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(gitYaml)) yaml = gitYaml;
        }

        var organizationId = await repo.GetPipelineOrganizationIdAsync(pipelineId, ct).ConfigureAwait(false) ?? 0;
        var resolution = await templateResolver.ResolveAsync(yaml, organizationId, parameters: null, ct)
            .ConfigureAwait(false);
        var definition = resolution.Definition;

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
                AllowedValues = p.AllowedValues
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
        return string.IsNullOrWhiteSpace(source?.CloneUrl)
            ? pipeline.Project?.RepositoryUrl
            : MirrorCloneUrl.Rehome(configuration, source.CloneUrl);
    }

    private async Task<string> ResolveDefinitionYamlAsync(
        Pipeline pipeline,
        int? projectId,
        string? branch,
        string? commitHash,
        string? yamlOverride,
        bool externalSource,
        CancellationToken ct)
    {
        if (yamlOverride is not null || projectId is not { } gitProjectId)
            return yamlOverride ?? pipeline.YamlDefinition;
        if (externalSource && pipeline.Project?.RepositoryUrl is { Length: > 0 } externalUrl
            && !string.IsNullOrWhiteSpace(commitHash))
        {
            var externalYaml = await gitCli.ReadPipelineYamlAsync(externalUrl, commitHash, pipeline.Name, ct).ConfigureAwait(false);
            return externalYaml ?? pipeline.YamlDefinition;
        }
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
