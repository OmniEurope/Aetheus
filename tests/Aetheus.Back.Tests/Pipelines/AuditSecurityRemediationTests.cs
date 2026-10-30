// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Covers the security findings of the 2026-08-20 360 audit that had no test at all. Each test asserts
/// the FIXED behaviour and would fail on the code as it was, which is the only reason these exist:
/// every one of these paths was reachable and silent.
/// </summary>
public class AuditSecurityRemediationTests
{
    // ---- A360-23: condition evidence must redact runtime secrets, not only vault keys ----

    [Fact]
    public void ConditionEvidence_RedactsARuntimeSecretByValue_NotOnlyVaultKeysByName()
    {
        // The bootstrap password carries no vault name, so name-based redaction never saw it and the
        // value was persisted in clear, then served unmasked forever after.
        const string runtimeSecret = "bootstrap-Pa55word-not-a-vault-key";
        var variables = new Dictionary<string, string>
        {
            ["DEPLOYMENT_BOOTSTRAP_PASSWORD"] = runtimeSecret,
            ["BRANCH"] = "main"
        };

        var captured = PipelineConditionEvidence.CaptureVariables(
            "variables['DEPLOYMENT_BOOTSTRAP_PASSWORD'] == '' && variables['BRANCH'] == 'main'",
            variables,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [runtimeSecret]);

        Assert.Equal("[masked]", captured["DEPLOYMENT_BOOTSTRAP_PASSWORD"]);
        Assert.DoesNotContain(runtimeSecret, string.Join('|', captured.Values), StringComparison.Ordinal);
        // A non-secret variable in the same condition must still be readable, or the evidence is useless.
        Assert.Equal("main", captured["BRANCH"]);
    }

    [Fact]
    public void ConditionEvidence_StillRedactsVaultKeysByName_AndKeepsReportingUnsetVariables()
    {
        var captured = PipelineConditionEvidence.CaptureVariables(
            "variables['API_TOKEN'] != '' && variables['MISSING'] == ''",
            new Dictionary<string, string> { ["API_TOKEN"] = "value-from-vault" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "API_TOKEN" });

        Assert.Equal("[masked]", captured["API_TOKEN"]);
        Assert.Equal("[not set]", captured["MISSING"]);
    }

    [Fact]
    public void ConditionEvidence_DoesNotMaskAnUnrelatedValueThatIsMerelySimilar()
    {
        // Redaction is by exact value: masking a prefix match would blank ordinary variables.
        var captured = PipelineConditionEvidence.CaptureVariables(
            "variables['ENV'] == 'prod'",
            new Dictionary<string, string> { ["ENV"] = "prod" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["prod-secret-value"]);

        Assert.Equal("prod", captured["ENV"]);
    }

    // ---- A360-04: the bootstrap variable names must be reserved ----

    [Theory]
    [InlineData("DEPLOYMENT_BOOTSTRAP_PASSWORD")]
    [InlineData("DEPLOYMENT_BOOTSTRAP_USER")]
    [InlineData("deployment_bootstrap_password")]
    [InlineData("BOOTSTRAP_STAMP")]
    public void BootstrapVariableNames_AreReserved_SoAPipelineCannotDeclareTheAdminCredential(string name)
        => Assert.True(PipelineParameterResolver.IsReservedName(name));

    [Theory]
    [InlineData("UPSTREAM_RUN_ID")]
    [InlineData("upstream_chain")]
    [InlineData("UPSTREAM_RELEASE")]
    public void TheParentContextNames_AreReserved_SoARunCannotClaimAParent(string name)
        => Assert.True(PipelineParameterResolver.IsReservedName(name));

    [Theory]
    [InlineData("AETHEUS_SOURCE_COMMIT")]
    [InlineData("BUILD_BUILDID")]
    [InlineData("SYSTEM_STAGE")]
    public void ThePreviouslyReservedPrefixes_StayReserved(string name)
        => Assert.True(PipelineParameterResolver.IsReservedName(name));

    [Theory]
    [InlineData("MY_PARAMETER")]
    [InlineData("BOOTSTRAP_MODE")]
    [InlineData("DEPLOYMENT_TARGET")]
    public void OrdinaryParameterNames_StayAllowed(string name)
        => Assert.False(PipelineParameterResolver.IsReservedName(name));

    // ---- A360-58: a seeded admin login must not inherit the deployment window ----

    [Fact]
    public void SeededAdminLogin_DoesNotInheritTheDeploymentExpiry()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var config = BuildConfig(new()
        {
            ["Auth:AdminPassword"] = "seeded-password",
            ["Auth:DeploymentBootstrapUser"] = "deploy-user@bootstrap.invalid",
            ["Auth:DeploymentBootstrapPassword"] = "deploy-password",
            ["Auth:DeploymentBootstrapExpiresAtUtc"] =
                timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime.ToString("O")
        });

        var match = BootstrapCredentialPolicy.Match(
            new LoginRequest { Email = "admin@aetheus.local", Password = "seeded-password" },
            config, timeProvider, hasDbUsers: false);

        Assert.NotNull(match);
        // The seeded administrator authenticated with Auth:AdminPassword, not with the deployment
        // identity: tying its token to a deployment's clock was an unrelated coupling.
        Assert.Null(match.ExpiresAt);
    }

    [Fact]
    public void DeploymentIdentityLogin_StillCarriesTheDeploymentExpiry()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var expiry = timeProvider.GetUtcNow().AddMinutes(10).UtcDateTime;
        var config = BuildConfig(new()
        {
            ["Auth:AdminPassword"] = "seeded-password",
            ["Auth:DeploymentBootstrapUser"] = "deploy-user@bootstrap.invalid",
            ["Auth:DeploymentBootstrapPassword"] = "deploy-password",
            ["Auth:DeploymentBootstrapExpiresAtUtc"] = expiry.ToString("O")
        });

        var match = BootstrapCredentialPolicy.Match(
            new LoginRequest { Email = "deploy-user@bootstrap.invalid", Password = "deploy-password" },
            config, timeProvider, hasDbUsers: false);

        Assert.NotNull(match);
        Assert.NotNull(match.ExpiresAt);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
