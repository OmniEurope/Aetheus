// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Shared;

/// <summary>An organization's exclusion rules of the system logs (R-28): the lines they match move to the
/// second tab of the logs page.</summary>
public sealed class SystemLogRulesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<List<SystemLogExclusionRuleDto>> ListAsync(int organizationId, CancellationToken ct = default) =>
        await GetJsonAsync<List<SystemLogExclusionRuleDto>>($"api/system-log-rules?organizationId={organizationId}", ct) ?? [];

    /// <summary>Creates the rule; a 409 (the rule exists) is read from the outcome's message.</summary>
    public Task<ApiOutcome<SystemLogExclusionRuleDto, ApiError>> CreateAsync(
        int organizationId, SaveSystemLogExclusionRuleRequest request, CancellationToken ct = default) =>
        SendForOutcomeAsync<SystemLogExclusionRuleDto, ApiError>(
            HttpMethod.Post, $"api/system-log-rules?organizationId={organizationId}", request, ct, callerHandlesConflict: true);

    public Task<ApiOutcome<SystemLogExclusionRuleDto, ApiError>> SetEnabledAsync(
        int organizationId, int ruleId, bool enabled, CancellationToken ct = default) =>
        PutForOutcomeAsync<SystemLogExclusionRuleDto, ApiError>(
            $"api/system-log-rules/{ruleId}/enabled?organizationId={organizationId}",
            new SetSystemLogExclusionRuleEnabledRequest(enabled), ct);

    public Task<ApiStatus> DeleteAsync(int organizationId, int ruleId, CancellationToken ct = default) =>
        base.DeleteAsync($"api/system-log-rules/{ruleId}?organizationId={organizationId}", ct);
}
