// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Ownership transfer requires an active target user and reports that user's Server.Admin
/// permissions per pipeline. Chained runs remain subject to the same authorization gate.</summary>
public interface IPipelineOwnershipService
{
    Task<List<PipelineOwnershipDto>> GetOwnedPipelinesAsync(string ownerUsername, CancellationToken ct = default);

    Task<List<PipelineOwnerCandidateDto>> GetCandidatesAsync(CancellationToken ct = default);

    /// <summary>What <see cref="TransferAsync"/> would do, without writing. Null when
    /// <paramref name="actor"/> is not a platform administrator.</summary>
    Task<PipelineOwnerTransferResultDto?> PreviewAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default);

    /// <summary>Changes the owner of every pipeline of the scope and writes one
    /// <c>PipelineOwnerTransferred</c> audit entry per changed pipeline. Null when
    /// <paramref name="actor"/> is not a platform administrator.</summary>
    Task<PipelineOwnerTransferResultDto?> TransferAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default);

    /// <summary>The same transfer, same checks, owner change and audit entries as
    /// <see cref="TransferAsync"/>, without the per-pipeline Server.Admin report: for a caller that
    /// already showed the preview and does not read the result (recette R1-51, deactivation of an
    /// owner). False when <paramref name="actor"/> is not a platform administrator.</summary>
    Task<bool> TransferWithoutReportAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="IPipelineOwnershipService"/>
