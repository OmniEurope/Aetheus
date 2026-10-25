// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests;

public class PipelineParameterResolverTests
{
    private static PipelineTemplateParameterDefinition Param(
        string name, string type = "string", string? def = null, bool required = false, params string[] allowed)
        => new() { Name = name, Type = type, Default = def, Required = required, AllowedValues = [.. allowed] };

    [Fact]
    public void TryResolve_SuppliedValue_WinsOverDefault()
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("ENV", def: "dev")],
            new Dictionary<string, string> { ["ENV"] = "prod" },
            out var effective, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal("prod", effective["ENV"]);
    }

    [Fact]
    public void TryResolve_NoSuppliedValue_FallsBackToDefault()
    {
        var ok = PipelineParameterResolver.TryResolve([Param("ENV", def: "dev")], null, out var effective, out _);

        Assert.True(ok);
        Assert.Equal("dev", effective["ENV"]);
    }

    [Fact]
    public void TryResolve_RequiredMissingNoDefault_Errors()
    {
        var ok = PipelineParameterResolver.TryResolve([Param("ENV", required: true)], null, out var effective, out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("ENV") && e.Contains("required"));
        Assert.False(effective.ContainsKey("ENV"));
    }

    /// <summary>R-25: <c>advanced: true</c> reads from the pipeline YAML, and absent means a main field.</summary>
    [Fact]
    public void ParameterYaml_ReadsTheAdvancedFlag()
    {
        var definition = Aetheus.Back.Services.YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>("""
            name: p
            parameters:
              - name: imageRetentionDryRun
                type: boolean
                advanced: true
              - name: candidateVersion
                required: true
            """);

        Assert.True(definition.Parameters.Single(p => p.Name == "imageRetentionDryRun").Advanced);
        Assert.False(definition.Parameters.Single(p => p.Name == "candidateVersion").Advanced);
    }

    /// <summary>R-26: a preview that runs before the user answered does not complain about the field.</summary>
    [Fact]
    public void TryResolve_RequiredMissing_IsNotAnError_WhenRequiredIsNotEnforced()
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("ENV", required: true)], null, out var effective, out var errors, enforceRequired: false);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.False(effective.ContainsKey("ENV"));
    }

    /// <summary>
    /// Saving a pipeline and launching one ask different questions, and only one of them is about
    /// values. A required parameter with no default is a perfectly storable declaration - it is what
    /// .pipeline/aetheus-deploy-prod.yaml ships - but running the launch check against an empty value
    /// set at save time made that shape impossible to write through the API or the editor, while the
    /// repository-sync path stored it happily. These two pin the split so the paths cannot drift back
    /// together.
    /// </summary>
    [Fact]
    public void ValidateDeclarations_AcceptsARequiredParameterWithNoDefault()
    {
        var errors = PipelineParameterResolver.ValidateDeclarations([Param("candidateVersion", required: true)]);

        Assert.Empty(errors);
    }

    [Fact]
    public void ARequiredParameterWithNoValue_IsStillRefusedAtRunTime()
    {
        var declared = new[] { Param("candidateVersion", required: true) };

        // Storable...
        Assert.Empty(PipelineParameterResolver.ValidateDeclarations(declared));

        // ...and still unlaunchable without a value, which is where the requirement belongs.
        var ok = PipelineParameterResolver.TryResolve(declared, null, out _, out var errors);
        Assert.False(ok);
        Assert.Contains(errors, error =>
            error.Contains("candidateVersion", StringComparison.Ordinal)
            && error.Contains("required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("AETHEUS_THING")]
    [InlineData("BUILD_THING")]
    [InlineData("SYSTEM_THING")]
    public void ValidateDeclarations_StillRejectsAReservedName(string name)
    {
        var errors = PipelineParameterResolver.ValidateDeclarations([Param(name)]);

        // Relaxing the value check must not relax the checks that are genuinely about the definition.
        Assert.Contains(errors, error => error.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDeclarations_RejectsADefaultThatContradictsItsOwnType()
    {
        var errors = PipelineParameterResolver.ValidateDeclarations([Param("retries", type: "number", def: "abc")]);

        Assert.Contains(errors, error => error.Contains("number", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDeclarations_RejectsADuplicateName()
    {
        var errors = PipelineParameterResolver.ValidateDeclarations([Param("env"), Param("ENV")]);

        // Case-insensitive: the resolver matches supplied values that way, so two declarations
        // differing only in case would make the effective value depend on declaration order.
        Assert.Contains(errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void TryResolve_RequiredWithDefault_UsesDefault()
    {
        var ok = PipelineParameterResolver.TryResolve([Param("ENV", def: "dev", required: true)], null, out var effective, out _);

        Assert.True(ok);
        Assert.Equal("dev", effective["ENV"]);
    }

    [Fact]
    public void TryResolve_UnknownParameter_Errors()
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("ENV")],
            new Dictionary<string, string> { ["NOPE"] = "x" },
            out _, out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("Unknown parameter 'NOPE'"));
    }

    [Fact]
    public void TryResolve_ChoiceOutOfRange_Errors()
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("ENV", type: "choice", allowed: ["dev", "prod"])],
            new Dictionary<string, string> { ["ENV"] = "staging" },
            out _, out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("ENV") && e.Contains("one of"));
    }

    [Fact]
    public void TryResolve_ChoiceInRange_Ok()
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("ENV", type: "choice", allowed: ["dev", "prod"])],
            new Dictionary<string, string> { ["ENV"] = "prod" },
            out var effective, out _);

        Assert.True(ok);
        Assert.Equal("prod", effective["ENV"]);
    }

    [Theory]
    [InlineData("number", "12.5", true)]
    [InlineData("number", "abc", false)]
    [InlineData("boolean", "true", true)]
    [InlineData("boolean", "maybe", false)]
    public void TryResolve_TypedValue_Validates(string type, string value, bool expectedOk)
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param("P", type: type)],
            new Dictionary<string, string> { ["P"] = value },
            out _, out _);

        Assert.Equal(expectedOk, ok);
    }

    [Fact]
    public void TryResolve_OptionalNoValue_NotInjected()
    {
        var ok = PipelineParameterResolver.TryResolve([Param("OPT")], null, out var effective, out _);

        Assert.True(ok);
        Assert.Empty(effective);
    }

    [Theory]
    [InlineData("AETHEUS_RUN_BRANCH")]
    [InlineData("BUILD_SOURCEVERSION")]
    [InlineData("SYSTEM_TOKEN")]
    public void TryResolve_ReservedSystemName_IsRejected(string name)
    {
        var ok = PipelineParameterResolver.TryResolve(
            [Param(name, def: "attacker")], null, out var effective, out var errors);

        Assert.False(ok);
        Assert.Empty(effective);
        Assert.Contains(errors, error => error.Contains("reserved system variable", StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_ReturnsOnlyParamsWithDefault()
    {
        var defaults = PipelineParameterResolver.Defaults([Param("A", def: "1"), Param("B"), Param("C", def: "3")]);

        Assert.Equal(2, defaults.Count);
        Assert.Equal("1", defaults["A"]);
        Assert.Equal("3", defaults["C"]);
        Assert.False(defaults.ContainsKey("B"));
    }
}
