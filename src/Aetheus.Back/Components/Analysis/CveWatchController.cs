// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Aetheus.Back.Components.Analysis;

[ApiController]
[Route("api/analysis/cve-watch")]
[Authorize]
public sealed class CveWatchController(
    ICveWatchQueryService query,
    ICveRemediationService remediation,
    ICveWatchOverviewService overview,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>R-15: the advisories the caller's projects are concerned by, one entry per CVE with its projects, across
    /// every organization the caller belongs to (a site Admin: all), only the projects it may read. Not the page of the
    /// per-organization listing above: this one groups by advisory and filters.</summary>
    [HttpGet("advisories")]
    public async Task<ActionResult<PaginatedResult<CveWatchAdvisoryGroupDto>>> GetAdvisories(
        [FromQuery] int? organizationId,
        [FromQuery] int? projectId,
        [FromQuery] bool includeClosed,
        [FromQuery] AnalysisSeverity? severity,
        [FromQuery] bool exploitedOnly,
        [FromQuery] bool withoutFixOnly,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery] int pageSize = PaginationRequest.DefaultPageSize,
        CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        if (organizationId is { } only && scope.OrganizationIds is not null && !scope.OrganizationIds.Contains(only)) return Forbid();
        var filter = new CveWatchAdvisoryFilter(organizationId, projectId, includeClosed, severity, exploitedOnly, withoutFixOnly);
        return Ok(await overview.GetAdvisoriesAsync(scope, filter, page, pageSize, ct));
    }

    /// <summary>R-15: the figures of the CVE watch tab (open CVE that are critical, exploited, with no fix), the projects
    /// whose inventory is not complete and the freshness of the sources, over the projects the caller may read.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<CveWatchOverviewDto>> GetOverview(CancellationToken ct = default) =>
        Ok(await overview.GetOverviewAsync(await ReadScopeAsync(ct), ct));

    /// <summary>R-22: one run of the watch, a report it wrote. A report of another kind, or of a project the caller may not read, is a 404.</summary>
    [HttpGet("runs/{reportId:int}")]
    public async Task<ActionResult<CveWatchRunDto>> GetRun(int reportId, CancellationToken ct = default)
    {
        var run = await overview.GetRunAsync(reportId, await ReadScopeAsync(ct), ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>What the watch says of one project: its open CVE by severity, the inventories that feed the watch and the last pass.
    /// A project the caller may not read, in any organization, is a 404.</summary>
    [HttpGet("projects/{projectId:int}")]
    public async Task<ActionResult<CveWatchProjectDto>> GetProject(int projectId, CancellationToken ct = default)
    {
        var project = await overview.GetProjectAsync(projectId, await ReadScopeAsync(ct), ct);
        return project is null ? NotFound() : Ok(project);
    }

    /// <summary>The match an analysis finding was written for, its recommendation checked against the registry. A match the caller may not read is a 404.</summary>
    [HttpGet("by-finding/{findingId:int}")]
    public async Task<ActionResult<CveWatchMatchDto>> GetMatchByFinding(int findingId, CancellationToken ct = default)
    {
        var organizationIds = User.IsInRole(RoleNames.Admin) ? null : await authz.GetUserOrganizationIdsAsync(User, ct);
        var accessibleProjectIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        var match = await query.GetDetailByFindingAsync(findingId, organizationIds, accessibleProjectIds, ct);
        return match is null ? NotFound() : Ok(match);
    }

    private async Task<CveWatchScope> ReadScopeAsync(CancellationToken ct) => new(
        User.IsInRole(RoleNames.Admin) ? null : await authz.GetUserOrganizationIdsAsync(User, ct),
        await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct));

    /// <summary>The vulnerabilities the watch holds for one organization, as the other fleet listings scope them:
    /// a member of the organization (or a site Admin), and only the projects it may read.</summary>
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<CveWatchMatchDto>>> GetMatches(
        [FromQuery, BindRequired, Range(1, int.MaxValue)] int organizationId,
        [FromQuery] int? projectId,
        [FromQuery] bool includeClosed,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery] int pageSize = PaginationRequest.DefaultPageSize,
        CancellationToken ct = default)
    {
        if (!User.IsInRole(RoleNames.Admin)
            && !(await authz.GetUserOrganizationIdsAsync(User, ct)).Contains(organizationId))
            return Forbid();
        var accessibleProjectIds = await authz.GetAccessibleResourceIdsAsync(
            User, ResourceType.Project, Permission.Read, ct);
        return Ok(await query.GetPageAsync(organizationId, accessibleProjectIds, projectId, includeClosed, page, pageSize, ct));
    }

    /// <summary>One match, its recommendation checked against the package's registry (one network call, kept an hour).
    /// A match the caller may not read, in any organization or project, is a 404.</summary>
    [HttpGet("{matchId:long}")]
    public async Task<ActionResult<CveWatchMatchDto>> GetMatch(long matchId, CancellationToken ct = default)
    {
        var organizationIds = User.IsInRole(RoleNames.Admin) ? null : await authz.GetUserOrganizationIdsAsync(User, ct);
        var accessibleProjectIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        var match = await query.GetDetailAsync(matchId, organizationIds, accessibleProjectIds, ct);
        return match is null ? NotFound() : Ok(match);
    }

    /// <summary>The latest attempt to fix a match. Same reach as the match itself.</summary>
    [HttpGet("{matchId:long}/remediation")]
    public async Task<ActionResult<CveRemediationDto>> GetRemediation(long matchId, CancellationToken ct = default)
    {
        var scope = await FindReadableAsync(matchId, ct);
        return scope is null ? NotFound() : Ok(await remediation.GetAsync(scope, ct));
    }

    /// <summary>Prepares the fix of a match. The caller is checked before any repository is read: a match it may not read is a 404, one it may
    /// read without write access to its project a 403. 201 for a new attempt, 200 for the one under way, 422 with the command to run
    /// and its reason when the platform cannot do it.</summary>
    [HttpPost("{matchId:long}/remediation")]
    public async Task<ActionResult<CveRemediationDto>> RequestRemediation(long matchId, CancellationToken ct = default)
    {
        var scope = await FindReadableAsync(matchId, ct);
        if (scope is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, scope.ProjectId, Permission.Write, ct)) return Forbid();
        var outcome = await remediation.RequestAsync(scope, User, ct);
        return outcome.Answer switch
        {
            CveRemediationAnswer.Started => StatusCode(StatusCodes.Status201Created, outcome.Remediation),
            CveRemediationAnswer.AlreadyActive => Ok(outcome.Remediation),
            _ => UnprocessableEntity(outcome.Remediation)
        };
    }

    private async Task<CveRemediationScope?> FindReadableAsync(long matchId, CancellationToken ct)
    {
        var organizationIds = User.IsInRole(RoleNames.Admin) ? null : await authz.GetUserOrganizationIdsAsync(User, ct);
        var readableProjectIds = await authz.GetAccessibleResourceIdsAsync(User, ResourceType.Project, Permission.Read, ct);
        return await remediation.FindScopeAsync(matchId, organizationIds, readableProjectIds, ct);
    }
}
