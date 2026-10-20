// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Verifies that each dispatch re-expands declared variables against published stage
/// outputs without expanding unrelated values.</summary>
public class PipelineVariableExpansionTests
{
    private static StepOutputProjection Output(string json, string stage = "Prepare", string step = "prepare")
        => new(stage, step, json);

    private static Dictionary<string, string> Resolved(params (string Key, string Value)[] entries)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries) variables[key] = value;
        return variables;
    }

    [Fact]
    public void ADeclaredValue_ReferencingAPublishedOutput_IsExpandedOnceTheOutputExists()
    {
        var variables = Resolved(("BG_BROWSER_IMAGE", "aetheus-browser-smoke:$(SOURCE_COMMIT)"));

        PipelineVariableExpansion.ApplyStepOutputs(variables, new HashSet<string>(), [Output("""{"SOURCE_COMMIT":"abc123"}""")]);

        Assert.Equal("aetheus-browser-smoke:abc123", variables["BG_BROWSER_IMAGE"]);
        Assert.Equal("abc123", variables["Prepare.prepare.SOURCE_COMMIT"]);
    }

    [Fact]
    public void APendingDefault_TakesTheOutputWhenPublished_AndTheDefaultOtherwise()
    {
        var published = Resolved(("IMAGE", "smoke:$(SOURCE_COMMIT:-latest)"));
        var notPublished = Resolved(("IMAGE", "smoke:$(SOURCE_COMMIT:-latest)"));

        PipelineVariableExpansion.ApplyStepOutputs(published, new HashSet<string>(), [Output("""{"SOURCE_COMMIT":"abc123"}""")]);
        PipelineVariableExpansion.ApplyStepOutputs(notPublished, new HashSet<string>(), []);

        Assert.Equal("smoke:abc123", published["IMAGE"]);
        Assert.Equal("smoke:latest", notPublished["IMAGE"]);
    }

    [Fact]
    public void ADefaultOnARunnerVariable_StaysPendingUntilTheRunnerIsPicked()
    {
        var variables = Resolved(("LABEL", "$(AGENT_NAME:-unknown)"));

        PipelineVariableExpansion.ApplyStepOutputs(variables, new HashSet<string>(), []);

        Assert.Equal("$(AGENT_NAME:-unknown)", variables["LABEL"]);
    }

    [Fact]
    public void AnOutput_StillOverridesADeclaredValue()
    {
        var variables = Resolved(("VERSION", "0.0.0"));

        PipelineVariableExpansion.ApplyStepOutputs(variables, new HashSet<string>(), [Output("""{"VERSION":"1.2.3"}""")]);

        Assert.Equal("1.2.3", variables["VERSION"]);
    }

    [Fact]
    public void TheDeploymentTarget_IsNeitherOverriddenNorReExpanded()
    {
        var variables = Resolved(
            (PipelineDeploymentTargetGuard.TargetVariable, "$(TARGET)"),
            ("OTHER", "x"));

        PipelineVariableExpansion.ApplyStepOutputs(
            variables, new HashSet<string>(),
            [Output($$"""{"{{PipelineDeploymentTargetGuard.TargetVariable}}":"local","TARGET":"local"}""")]);

        Assert.Equal("$(TARGET)", variables[PipelineDeploymentTargetGuard.TargetVariable]);
        Assert.Equal("local", variables[$"Prepare.prepare.{PipelineDeploymentTargetGuard.TargetVariable}"]);
    }

    /// <summary>
    /// A secret is scoped per step (only the steps that mention it receive it). Substituting its value
    /// into an ordinary variable would hand it to every step; re-expanding the secret itself would
    /// alter opaque data.
    /// </summary>
    [Fact]
    public void Secrets_AreNeitherReExpandedNorSubstitutedIntoOtherVariables()
    {
        var variables = Resolved(
            ("DB_PASSWORD", "p$(SOURCE_COMMIT)"),
            ("CONNECTION", "user:$(DB_PASSWORD)@$(SOURCE_COMMIT)"));
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DB_PASSWORD" };

        PipelineVariableExpansion.ApplyStepOutputs(variables, secrets, [Output("""{"SOURCE_COMMIT":"abc"}""")]);

        Assert.Equal("p$(SOURCE_COMMIT)", variables["DB_PASSWORD"]);
        Assert.Equal("user:$(DB_PASSWORD)@abc", variables["CONNECTION"]);
    }

    /// <summary>A published value is data a step produced, not a template.</summary>
    [Fact]
    public void AnOutputValue_IsNotItselfExpanded()
    {
        var variables = Resolved(("BUILD_BUILDID", "42"));

        PipelineVariableExpansion.ApplyStepOutputs(
            variables, new HashSet<string>(), [Output("""{"NOTE":"built by $(BUILD_BUILDID)"}""")]);

        Assert.Equal("built by $(BUILD_BUILDID)", variables["NOTE"]);
    }

    [Fact]
    public void Resolution_AppliesADefaultOnlyWhenTheNameCannotArriveLater()
    {
        var variables = Resolved(
            ("NOW", "$(NEVER_PROVIDED:-fallback)"),
            ("LATER", "$(SOURCE_COMMIT:-fallback)"),
            ("SECRET", "$(NEVER_PROVIDED:-kept-as-typed)"));

        PipelineVariableExpansion.ExpandResolved(
            variables,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SECRET" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SOURCE_COMMIT" });

        Assert.Equal("fallback", variables["NOW"]);
        Assert.Equal("$(SOURCE_COMMIT:-fallback)", variables["LATER"]);
        Assert.Equal("$(NEVER_PROVIDED:-kept-as-typed)", variables["SECRET"]);
    }

    /// <summary>Aliases inherit secret classification transitively while retaining their resolved
    /// values, preventing public persistence and unrestricted propagation.</summary>
    [Fact]
    public void Resolution_ClassifiesEveryAliasOfASecretAsSecret()
    {
        var variables = Resolved(
            ("VAULT_SECRET", "s3cr3t"),
            ("ALIAS", "$(VAULT_SECRET)"),
            ("CHAIN", "user:$(ALIAS)"),
            ("FALLBACK", "$(VAULT_SECRET:-none)"),
            ("PUBLIC", "$(OTHER)"),
            ("OTHER", "visible"));
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VAULT_SECRET" };

        PipelineVariableExpansion.ExpandResolved(variables, secrets, new HashSet<string>());

        Assert.Equal(["ALIAS", "CHAIN", "FALLBACK", "VAULT_SECRET"], secrets.Order(StringComparer.Ordinal));
        Assert.Equal("s3cr3t", variables["ALIAS"]);
        Assert.Equal("user:s3cr3t", variables["CHAIN"]);
        Assert.Equal("s3cr3t", variables["FALLBACK"]);
        Assert.Equal("visible", variables["PUBLIC"]);

        var persisted = PipelineRunHelpers.FilterSecretKeys(variables, secrets);
        Assert.DoesNotContain(persisted.Values, value => value.Contains("s3cr3t", StringComparison.Ordinal));

        var unrelated = new PipelineStepDefinition { Name = "build", Shell = "dotnet build" };
        var unrelatedEnv = PipelineRunHelpers.ScopeStepEnvironment(variables, secrets, unrelated);
        Assert.DoesNotContain(unrelatedEnv.Values, value => value.Contains("s3cr3t", StringComparison.Ordinal));

        var consumer = new PipelineStepDefinition { Name = "login", Shell = "login --password $(ALIAS)" };
        var consumerEnv = PipelineRunHelpers.ScopeStepEnvironment(variables, secrets, consumer);
        Assert.Equal("s3cr3t", consumerEnv["ALIAS"]);
        var command = PipelineCommandBuilder.BuildStepCommand(consumer, consumerEnv, isWindows: false, secrets);
        Assert.DoesNotContain("s3cr3t", command, StringComparison.Ordinal);
    }
}
