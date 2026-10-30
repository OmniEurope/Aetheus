// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What a pipeline update does with the definition file: the YAML the database mirror will
/// hold, whether the repository must be written at all, and the content to put back if the database
/// save fails after the repository was written.</summary>
internal sealed record PipelineUpdatePlan(string Yaml, bool WriteToGit, string? RollbackYaml);

/// <summary>
/// Aetheus never overwrites a repository change it has not seen. Before a pipeline update writes
/// <c>.pipeline/*.yaml</c>, the repository's file at the target branch is read and compared with the
/// definition Aetheus last synced (the database mirror):
/// <list type="bullet">
/// <item>the definition was not edited (a branch or settings-only save): the repository's file becomes
/// the definition and nothing is committed;</item>
/// <item>the definition was edited and the repository's file is the one Aetheus last synced, or does
/// not exist: the edit is committed;</item>
/// <item>the definition was edited and the repository's file differs from both: the save is refused
/// (409) unless the caller quotes the hash of the file it reviewed.</item>
/// </list>
/// </summary>
internal static class PipelineRepositoryDefinitionGuard
{
    /// <summary>Line endings and trailing blank lines carry no meaning for a definition.</summary>
    public static string Normalize(string? yaml) =>
        (yaml ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

    public static bool Same(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    public static string Sha256(string yaml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(yaml))));

    public static PipelineRepositoryDefinitionDto Describe(string? yaml) =>
        string.IsNullOrWhiteSpace(yaml)
            ? new PipelineRepositoryDefinitionDto()
            : new PipelineRepositoryDefinitionDto { Exists = true, Yaml = yaml, Sha256 = Sha256(yaml) };

    public static async Task<PipelineUpdatePlan> PlanAsync(
        IPipelineGitService pipelineGit,
        PipelineGitDefinitionLocation? previous,
        PipelineGitDefinitionLocation next,
        string syncedYaml,
        UpdatePipelineRequest request,
        CancellationToken ct)
    {
        var repositoryYaml = BlankToNull(await Read(pipelineGit, next, ct).ConfigureAwait(false));
        var previousYaml = previous is null
            ? null
            : previous == next
                ? repositoryYaml
                : BlankToNull(await Read(pipelineGit, previous, ct).ConfigureAwait(false));
        var rollbackYaml = previousYaml;

        if (Same(request.YamlDefinition, syncedYaml))
        {
            // Nothing was edited: the repository, not Aetheus's copy, is the definition. A file already
            // there is adopted as it is; only a rename or a first placement has to write one.
            if (repositoryYaml is not null)
                return new PipelineUpdatePlan(repositoryYaml, WriteToGit: false, rollbackYaml);
            return new PipelineUpdatePlan(previousYaml ?? request.YamlDefinition, WriteToGit: true, rollbackYaml);
        }

        if (repositoryYaml is null)
            return new PipelineUpdatePlan(request.YamlDefinition, WriteToGit: true, rollbackYaml);
        if (Same(repositoryYaml, request.YamlDefinition))
            return new PipelineUpdatePlan(request.YamlDefinition, WriteToGit: false, rollbackYaml);
        if (Same(repositoryYaml, syncedYaml)
            || string.Equals(request.ConfirmedRepositoryYamlSha256, Sha256(repositoryYaml), StringComparison.Ordinal))
            return new PipelineUpdatePlan(request.YamlDefinition, WriteToGit: true, rollbackYaml);

        throw new ConflictException(
            $"The definition file of pipeline '{next.PipelineName}' on branch '{next.SourceBranch ?? "default"}' "
            + "changed in the repository since Aetheus last synced it, and saving would overwrite those changes. "
            + "Review the difference against the repository file and confirm, or reload the pipeline to adopt the repository's version.");
    }

    private static Task<string?> Read(IPipelineGitService pipelineGit, PipelineGitDefinitionLocation location, CancellationToken ct) =>
        pipelineGit.ReadProjectPipelineYamlAsync(
            location.ProjectId, location.PipelineName, ct, location.SourceBranch, location.SourceRepositoryId);

    private static string? BlankToNull(string? yaml) => string.IsNullOrWhiteSpace(yaml) ? null : yaml;
}