public sealed class PipelineOwnershipService(
    IPipelineOwnershipRepository repo,
    IPipelineRunPreparationService preparations,
    IResourceAuthorizationService authz,
    IAuditService audit,
    ILogger<PipelineOwnershipService> logger) : IPipelineOwnershipService
{
    public const string TransferredAuditAction = "PipelineOwnerTransferred";

    public Task<List<PipelineOwnershipDto>> GetOwnedPipelinesAsync(string ownerUsername, CancellationToken ct = default) =>
        repo.GetOwnedPipelinesAsync(ownerUsername, ct);

    public Task<List<PipelineOwnerCandidateDto>> GetCandidatesAsync(CancellationToken ct = default) =>
        repo.GetActiveUsersAsync(ct);

    public Task<PipelineOwnerTransferResultDto?> PreviewAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default) =>
        RunAsync(actor, request, write: false, ct);

    public Task<PipelineOwnerTransferResultDto?> TransferAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default) =>
        RunAsync(actor, request, write: true, ct);

    public async Task<bool> TransferWithoutReportAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct = default)
    {
        var scope = await LoadScopeAsync(actor, request, ct).ConfigureAwait(false);
        if (scope is not { } loaded) return false;
        var (newOwner, pipelines) = loaded;
        await ApplyAsync(pipelines, newOwner, actor.Identity?.Name ?? "unknown", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The active new owner and the pipelines of the scope; null when <paramref name="actor"/>
    /// is not a platform administrator.</summary>
    private async Task<(string NewOwner, List<Data.Entities.Pipeline> Pipelines)?> LoadScopeAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!actor.IsInRole("Admin")) return null;
        ValidateScope(request);

        var requested = request.NewOwnerUsername.Trim().ToLowerInvariant();
        var newOwner = await repo.FindActiveUsernameAsync(requested, ct).ConfigureAwait(false)
            ?? throw new BadRequestException($"The new owner '{requested}' is not an active user.");

        var pipelines = await repo.GetPipelinesInScopeAsync(
            request.PipelineId, request.ProjectId, request.CurrentOwnerUsername?.Trim(), ct).ConfigureAwait(false);
        if (request.PipelineId is { } pipelineId && pipelines.Count == 0)
            throw new NotFoundException($"Pipeline {pipelineId} not found.");
        return (newOwner, pipelines);
    }

    private async Task<PipelineOwnerTransferResultDto?> RunAsync(
        ClaimsPrincipal actor, PipelineOwnerTransferRequest request, bool write, CancellationToken ct)
    {
        var scope = await LoadScopeAsync(actor, request, ct).ConfigureAwait(false);
        if (scope is not { } loaded) return null;
        var (newOwner, pipelines) = loaded;

        var projectNames = await repo.GetProjectNamesAsync(
            pipelines.Where(p => p.ProjectId is not null).Select(p => p.ProjectId!.Value).Distinct().ToList(), ct)
            .ConfigureAwait(false);
        var items = new List<PipelineOwnerTransferItemDto>(pipelines.Count);
        // One authorization answer per distinct server: the answer depends only on the new owner and the
        // server, and a user owning dozens of pipelines usually targets the same few servers.
        var serverAdmin = new Dictionary<int, bool>();
        foreach (var pipeline in pipelines)
        {
            var check = await CheckServerAdminAsync(pipeline.Id, newOwner, serverAdmin, ct).ConfigureAwait(false);
            items.Add(check with
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectName = pipeline.ProjectId is { } projectId ? projectNames.GetValueOrDefault(projectId) : null,
                PreviousOwnerUsername = pipeline.CreatedByUsername
            });
        }

        if (write)
            await ApplyAsync(pipelines, newOwner, actor.Identity?.Name ?? "unknown", ct).ConfigureAwait(false);
        return new PipelineOwnerTransferResultDto { NewOwnerUsername = newOwner, Preview = !write, Items = items };
    }

    private async Task ApplyAsync(
        List<Data.Entities.Pipeline> pipelines, string newOwner, string actorName, CancellationToken ct)
    {
        var changed = pipelines
            .Where(pipeline => !string.Equals(pipeline.CreatedByUsername, newOwner, StringComparison.Ordinal))
            .Select(pipeline => (Pipeline: pipeline, Previous: pipeline.CreatedByUsername))
            .ToList();
        if (changed.Count == 0) return;
        foreach (var (pipeline, _) in changed)
            pipeline.CreatedByUsername = newOwner;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (pipeline, previous) in changed)
        {
            logger.LogInformation(
                "Pipeline '{Name}' (id {Id}) owner transferred from '{Old}' to '{New}' by '{Actor}'",
                pipeline.Name, pipeline.Id, previous, newOwner, actorName);
            await audit.LogAsync("PipelineOwnerTransferred", "Pipeline", pipeline.Id,
                $"{pipeline.Name}: owner '{previous ?? "(none)"}' -> '{newOwner}' by '{actorName}'", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The F-EXEC-1b question for the new owner: Server.Admin on every target server of the pipeline,
    /// resolved with its default parameters (a parameter can move a chained run elsewhere; the gate
    /// re-checks at run time anyway). Same authorization call as the gate.
    /// </summary>
    private async Task<PipelineOwnerTransferItemDto> CheckServerAdminAsync(
        int pipelineId, string newOwner, Dictionary<int, bool> serverAdmin, CancellationToken ct)
    {
        PipelineRunPreparation? preparation;
        try
        {
            preparation = await preparations.PrepareRunAsync(pipelineId, null, null, null, ct).ConfigureAwait(false);
        }
        catch (BadRequestException ex)
        {
            return new PipelineOwnerTransferItemDto { TargetsUnresolvedReason = ex.Message };
        }
        if (preparation is null)
            return new PipelineOwnerTransferItemDto { TargetsUnresolvedReason = "The pipeline definition could not be read." };

        var missing = new List<int>();
        foreach (var serverId in preparation.TargetServerIds)
        {
            if (!serverAdmin.TryGetValue(serverId, out var isAdmin))
            {
                isAdmin = await authz.HasPermissionAsync(newOwner, ResourceType.Server, serverId, Permission.Admin, ct).ConfigureAwait(false);
                serverAdmin[serverId] = isAdmin;
            }
            if (!isAdmin)
                missing.Add(serverId);
        }
        if (missing.Count == 0)
            return new PipelineOwnerTransferItemDto { NewOwnerHoldsServerAdmin = true };
        var names = await repo.GetServerNamesAsync(missing, ct).ConfigureAwait(false);
        return new PipelineOwnerTransferItemDto
        {
            NewOwnerHoldsServerAdmin = false,
            ServersMissingAdmin = missing.Select(id => names.GetValueOrDefault(id) ?? $"#{id}").ToList()
        };
    }

    private static void ValidateScope(PipelineOwnerTransferRequest request)
    {
        var scopes = (request.PipelineId.HasValue ? 1 : 0)
            + (request.ProjectId.HasValue ? 1 : 0)
            + (string.IsNullOrWhiteSpace(request.CurrentOwnerUsername) ? 0 : 1);
        if (scopes != 1)
            throw new BadRequestException(
                "Name exactly one scope: a pipeline, a project, or the current owner of the pipelines.");
    }
}
