// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisService(
    IAnalysisRepository repository,
    AnalysisIngestGate ingestGate,
    AnalysisPolicyEngine policyEngine,
    TimeProvider timeProvider,
    IAuditService audit,
    IDbTransactionScope transaction,
    IEntityChangeNotifier notifier,
    INotificationService notifications,
    IDependencyTrackOutbox dependencyTrackOutbox,
    IOptions<AnalysisPlatformOptions> platformOptions,
    IOptions<DependencyTrackOptions> dependencyTrackOptions,
    IAnalysisPublicationSignal? publicationSignal = null) : IAnalysisService
{
    private readonly DependencyTrackOptions _dependencyTrackOptions = dependencyTrackOptions.Value;
    private readonly AnalysisPolicyRevisionManager _policyRevisions =
        new(repository, timeProvider, audit);
    private readonly AnalysisReportPublisher _publisher = new(
        repository,
        ingestGate,
        policyEngine,
        timeProvider,
        transaction,
        notifier,
        notifications,
        dependencyTrackOutbox,
        platformOptions);

    public async Task<AnalysisReportDto> PublishReportAsync(
        int runId,
        PublishAnalysisReportRequest request,
        CancellationToken ct = default)
    {
        // R1-12: the run page shows the publication while it lasts; the end is announced even when it fails.
        if (publicationSignal is not null)
            await publicationSignal.AnnounceAsync(runId, true, ct).ConfigureAwait(false);
        try
        {
            return await _publisher.PublishAsync(runId, request, ct).ConfigureAwait(false);
        }
        finally
        {
            if (publicationSignal is not null)
                await publicationSignal.AnnounceAsync(runId, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<AnalysisRunGateDto> GetRunGateAsync(
        int runId,
        string? scope = null,
        CancellationToken ct = default)
    {
        if (await repository.GetRunContextAsync(runId, ct).ConfigureAwait(false) is null)
            throw new NotFoundException("Pipeline run not found or not owned by a project.");
        var gate = await repository.GetRunGateAsync(runId, scope, ct).ConfigureAwait(false);
        return await DependencyTrackGateVerdict.ApplyAsync(
            gate, runId, scope, _dependencyTrackOptions, repository, ct).ConfigureAwait(false);
    }

    public Task<AnalysisRunGateDto> GetRunGateAsync(
        int runId,
        CancellationToken ct = default) =>
        GetRunGateAsync(runId, null, ct);

    public async Task<PaginatedResult<AnalysisFindingDto>> GetFindingsAsync(
        int projectId,
        AnalysisFindingPaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repository.GetFindingsAsync(projectId, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisFindingDto>
        {
            Items = items.Select(AnalysisMapper.ToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AnalysisFindingDto?> GetFindingAsync(int findingId, CancellationToken ct = default)
    {
        var finding = await repository.GetFindingAsync(findingId, ct).ConfigureAwait(false);
        return finding is null ? null : AnalysisMapper.ToDto(finding);
    }

    public async Task<List<AnalysisFindingOccurrenceDto>> GetFindingOccurrencesAsync(
        int findingId,
        int take,
        CancellationToken ct = default) =>
        (await repository.GetFindingOccurrencesAsync(findingId, take, ct).ConfigureAwait(false))
            .Select(AnalysisMapper.ToDto)
            .ToList();

    public Task<int?> GetFindingProjectIdAsync(int findingId, CancellationToken ct = default)
        => repository.GetFindingProjectIdAsync(findingId, ct);

    public async Task<PaginatedResult<AnalysisReportDto>> GetReportsAsync(
        int projectId,
        PaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repository.GetReportsAsync(projectId, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisReportDto>
        {
            Items = items.Select(AnalysisMapper.ToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<AnalysisPortfolioRowDto>> GetPortfolioAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        AnalysisPortfolioPaginationRequest request,
        CancellationToken ct = default)
    {
        if (request.From.HasValue && request.To.HasValue && request.From.Value > request.To.Value)
            throw new BadRequestException("Analysis period start must precede its end.");
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repository.GetPortfolioAsync(accessibleProjectIds, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisPortfolioRowDto>
        {
            Items = items.Select(AnalysisMapper.ToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<AnalysisProjectSummaryDto> GetProjectSummaryAsync(int projectId, CancellationToken ct = default)
        => repository.GetProjectSummaryAsync(projectId, ct);

    public Task<List<AnalysisPortfolioProjectDto>> GetPortfolioProjectsAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default) =>
        repository.GetPortfolioProjectsAsync(accessibleProjectIds, ct);

    public Task<AnalysisPortfolioFilterValuesDto> GetPortfolioFilterValuesAsync(
        IReadOnlyCollection<int>? accessibleProjectIds,
        CancellationToken ct = default) =>
        repository.GetPortfolioFilterValuesAsync(accessibleProjectIds, ct);

    public async Task<List<AnalysisPolicyDto>> GetPoliciesAsync(int projectId, CancellationToken ct = default)
    {
        var organizationId = await RequireProjectOrganizationAsync(projectId, ct).ConfigureAwait(false);
        var policies = await repository.GetPoliciesAsync(projectId, ct).ConfigureAwait(false);
        return AnalysisPolicyResolver.Resolve(organizationId, projectId, policies)
            .Select(AnalysisMapper.ToDto)
            .ToList();
    }

    public async Task<AnalysisPolicyDto> CreatePolicyAsync(
        int projectId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default)
    {
        var organizationId = await RequireProjectOrganizationAsync(projectId, ct).ConfigureAwait(false);
        AnalysisPolicyRequestValidator.Validate(request);
        return await CreatePolicyCoreAsync(
            organizationId, projectId, request, $"project={projectId}", ct).ConfigureAwait(false);
    }

    public async Task<AnalysisPolicyDto> UpdatePolicyAsync(
        int projectId,
        int policyId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default)
    {
        var policy = await GetPolicyForUpdateAsync(policyId, request, ct).ConfigureAwait(false);
        if (policy.ProjectId != projectId)
            throw new NotFoundException("Analysis policy not found for this project.");
        return await UpdatePolicyCoreAsync(
            policy, projectId, request, $"project={projectId}", ct).ConfigureAwait(false);
    }

    public async Task<List<AnalysisPolicyDto>> GetScopedPoliciesAsync(
        int? organizationId,
        CancellationToken ct = default)
    {
        if (organizationId.HasValue && !await repository.OrganizationExistsAsync(organizationId.Value, ct).ConfigureAwait(false))
            throw new NotFoundException("Organization not found.");
        var policies = await repository.GetPoliciesAsyncForConfigurationAsync(
            organizationId,
            ct).ConfigureAwait(false);
        return AnalysisPolicyResolver.Resolve(organizationId, null, policies)
            .Select(AnalysisMapper.ToDto)
            .ToList();
    }

    public async Task<AnalysisPolicyDto> CreateScopedPolicyAsync(
        int? organizationId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default)
    {
        AnalysisPolicyRequestValidator.Validate(request);
        if (organizationId.HasValue && !await repository.OrganizationExistsAsync(organizationId.Value, ct).ConfigureAwait(false))
            throw new NotFoundException("Organization not found.");
        var auditScope = organizationId.HasValue ? $"scope=organization:{organizationId}" : "scope=global";
        return await CreatePolicyCoreAsync(
            organizationId, projectId: null, request, auditScope, ct).ConfigureAwait(false);
    }

    public async Task<AnalysisPolicyDto> UpdateScopedPolicyAsync(
        int? organizationId,
        int policyId,
        UpsertAnalysisPolicyRequest request,
        CancellationToken ct = default)
    {
        var policy = await GetPolicyForUpdateAsync(policyId, request, ct).ConfigureAwait(false);
        if (policy.ProjectId.HasValue || policy.OrganizationId != organizationId)
            throw new NotFoundException("Analysis policy not found in the selected scope.");
        var auditScope = organizationId.HasValue ? $"scope=organization:{organizationId}" : "scope=global";
        return await UpdatePolicyCoreAsync(
            policy, projectId: null, request, auditScope, ct).ConfigureAwait(false);
    }

    // Validates the request before loading, so an invalid update never reads the policy.
    private async Task<AnalysisPolicy> GetPolicyForUpdateAsync(
        int policyId, UpsertAnalysisPolicyRequest request, CancellationToken ct)
    {
        AnalysisPolicyRequestValidator.Validate(request);
        return await repository.GetPolicyAsync(policyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Analysis policy not found.");
    }

    private async Task<AnalysisPolicyDto> CreatePolicyCoreAsync(
        int? organizationId,
        int? projectId,
        UpsertAnalysisPolicyRequest request,
        string auditScope,
        CancellationToken ct)
    {
        var existing = await repository.GetPoliciesForScopeAsync(organizationId, projectId, ct).ConfigureAwait(false);
        AnalysisPolicyMutationRules.EnsureUnique(existing, request, null, null);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var policy = new AnalysisPolicy
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            CreatedAt = now,
            UpdatedAt = now
        };
        AnalysisPolicyRequestValidator.Apply(policy, request);
        await repository.SavePolicyAsync(policy, ct).ConfigureAwait(false);
        await repository.SavePolicyRevisionAsync(policy, now, ct).ConfigureAwait(false);
        await audit.LogAsync(
            "AnalysisPolicyCreated",
            nameof(AnalysisPolicy),
            policy.Id,
            $"{auditScope};version={policy.Version}",
            ct).ConfigureAwait(false);
        return AnalysisMapper.ToDto(policy);
    }

    private async Task<AnalysisPolicyDto> UpdatePolicyCoreAsync(
        AnalysisPolicy policy,
        int? projectId,
        UpsertAnalysisPolicyRequest request,
        string auditScope,
        CancellationToken ct)
    {
        var existing = await repository.GetPoliciesForScopeAsync(policy.OrganizationId, projectId, ct)
            .ConfigureAwait(false);
        AnalysisPolicyMutationRules.EnsureUnique(existing, request, policy.Id, policy.PolicyKey);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await AnalysisPolicyVersionWriter.SaveNewVersionAsync(repository, policy, request, now, ct)
            .ConfigureAwait(false);
        await audit.LogAsync(
            "AnalysisPolicyUpdated",
            nameof(AnalysisPolicy),
            policy.Id,
            $"{auditScope};version={policy.Version}",
            ct).ConfigureAwait(false);
        return AnalysisMapper.ToDto(policy);
    }

    public async Task<List<AnalysisPolicyDto>> ApplyPolicyBatchAsync(
        int? organizationId,
        int? projectId,
        ApplyAnalysisPolicyBatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count is < 1 or > 100)
            throw new BadRequestException("An analysis policy batch must contain between 1 and 100 items.");

        var effectiveOrganizationId = projectId.HasValue
            ? await RequireProjectOrganizationAsync(projectId.Value, ct).ConfigureAwait(false)
            : organizationId;
        if (!projectId.HasValue && effectiveOrganizationId.HasValue
            && !await repository.OrganizationExistsAsync(effectiveOrganizationId.Value, ct).ConfigureAwait(false))
            throw new NotFoundException("Organization not found.");

        // Validate scope membership, immutable keys, and the complete prospective key/name set
        // before the first repository write. The transaction remains the final safety net for a
        // concurrent conflict, but deterministic request errors never create partial audit/revision work.
        var existing = await repository.GetPoliciesForScopeAsync(
            effectiveOrganizationId, projectId, ct).ConfigureAwait(false);
        AnalysisPolicyMutationRules.ValidateBatch(existing, request.Items);

        async Task<List<AnalysisPolicyDto>> ApplyAsync()
        {
            var saved = new List<AnalysisPolicyDto>(request.Items.Count);
            foreach (var item in request.Items)
            {
                saved.Add(projectId.HasValue
                    ? item.PolicyId.HasValue
                        ? await UpdatePolicyAsync(projectId.Value, item.PolicyId.Value, item.Policy, ct)
                            .ConfigureAwait(false)
                        : await CreatePolicyAsync(projectId.Value, item.Policy, ct).ConfigureAwait(false)
                    : item.PolicyId.HasValue
                        ? await UpdateScopedPolicyAsync(
                            effectiveOrganizationId, item.PolicyId.Value, item.Policy, ct).ConfigureAwait(false)
                        : await CreateScopedPolicyAsync(effectiveOrganizationId, item.Policy, ct)
                            .ConfigureAwait(false));
            }
            return saved;
        }

        return transaction.IsRelational
            ? await transaction.ExecuteInTransactionAsync(ApplyAsync, ct).ConfigureAwait(false)
            : await ApplyAsync().ConfigureAwait(false);
    }

    public async Task<AnalysisPolicySetPreviewDto> PreviewPolicySetAsync(
        int? organizationId,
        int? projectId,
        PreviewAnalysisPolicySetRequest request,
        CancellationToken ct = default)
    {
        var effectiveOrganizationId = projectId.HasValue
            ? await RequireProjectOrganizationAsync(projectId.Value, ct).ConfigureAwait(false)
            : organizationId;
        if (!projectId.HasValue && effectiveOrganizationId.HasValue
            && !await repository.OrganizationExistsAsync(effectiveOrganizationId.Value, ct).ConfigureAwait(false))
            throw new NotFoundException("Organization not found.");

        var policies = projectId.HasValue
            ? await repository.GetPoliciesAsync(projectId.Value, ct).ConfigureAwait(false)
            : await repository.GetPoliciesAsyncForConfigurationAsync(effectiveOrganizationId, ct).ConfigureAwait(false);
        AnalysisPolicyMutationRules.ApplyPreviewCandidate(
            policies, effectiveOrganizationId, projectId, request);

        var resolved = AnalysisPolicyResolver.Resolve(effectiveOrganizationId, projectId, policies);
        var snapshot = string.Join('\n', resolved.Select(AnalysisPolicySnapshot.Serialize));
        var latestRunId = projectId.HasValue
            ? await repository.GetLatestProjectRunIdAsync(projectId.Value, ct).ConfigureAwait(false)
            : null;
        return new AnalysisPolicySetPreviewDto
        {
            OrganizationId = effectiveOrganizationId,
            ProjectId = projectId,
            SnapshotHash = AnalysisPolicySnapshot.Hash(snapshot),
            Policies = resolved.Select(AnalysisMapper.ToDto).ToList(),
            ExpectedProducers = AnalysisPolicyPreviewExplainer.ExpectedProducers(resolved),
            Conflicts = AnalysisPolicyPreviewExplainer.Conflicts(policies),
            LatestRunGate = latestRunId.HasValue
                ? await repository.GetRunGateAsync(latestRunId.Value, null, ct).ConfigureAwait(false)
                : null
        };
    }

    public Task<List<AnalysisPolicyRevisionDto>> GetPolicyRevisionsAsync(
        int? organizationId,
        int? projectId,
        int policyId,
        CancellationToken ct = default) =>
        _policyRevisions.GetAsync(organizationId, projectId, policyId, ct);

    public Task<AnalysisPolicyDto> RollbackPolicyAsync(
        int? organizationId,
        int? projectId,
        int policyId,
        int version,
        CancellationToken ct = default) =>
        _policyRevisions.RollbackAsync(organizationId, projectId, policyId, version, ct);

    public async Task<List<AnalysisPolicyExceptionDto>> GetExceptionsAsync(int projectId, CancellationToken ct = default) =>
        (await repository.GetExceptionsAsync(projectId, ct).ConfigureAwait(false)).Select(AnalysisMapper.ToDto).ToList();

    public async Task<AnalysisPolicyExceptionDto> CreateExceptionAsync(
        int projectId,
        CreateAnalysisPolicyExceptionRequest request,
        string actor,
        CancellationToken ct = default)
    {
        var organizationId = await RequireProjectOrganizationAsync(projectId, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (request.ExpiresAt <= now)
            throw new BadRequestException("An analysis exception must expire in the future.");
        if (!request.AnalysisFindingId.HasValue
            && string.IsNullOrWhiteSpace(request.Fingerprint)
            && string.IsNullOrWhiteSpace(request.RuleId)
            && string.IsNullOrWhiteSpace(request.ScannerKey)
            && !request.Category.HasValue)
            throw new BadRequestException("An analysis exception requires a finding, fingerprint, rule, scanner or category scope.");
        if (request.AnalysisFindingId.HasValue)
        {
            var finding = await repository.GetTrackedFindingAsync(request.AnalysisFindingId.Value, ct).ConfigureAwait(false);
            if (finding?.ProjectId != projectId)
                throw new BadRequestException("The scoped finding does not belong to this project.");
        }
        if (request.AnalysisPolicyId.HasValue)
        {
            var policy = await repository.GetPolicyAsync(request.AnalysisPolicyId.Value, ct).ConfigureAwait(false);
            if (policy is null || (policy.OrganizationId.HasValue && policy.OrganizationId != organizationId)
                || (policy.ProjectId.HasValue && policy.ProjectId != projectId))
                throw new BadRequestException("The scoped policy is not applicable to this project.");
        }

        var exception = new AnalysisPolicyException
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            AnalysisPolicyId = request.AnalysisPolicyId,
            AnalysisFindingId = request.AnalysisFindingId,
            Fingerprint = Normalize(request.Fingerprint),
            RuleId = Normalize(request.RuleId),
            ScannerKey = Normalize(request.ScannerKey),
            Category = request.Category,
            BranchPattern = Normalize(request.BranchPattern),
            EnvironmentPattern = Normalize(request.EnvironmentPattern),
            Reason = request.Reason.Trim(),
            CreatedByUsername = actor,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now
        };
        await repository.SaveExceptionAsync(exception, ct).ConfigureAwait(false);
        await audit.LogAsync("AnalysisExceptionCreated", nameof(AnalysisPolicyException), exception.Id,
            $"project={projectId};expires={exception.ExpiresAt:O}", ct).ConfigureAwait(false);
        return AnalysisMapper.ToDto(exception);
    }

    public async Task RevokeExceptionAsync(
        int projectId,
        int exceptionId,
        string actor,
        CancellationToken ct = default)
    {
        var exception = await repository.GetExceptionAsync(exceptionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Analysis exception not found.");
        if (exception.ProjectId != projectId)
            throw new NotFoundException("Analysis exception not found for this project.");
        if (!exception.RevokedAt.HasValue)
        {
            exception.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
            await repository.SaveExceptionAsync(exception, ct).ConfigureAwait(false);
            await audit.LogAsync("AnalysisExceptionRevoked", nameof(AnalysisPolicyException), exception.Id,
                $"project={projectId};actor={actor}", ct).ConfigureAwait(false);
        }
    }

    public async Task<PaginatedResult<AnalysisMetricDto>> GetMetricsAsync(
        int projectId,
        AnalysisMetricPaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repository.GetMetricsAsync(projectId, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisMetricDto>
        {
            Items = items.Select(AnalysisMapper.ToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<AnalysisComponentDto>> GetComponentsAsync(
        int projectId,
        AnalysisComponentPaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repository.GetComponentsAsync(projectId, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisComponentDto>
        {
            Items = items.Select(AnalysisMapper.ToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AnalysisTrackingStatusDto?> GetTrackingStatusAsync(int projectId, CancellationToken ct = default)
    {
        var tracking = await repository.GetTrackingProjectAsync(projectId, ct).ConfigureAwait(false);
        return tracking is null ? null : new AnalysisTrackingStatusDto
        {
            ProjectId = tracking.ProjectId,
            Provider = tracking.Provider,
            Active = tracking.Active,
            SyncStatus = tracking.SyncStatus,
            LastError = tracking.LastError,
            LastKnownVulnerabilityCount = tracking.LastKnownVulnerabilityCount,
            LastSyncAt = tracking.LastSyncAt
        };
    }

    public async Task<PaginatedResult<AnalysisVulnerabilityObservationDto>> GetVulnerabilityObservationsAsync(
        int projectId,
        PaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repository.GetVulnerabilityObservationsAsync(projectId, request, ct).ConfigureAwait(false);
        return new PaginatedResult<AnalysisVulnerabilityObservationDto>
        {
            Items = items.Select(item => new AnalysisVulnerabilityObservationDto
            {
                Id = item.Id,
                AnalysisReportId = item.AnalysisReportId,
                VulnerabilityId = item.VulnerabilityId,
                ComponentName = item.ComponentName,
                ComponentVersion = item.ComponentVersion,
                PackageUrl = item.PackageUrl,
                Severity = item.Severity,
                Status = item.Status,
                Source = item.Source,
                IsContinuous = item.IsContinuous,
                ObservedAt = item.ObservedAt
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    private async Task<int> RequireProjectOrganizationAsync(int projectId, CancellationToken ct) =>
        await repository.GetProjectOrganizationIdAsync(projectId, ct).ConfigureAwait(false)
        ?? throw new NotFoundException("Project not found.");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
