// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.SystemLogs;

public sealed class SystemLogExclusionService(
    ISystemLogExclusionRepository repository,
    IAuditService audit) : ISystemLogExclusionService
{
    public async Task<List<SystemLogExclusionRuleDto>> ListAsync(int organizationId, CancellationToken ct = default) =>
        [.. (await repository.ListAsync(organizationId, enabledOnly: false, ct).ConfigureAwait(false)).Select(ToDto)];

    public async Task<List<SystemLogExclusionRuleDto>> ListEnabledAsync(int organizationId, CancellationToken ct = default) =>
        [.. (await repository.ListAsync(organizationId, enabledOnly: true, ct).ConfigureAwait(false)).Select(ToDto)];

    public async Task<SystemLogExclusionRuleDto> CreateAsync(
        int organizationId, SaveSystemLogExclusionRuleRequest request, string? username, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pattern = request.Pattern.Trim();
        var category = request.Category.Trim();
        if (pattern.Length == 0)
            throw new BadRequestException("The pattern of an exclusion rule cannot be empty.");
        if (!await repository.OrganizationExistsAsync(organizationId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Organization {organizationId} not found.");
        if (await repository.ExistsAsync(organizationId, pattern, category, null, ct).ConfigureAwait(false))
            throw new ConflictException("This exclusion rule already exists.");

        var rule = new SystemLogExclusionRule
        {
            OrganizationId = organizationId,
            Pattern = pattern,
            Category = category,
            Enabled = request.Enabled,
            CreatedBy = username
        };
        await repository.AddAsync(rule, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "SystemLogExclusionRule", rule.Id, Describe(rule), ct).ConfigureAwait(false);
        return ToDto(rule);
    }

    public async Task<SystemLogExclusionRuleDto> SetEnabledAsync(
        int organizationId, int ruleId, bool enabled, CancellationToken ct = default)
    {
        var rule = await FindAsync(organizationId, ruleId, ct).ConfigureAwait(false);
        rule.Enabled = enabled;
        await repository.SaveAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "SystemLogExclusionRule", rule.Id, $"{Describe(rule)}: {(enabled ? "on" : "off")}", ct).ConfigureAwait(false);
        return ToDto(rule);
    }

    public async Task DeleteAsync(int organizationId, int ruleId, CancellationToken ct = default)
    {
        var rule = await FindAsync(organizationId, ruleId, ct).ConfigureAwait(false);
        await repository.DeleteAsync(rule, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "SystemLogExclusionRule", ruleId, Describe(rule), ct).ConfigureAwait(false);
    }

    private async Task<SystemLogExclusionRule> FindAsync(int organizationId, int ruleId, CancellationToken ct) =>
        await repository.FindAsync(organizationId, ruleId, ct).ConfigureAwait(false)
        ?? throw new NotFoundException($"Exclusion rule {ruleId} not found.");

    private static string Describe(SystemLogExclusionRule rule) =>
        rule.Category.Length == 0 ? rule.Pattern : $"{rule.Pattern} ({rule.Category})";

    private static SystemLogExclusionRuleDto ToDto(SystemLogExclusionRule rule) =>
        new(rule.Id, rule.OrganizationId, rule.Pattern, rule.Category, rule.Enabled, rule.CreatedBy, rule.CreatedAt);
}
