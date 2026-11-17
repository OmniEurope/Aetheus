// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

// Audit F-SEC-08: per-step secret scoping (now applied to the FIRST dispatch too, consistent with later
// stages). These tests lock the documented behaviour - INCLUDING the sharp edge that a secret NOT named in
// the step's shell is removed (a step consuming a secret "by convention" must reference its key name, or opt
// in explicitly - tracked as a follow-up). Locking it prevents silent drift and documents the contract.
public class SecretScopingTests
{
    [Fact]
    public void ScopeStepEnvironment_KeepsReferencedSecret_RemovesUnreferenced_KeepsNonSecret()
    {
        var legVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["MY_SECRET"] = "s", ["UNUSED_SECRET"] = "u", ["PLAIN_VAR"] = "p" };
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MY_SECRET", "UNUSED_SECRET" };
        var step = new PipelineStepDefinition { Name = "build", Shell = "echo using $MY_SECRET now" };

        var scoped = PipelineRunHelpers.ScopeStepEnvironment(legVars, secretKeys, step);

        Assert.True(scoped.ContainsKey("MY_SECRET"));       // referenced in the shell -> kept
        Assert.False(scoped.ContainsKey("UNUSED_SECRET"));  // secret not referenced -> dropped (the sharp edge)
        Assert.True(scoped.ContainsKey("PLAIN_VAR"));       // non-secret -> always kept
    }

    [Fact]
    public void ScopeStepEnvironment_EmptySecretKeys_KeepsAll()
    {
        var legVars = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };

        var scoped = PipelineRunHelpers.ScopeStepEnvironment(
            legVars, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new PipelineStepDefinition { Name = "s", Shell = "echo hi" });

        Assert.Equal(2, scoped.Count); // no secret keys => no scoping
    }

    [Fact]
    public void ScopeStepEnvironment_CheckoutStep_KeepsGitCredsEvenWhenUnreferenced()
    {
        var legVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GIT_PASSWORD"] = "p" };
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GIT_PASSWORD" };
        var step = new PipelineStepDefinition { Name = "Clone Repository", Shell = "", Checkout = true };

        var scoped = PipelineRunHelpers.ScopeStepEnvironment(legVars, secretKeys, step);

        Assert.True(scoped.ContainsKey("GIT_PASSWORD")); // checkout preamble references the git creds implicitly
    }

    [Fact]
    public void ScopeStepEnvironment_TypedObservabilityPublisher_ReceivesOnlyItsDeclaredSecrets()
    {
        var legVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_PACKAGE_TOKEN"] = "token",
            ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = "password",
            ["AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"] = "fingerprint",
            ["UNRELATED_SECRET"] = "unrelated"
        };
        var secretKeys = legVars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var step = new PipelineStepDefinition { Name = "publish", Type = "publish-observability" };

        var scoped = PipelineRunHelpers.ScopeStepEnvironment(legVars, secretKeys, step);

        // R-57: the registry credential is minted per run by the server; a vault value of that name is no
        // longer forwarded to the step.
        Assert.False(scoped.ContainsKey("AETHEUS_PACKAGE_TOKEN"));
        Assert.True(scoped.ContainsKey("AETHEUS_NUGET_SIGNING_PFX_PASSWORD"));
        Assert.True(scoped.ContainsKey("AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT"));
        Assert.False(scoped.ContainsKey("UNRELATED_SECRET"));
    }
}
