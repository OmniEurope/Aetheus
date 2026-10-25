// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Recette R-17: a gate violation names the policy it came from by its human name.</summary>
public sealed class AnalysisGatePolicyNamesTests
{
    private static AnalysisRunGateViolationDto Violation(string key, int version = 1) => new()
    {
        PolicyKey = key,
        Version = version,
        Outcome = "Block"
    };

    private static AnalysisPolicy Policy(
        string key, string name, int version = 1, int? organizationId = null, int? projectId = null) => new()
        {
            PolicyKey = key,
            Name = name,
            Version = version,
            OrganizationId = organizationId,
            ProjectId = projectId
        };

    [Fact]
    public void AViolation_TakesTheNameOfItsPolicy()
    {
        var named = AnalysisGatePolicyNames.Apply(
            [Violation("coverage.line.minimum")],
            [Policy("coverage.line.minimum", "Couverture de lignes minimale")]);

        Assert.Equal("Couverture de lignes minimale", Assert.Single(named).PolicyName);
        Assert.Equal("coverage.line.minimum", named[0].PolicyKey);
    }

    [Fact]
    public void AViolationWhosePolicyIsGone_OrHasNoName_KeepsANullName()
    {
        var named = AnalysisGatePolicyNames.Apply(
            [Violation("gone.policy"), Violation("blank.policy")],
            [Policy("blank.policy", "  ")]);

        Assert.All(named, violation => Assert.Null(violation.PolicyName));
    }

    [Fact]
    public void TheSameKeyInSeveralScopes_TakesTheMostSpecificPolicyOfTheViolationsVersion()
    {
        var violation = Violation("coverage.line.minimum", version: 2);

        var named = AnalysisGatePolicyNames.Apply(
            [violation],
            [
                Policy("coverage.line.minimum", "Global", version: 2),
                Policy("coverage.line.minimum", "Organisation", version: 2, organizationId: 4),
                Policy("coverage.line.minimum", "Projet", version: 2, organizationId: 4, projectId: 9),
                Policy("coverage.line.minimum", "Projet, ancienne version", version: 1, organizationId: 4, projectId: 9)
            ]);

        Assert.Equal("Projet", Assert.Single(named).PolicyName);
    }

    [Fact]
    public void AStoredPolicyWithoutAKey_IsMatchedByItsLegacyKey()
    {
        var legacy = Policy(string.Empty, "Ancienne règle");
        legacy.Id = 12;

        var named = AnalysisGatePolicyNames.Apply([Violation("legacy.12")], [legacy]);

        Assert.Equal("Ancienne règle", Assert.Single(named).PolicyName);
    }
}
