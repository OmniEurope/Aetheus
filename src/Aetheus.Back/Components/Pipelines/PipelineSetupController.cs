// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The setup wizard's own surface. It sits beside <see cref="PipelinesController"/> rather than
/// inside it because it answers a question about a project that has no pipelines yet, and because
/// that controller is already at its size budget.
/// </summary>
[ApiController]
[Route("api/pipelines/setup")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
public class PipelineSetupController(
    IPipelineSetupReadinessService setupReadiness,
    IPipelineRequirementsProvisioner provisioner,
    IPipelineProductionSetupService productionSetup,
    IPipelineProjectCopyService projectCopy,
    IPipelineOwnerAuthorization ownerAuthz,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary> What copying a project's pipelines into another project would do. Read on the
    /// source project, and the rights the creation itself needs on the target (Pipeline/Write and
    /// ownership of the target project). Only the source pipelines, libraries and vaults the caller
    /// can read are considered, and only names leave this endpoint, never a value. </summary>
    [HttpPost("copy-plan")]
    public async Task<ActionResult<PipelineProjectCopyPlanDto>> PlanProjectCopy(
        [FromBody] PipelineProjectCopyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await CanCopyAsync(request, requireResourceWrite: false, ct))
            return Forbid();

        return Ok(await projectCopy.PlanAsync(request,
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct), ct));
    }

    /// <summary> Makes the copies, with the structure of the libraries and vaults they use (names
    /// only, values empty), so the rights to create libraries and vaults are needed too. </summary>
    [HttpPost("copy")]
    public async Task<ActionResult<PipelineProjectCopyResultDto>> CopyProject(
        [FromBody] PipelineProjectCopyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await CanCopyAsync(request, requireResourceWrite: true, ct))
            return Forbid();

        return Ok(await projectCopy.CopyAsync(request, await ResolveOrganizationIdAsync(ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Pipeline, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct), ct));
    }

    private async Task<bool> CanCopyAsync(PipelineProjectCopyRequest request, bool requireResourceWrite, CancellationToken ct) =>
        await authz.HasPermissionAsync(User, ResourceType.Project, request.SourceProjectId, Permission.Read, ct)
        && await authz.HasPermissionAsync(User, ResourceType.Project, request.TargetProjectId, Permission.Write, ct)
        // RBAC audit, finding 9: each right is held in the target project's organization, not in any one.
        && await authz.CanCreateInProjectAsync(User, ResourceType.Pipeline, request.TargetProjectId, ct)
        && await ownerAuthz.CanOwnAsync(User, request.TargetProjectId, null, null, ct)
        && (!requireResourceWrite
            || (await authz.CanCreateInProjectAsync(User, ResourceType.VariableLibrary, request.TargetProjectId, ct)
                && await authz.CanCreateInProjectAsync(User, ResourceType.Vault, request.TargetProjectId, ct)));

    /// <summary>
    /// What would stop the pipelines the wizard is about to create from running. Gated on WRITE
    /// access to the project - the same permission the creation itself needs - so it cannot be used
    /// to probe the repository or fleet of a project the caller may not change.
    /// </summary>
    [HttpPost("readiness")]
    public async Task<ActionResult<PipelineSetupReadinessDto>> CheckReadiness(
        [FromBody] PipelineSetupReadinessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();

        var organizationId = await ResolveOrganizationIdAsync(ct);

        return Ok(await setupReadiness.CheckAsync(
            request.ProjectId, request.TemplateNames, organizationId, ct));
    }

    /// <summary> What would stop the project's existing pipelines from running, shown on its
    /// pipelines page. Read access to the project suffices: it reports on that project's own
    /// pipelines and repository, which a reader can already open, and on runners only within the
    /// caller's own organization (an administrator, the whole fleet), as the wizard's check does.
    /// </summary>
    [HttpGet("project-readiness")]
    public async Task<ActionResult<PipelineSetupReadinessDto>> CheckProjectReadiness(
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        return Ok(await setupReadiness.CheckProjectAsync(
            projectId, pipelineId: null, await ResolveOrganizationIdAsync(ct), ct));
    }

    /// <summary> The same verdict for one pipeline, asked just before it is launched. Gated on read
    /// access to the pipeline and to its project; a pipeline with no project gets an empty verdict.
    /// </summary>
    [HttpGet("pipeline-readiness")]
    public async Task<ActionResult<PipelineSetupReadinessDto>> CheckPipelineReadiness(
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] int pipelineId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Pipeline, pipelineId, Permission.Read, ct))
            return Forbid();
        if (await setupReadiness.FindPipelineProjectIdAsync(pipelineId, ct) is not { } projectId)
            return Ok(new PipelineSetupReadinessDto());
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        return Ok(await setupReadiness.CheckProjectAsync(
            projectId, pipelineId, await ResolveOrganizationIdAsync(ct), ct));
    }

    /// <summary> The libraries and vaults this project's pipelines require and it lacks. Read
    /// access to the project suffices: it only names resources and pipelines of that project.
    /// </summary>
    [HttpGet("unmet")]
    public async Task<ActionResult<List<UnmetRequirementDto>>> GetUnmetRequirements(
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        var organizationId = await ResolveOrganizationIdAsync(ct);

        return Ok(await provisioner.GetUnmetAsync(projectId, organizationId, ct));
    }

    /// <summary>Provisioning copies missing library and vault key names with empty values. It
    /// requires project and resource-creation permissions and reads only resources accessible to
    /// the caller.</summary>
    [HttpPost("provision")]
    public async Task<ActionResult<PipelineRequirementsProvisionResultDto>> ProvisionRequirements(
        [FromBody] PipelineSetupReadinessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await AuthorizeProvisioningAsync(request.ProjectId, ct) is not { } scope)
            return Forbid();

        return Ok(await provisioner.ProvisionAsync(
            request.ProjectId, request.TemplateNames, scope.OrganizationId, scope.ReadableLibraries, scope.ReadableVaults, ct));
    }

    /// <summary> The overview's one-click fix. Creates what <see cref="GetUnmetRequirements"/>
    /// reports for the project's existing pipelines, under the same permissions as the wizard's.
    /// </summary>
    [HttpPost("provision-unmet")]
    public async Task<ActionResult<PipelineRequirementsProvisionResultDto>> ProvisionUnmetRequirements(
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] int projectId, CancellationToken ct)
    {
        if (await AuthorizeProvisioningAsync(projectId, ct) is not { } scope)
            return Forbid();

        return Ok(await provisioner.ProvisionUnmetAsync(
            projectId, scope.OrganizationId, scope.ReadableLibraries, scope.ReadableVaults, ct));
    }

    /// <summary>What the production deployment of the project needs, read from the Compose file of its repository:
    /// the files found, the variables that file reads and where each value comes from, the servers. Gated on WRITE
    /// access to the project, like the readiness check, since it reads the repository.</summary>
    [HttpPost("production-analysis")]
    public async Task<ActionResult<PipelineProductionAnalysisDto>> AnalyzeProduction(
        [FromBody] PipelineProductionAnalysisRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();

        return Ok(await productionSetup.AnalyzeAsync(
            request.ProjectId, request.ComposeFile, await AdministeredServersAsync(ct), ct));
    }

    /// <summary>Creates the variables, secrets and ports the Compose file asks for and the project's configuration
    /// repository with its versioned files. It writes to the project, its libraries and its vaults, so all three
    /// rights are needed, as for provisioning; a value the operator gave for a secret goes to the vault only.</summary>
    [HttpPost("production-apply")]
    public async Task<ActionResult<PipelineProductionApplyResultDto>> ApplyProduction(
        [FromBody] PipelineProductionApplyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await AuthorizeProvisioningAsync(request.ProjectId, ct) is null)
            return Forbid();

        return Ok(await productionSetup.ApplyAsync(request, await AdministeredServersAsync(ct), ct));
    }

    /// <summary>Recette R-77: the servers a project with none may be given, those the user administers, since launching
    /// the deployment needs Admin on its target. A server outside them is never offered nor attached.</summary>
    private async Task<ProductionServerScope> AdministeredServersAsync(CancellationToken ct) =>
        new(await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Server, Permission.Admin, ct));

    /// <summary>Mends the environment sample the project's configuration repository already holds: a refused variable
    /// name becomes its scoped name there and in the library <c>prod</c>. It writes to the project's configuration
    /// repository and to its library, so both rights are needed (no vault is touched).</summary>
    [HttpPost("production-repair")]
    public async Task<ActionResult<PipelineProductionRepairResultDto>> RepairProductionEnvSample(
        [FromBody] PipelineProductionRepairRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct)
            || !await authz.CanCreateInProjectAsync(User, ResourceType.VariableLibrary, request.ProjectId, ct))
            return Forbid();

        return Ok(await productionSetup.RepairEnvSampleAsync(request.ProjectId, ct));
    }

    // An admin sees the whole fleet; anyone else is answered about their own organization, so a
    // runner they could never reach is not counted as one they have.
    private async Task<int?> ResolveOrganizationIdAsync(CancellationToken ct)
    {
        var organizationIds = await authz.GetUserOrganizationIdsAsync(User, ct);
        int? organizationId = User.IsInRole("Admin") ? null : organizationIds.FirstOrDefault();
        return organizationId is <= 0 ? null : organizationId;
    }

    // Provisioning writes to the project and creates libraries and vaults: all three rights are needed
    // (null = forbidden). Key names are then read only from the libraries and vaults the caller can read.
    private async Task<(int? OrganizationId, List<int>? ReadableLibraries, List<int>? ReadableVaults)?> AuthorizeProvisioningAsync(
        int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct)
            || !await authz.CanCreateInProjectAsync(User, ResourceType.VariableLibrary, projectId, ct)
            || !await authz.CanCreateInProjectAsync(User, ResourceType.Vault, projectId, ct))
            return null;

        return (await ResolveOrganizationIdAsync(ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.VariableLibrary, Permission.Read, ct),
            await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Vault, Permission.Read, ct));
    }
}
