// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What declaring a holder amounts to, written nowhere: the vault or library the project holds, and the
/// pipeline YAML with it declared (unchanged when it already was).</summary>
public sealed record PipelineHolderDeclaration(
    PipelineSetupReadinessContainerKind Kind, string Name, int HolderId, string Yaml, bool Changed);

/// <summary>R-24: declares a vault or a variable library of the project in a pipeline's definition.</summary>
public interface IPipelineHolderDeclarationService
{
    /// <summary>The edit asked of <paramref name="pipeline"/>, written nowhere, so that the caller can authorize it
    /// on the holder it names. A holder the project does not hold, or a definition the YAML cannot take, is a
    /// <see cref="BadRequestException"/>.</summary>
    Task<PipelineHolderDeclaration> PrepareAsync(
        PipelineDto pipeline, DeclarePipelineHolderRequest request, CancellationToken ct);

    /// <summary>Writes <paramref name="declaration"/> as the update of a pipeline does: validated as the save
    /// validates it, then <see cref="IPipelineService.UpdatePipelineAsync"/> (file committed, a repository change
    /// Aetheus has not seen refuses the write, a failed save puts the file back, audited and broadcast). A holder
    /// already declared writes nothing.</summary>
    Task<PipelineDto> ApplyAsync(PipelineDto pipeline, PipelineHolderDeclaration declaration, CancellationToken ct);
}

/// <inheritdoc cref="IPipelineHolderDeclarationService"/>
public sealed class PipelineHolderDeclarationService(
    IPipelineService pipelineService,
    IPipelineRepository pipelines,
    IPipelineTemplateResolver templateResolver,
    IPipelineRequirementsChecker requirements,
    IAuditService audit,
    ILogger<PipelineHolderDeclarationService> logger) : IPipelineHolderDeclarationService
{
    public async Task<PipelineHolderDeclaration> PrepareAsync(
        PipelineDto pipeline, DeclarePipelineHolderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(request);
        var projectId = pipeline.ProjectId
            ?? throw new BadRequestException($"Pipeline '{pipeline.Name}' belongs to no project, so no holder can be declared in it.");
        var isVault = request.Kind == PipelineSetupReadinessContainerKind.Vault;

        // Only a holder a run of the project can read: the same lookup the readiness and the launch use.
        var ids = isVault
            ? await requirements.FindVaultIdsAsync([request.Name], projectId, ct).ConfigureAwait(false)
            : await requirements.FindLibraryIdsAsync([request.Name], projectId, ct).ConfigureAwait(false);
        var found = ids.FirstOrDefault(pair => string.Equals(pair.Key, request.Name, StringComparison.OrdinalIgnoreCase));
        if (found.Key is null)
            throw new BadRequestException(
                $"The project has no {(isVault ? "vault" : "variable library")} named '{request.Name}' that its pipelines can read.");

        var inherited = await InheritedNamesAsync(pipeline, request.Kind, ct).ConfigureAwait(false);
        var edit = PipelineHolderListYamlEditor.Declare(pipeline.YamlDefinition, request.Kind, found.Key, inherited);
        return edit.Succeeded
            ? new PipelineHolderDeclaration(request.Kind, found.Key, found.Value, edit.Yaml!, edit.Changed)
            : throw new BadRequestException(edit.Error!);
    }

    public async Task<PipelineDto> ApplyAsync(PipelineDto pipeline, PipelineHolderDeclaration declaration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(declaration);
        if (!declaration.Changed)
        {
            return pipeline;
        }

        // The same gate as PUT api/pipelines/{id}: what is refused there is refused here, with its reasons.
        var validation = await pipelineService.ValidateYamlStrictAsync(
            declaration.Yaml, pipeline.ProjectId, pipeline.EnvironmentId, pipeline.ProjectServerId, ct: ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new BadRequestException(string.Join("; ", validation.Errors));
        }

        var updated = await pipelineService.UpdatePipelineAsync(pipeline.Id, new UpdatePipelineRequest
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = declaration.Yaml,
            ProjectId = pipeline.ProjectId,
            SourceRepositoryId = pipeline.SourceRepositoryId,
            SourceBranch = pipeline.SourceBranch,
            EnvironmentId = pipeline.EnvironmentId,
            ProjectServerId = pipeline.ProjectServerId,
            AiRunnerProfileId = pipeline.AiRunnerProfileId
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Pipeline {pipeline.Id} was not found.");

        // The update logs "Updated"; this line says what the update was.
        await audit.LogAsync(
            "HolderDeclared", "Pipeline", pipeline.Id,
            $"{(declaration.Kind == PipelineSetupReadinessContainerKind.Vault ? "vaults" : "variable_libraries")}: + '{declaration.Name}'",
            ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>The names the pipeline reads through the template it extends, when it lists none of its own (its list then
    /// replaces the template's, so the edit must carry them). Empty for a pipeline that extends nothing, one whose
    /// template cannot be resolved (the readiness reads such a pipeline's own YAML too), or one that lists its own.</summary>
    private async Task<IReadOnlyList<string>> InheritedNamesAsync(
        PipelineDto pipeline, PipelineSetupReadinessContainerKind kind, CancellationToken ct)
    {
        var own = YamlParsingHelper.ParseAndValidate(pipeline.YamlDefinition, logger);
        if (own?.Extends is null
            || await pipelines.GetPipelineOrganizationIdAsync(pipeline.Id, ct).ConfigureAwait(false) is not { } organizationId)
        {
            return [];
        }

        try
        {
            var resolved = (await templateResolver.ResolveAsync(pipeline.YamlDefinition, organizationId, parameters: null, ct)
                .ConfigureAwait(false)).Definition;
            return [.. kind == PipelineSetupReadinessContainerKind.Vault ? resolved.Vaults : resolved.VariableLibraries];
        }
        catch (BadRequestException exception)
        {
            logger.LogDebug(exception, "A holder is declared in '{Pipeline}' without its template's names: the template could not be resolved.", pipeline.Name);
            return [];
        }
    }
}
