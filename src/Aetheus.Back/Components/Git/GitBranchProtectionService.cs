// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Git;

public sealed class GitBranchProtectionService(
    IGitLightRepository repository,
    IGitLightCliService cli,
    IAuditService audit,
    IOptions<GitLightOptions> options)
{
    private readonly GitLightOptions _options = options.Value;

    public async Task<List<BranchProtectionRuleDto>> GetRulesAsync(
        int repoId, CancellationToken ct = default)
    {
        var rules = await repository.GetBranchProtectionRulesAsync(repoId, ct).ConfigureAwait(false);
        return rules.Select(GitLightMapper.MapProtectionRuleToDto).ToList();
    }

    public async Task<BranchProtectionRuleDto> CreateRuleAsync(
        int repoId, CreateBranchProtectionRuleRequest request, CancellationToken ct = default)
    {
        var rule = new BranchProtectionRule
        {
            GitInternalRepoId = repoId,
            Pattern = request.Pattern,
            PreventDeletion = request.PreventDeletion,
            PreventForcePush = request.PreventForcePush,
            ProtectionLevel = request.ProtectionLevel
        };

        await repository.AddBranchProtectionRuleAsync(rule, ct).ConfigureAwait(false);
        await audit.LogAsync(
            "Created", "BranchProtectionRule", rule.Id,
            $"{request.Pattern} (repo {repoId})", ct).ConfigureAwait(false);
        await SyncConfigAsync(repoId, ct).ConfigureAwait(false);
        return GitLightMapper.MapProtectionRuleToDto(rule);
    }

    public async Task<BranchProtectionRuleDto?> UpdateRuleAsync(
        int repoId, int ruleId, UpdateBranchProtectionRuleRequest request,
        CancellationToken ct = default)
    {
        var rule = await repository.FindBranchProtectionRuleAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null || rule.GitInternalRepoId != repoId) return null;

        rule.PreventDeletion = request.PreventDeletion;
        rule.PreventForcePush = request.PreventForcePush;
        rule.ProtectionLevel = request.ProtectionLevel;
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync(
            "Updated", "BranchProtectionRule", ruleId, rule.Pattern, ct).ConfigureAwait(false);
        await SyncConfigAsync(rule.GitInternalRepoId, ct).ConfigureAwait(false);
        return GitLightMapper.MapProtectionRuleToDto(rule);
    }

    public async Task<bool> DeleteRuleAsync(int repoId, int ruleId, CancellationToken ct = default)
    {
        var rule = await repository.FindBranchProtectionRuleAsync(ruleId, ct).ConfigureAwait(false);
        // The caller is authorized on repoId, so a rule belonging to another repository must be
        // refused here rather than deleted: the id alone carried no ownership, which let an admin of
        // one repository drop the force-push and pull-request protections of any other.
        if (rule is null || rule.GitInternalRepoId != repoId) return false;

        await repository.RemoveBranchProtectionRuleAsync(rule, ct).ConfigureAwait(false);
        await audit.LogAsync(
            "Deleted", "BranchProtectionRule", ruleId, rule.Pattern, ct).ConfigureAwait(false);
        await SyncConfigAsync(rule.GitInternalRepoId, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsProtectedAsync(
        int repoId, string branchName, CancellationToken ct = default)
    {
        var rules = await repository.GetBranchProtectionRulesAsync(repoId, ct).ConfigureAwait(false);
        return rules.Any(rule => GitBranchPatternMatcher.Matches(branchName, rule.Pattern));
    }

    private async Task SyncConfigAsync(int repoId, CancellationToken ct)
    {
        var entity = await repository.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return;

        var diskPath = GitRepoPathResolver.TryResolve(
            _options.RepositoriesPath, entity.ProjectId, entity.Slug)
            ?? throw new BadRequestException("Invalid repository path.");
        var rules = await repository.GetBranchProtectionRulesAsync(repoId, ct).ConfigureAwait(false);
        var data = rules
            .Select(rule => (rule.Pattern, rule.PreventDeletion, rule.PreventForcePush))
            .ToList();
        await cli.WriteProtectionConfigAsync(diskPath, data, ct).ConfigureAwait(false);
    }
}
