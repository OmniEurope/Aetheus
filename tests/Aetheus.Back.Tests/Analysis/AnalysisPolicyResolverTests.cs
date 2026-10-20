// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisPolicyResolverTests
{
    [Fact]
    public void Resolve_ProjectPolicyOverridesOrganizationGlobalAndSystemPolicy()
    {
        var global = Policy(1, "quality.coverage.line", 70);
        var organization = Policy(2, "quality.coverage.line", 80, organizationId: 10);
        var project = Policy(3, "quality.coverage.line", 85, organizationId: 10, projectId: 20);

        var resolved = AnalysisPolicyResolver.Resolve(10, 20, [global, organization, project]);
        var coverage = Assert.Single(resolved, item => item.Policy.PolicyKey == "quality.coverage.line");

        Assert.Same(project, coverage.Policy);
        Assert.Equal(AnalysisPolicyScope.Project, coverage.Scope);
        Assert.True(coverage.IsOverride);
        Assert.False(coverage.IsInherited);
        Assert.Equal(AnalysisPolicyScope.Organization, coverage.OverriddenScope);
        Assert.True(coverage.IsEffective);
    }

    [Fact]
    public void Resolve_DisabledProjectOverrideMasksInheritedPolicy()
    {
        var organization = Policy(2, "quality.coverage.line", 80, organizationId: 10);
        var project = Policy(3, "quality.coverage.line", 80, organizationId: 10, projectId: 20);
        project.Enabled = false;

        var resolved = AnalysisPolicyResolver.Resolve(10, 20, [organization, project]);
        var coverage = Assert.Single(resolved, item => item.Policy.PolicyKey == "quality.coverage.line");

        Assert.Same(project, coverage.Policy);
        Assert.False(coverage.IsEffective);
        Assert.True(coverage.IsOverride);
        Assert.Equal(AnalysisPolicyScope.Organization, coverage.OverriddenScope);
    }

    [Fact]
    public void Resolve_OrganizationViewMarksGlobalRuleAsInherited()
    {
        var global = Policy(1, "custom.global", 1);

        var resolved = AnalysisPolicyResolver.Resolve(10, null, [global]);
        var policy = Assert.Single(resolved, item => item.Policy.PolicyKey == "custom.global");

        Assert.Equal(AnalysisPolicyScope.Global, policy.Scope);
        Assert.True(policy.IsInherited);
        Assert.False(policy.IsOverride);
    }

    [Fact]
    public void Resolve_ExposesVersionedSystemDefaults()
    {
        var resolved = AnalysisPolicyResolver.Resolve(null, null, []);

        Assert.NotEmpty(resolved);
        Assert.All(resolved, item =>
        {
            Assert.Equal(AnalysisPolicyScope.System, item.Scope);
            Assert.Equal(1, item.Policy.Version);
            Assert.True(item.IsInherited);
            Assert.True(item.IsEffective);
        });
    }

    [Fact]
    public void Resolve_GlobalRuleCanOverrideSystemDefault()
    {
        var global = Policy(1, "quality.coverage.line", 82);

        var resolved = AnalysisPolicyResolver.Resolve(null, null, [global]);
        var coverage = Assert.Single(resolved, item => item.Policy.PolicyKey == "quality.coverage.line");

        Assert.Same(global, coverage.Policy);
        Assert.Equal(AnalysisPolicyScope.Global, coverage.Scope);
        Assert.True(coverage.IsOverride);
        Assert.Equal(AnalysisPolicyScope.System, coverage.OverriddenScope);
    }

    [Fact]
    public void PolicyKey_UsesStableExistingKeyAndValidatesExplicitKey()
    {
        Assert.Equal("quality.coverage.line", AnalysisPolicyKey.Resolve(null, "Quality coverage line"));
        Assert.Equal("custom.coverage", AnalysisPolicyKey.Resolve("custom.coverage", "Ignored"));
        Assert.Throws<BadRequestException>(() => AnalysisPolicyKey.Resolve("Invalid Key", "Ignored"));
        Assert.Throws<BadRequestException>(() => AnalysisPolicyKey.Resolve("-invalid", "Ignored"));
        Assert.Throws<BadRequestException>(() => AnalysisPolicyKey.Resolve("invalid-", "Ignored"));
    }

    private static AnalysisPolicy Policy(
        int id,
        string key,
        double threshold,
        int? organizationId = null,
        int? projectId = null) => new()
        {
            Id = id,
            OrganizationId = organizationId,
            ProjectId = projectId,
            PolicyKey = key,
            Name = key,
            MetricKey = "coverage.line.percent",
            Operator = AnalysisPolicyOperator.LessThan,
            Threshold = threshold,
            Behavior = AnalysisGateBehavior.Block,
            Enabled = true,
            Version = 1
        };
}
