// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests;

/// <summary>
/// The run preview applies declared parameters the way a real trigger does: only declared names are taken
/// from the supplied variables, the effective value (supplied, else default) is added under its own name and
/// under the <c>parameters.</c> namespace, and a value the resolver refuses becomes a warning, never an error.
/// </summary>
public class PipelineRunPreviewParameterTests
{
    private static PipelineYamlDefinition Definition(params PipelineTemplateParameterDefinition[] parameters) =>
        new() { Parameters = [.. parameters] };

    private static PipelineTemplateParameterDefinition Param(
        string name, string? def = null, bool required = false, params string[] allowed) =>
        new() { Name = name, Type = "string", Default = def, Required = required, AllowedValues = [.. allowed] };

    [Fact]
    public void APipelineWithoutParameters_LeavesTheVariablesUntouched()
    {
        Dictionary<string, string>? variables = new() { ["ENV"] = "prod" };

        var warnings = PipelineRunService.ValidateAndApplyParameters(Definition(), ref variables);

        Assert.Empty(warnings);
        Assert.Equal(new Dictionary<string, string> { ["ENV"] = "prod" }, variables);
    }

    [Fact]
    public void SuppliedAndDefaultValues_AreAppliedUnderBothNames_AndUndeclaredVariablesAreKept()
    {
        Dictionary<string, string>? variables = new(StringComparer.OrdinalIgnoreCase) { ["env"] = "prod", ["OTHER"] = "kept" };

        var warnings = PipelineRunService.ValidateAndApplyParameters(
            Definition(Param("ENV", def: "dev"), Param("REGION", def: "eu")), ref variables);

        Assert.Empty(warnings);
        Assert.NotNull(variables);
        Assert.Equal("prod", variables["ENV"]);
        Assert.Equal("prod", variables["parameters.ENV"]);
        Assert.Equal("eu", variables["REGION"]);
        Assert.Equal("eu", variables["parameters.REGION"]);
        Assert.Equal("kept", variables["OTHER"]);
    }

    [Fact]
    public void WithoutSuppliedVariables_TheDefaultsCreateTheSet()
    {
        Dictionary<string, string>? variables = null;

        var warnings = PipelineRunService.ValidateAndApplyParameters(Definition(Param("ENV", def: "dev")), ref variables);

        Assert.Empty(warnings);
        Assert.NotNull(variables);
        Assert.Equal("dev", variables["ENV"]);
        Assert.Equal("dev", variables["parameters.ENV"]);
    }

    [Fact]
    public void ARefusedOrMissingValue_IsAWarning_AndNothingIsInvented()
    {
        Dictionary<string, string>? variables = null;

        var warnings = PipelineRunService.ValidateAndApplyParameters(
            Definition(Param("VERSION", required: true)), ref variables);

        var warning = Assert.Single(warnings);
        Assert.Equal("ParameterRequired", StoredProblem.CodeOf(warning));
        Assert.StartsWith("Parameter: ", StoredProblem.English(warning), StringComparison.Ordinal);
        Assert.Contains("VERSION", StoredProblem.English(warning), StringComparison.Ordinal);
        Assert.Null(variables);
    }

    /// <summary>R-20 and R-26: the launch preflight runs before the user answered. It warns about no
    /// missing required value, and reads a declared parameter with no value as the empty string the
    /// launch will substitute under <c>parameters.</c>, while supplied and default values stay as they are.</summary>
    [Fact]
    public void ThePreflight_DoesNotWarnAboutAMissingRequiredValue_AndReadsAnEmptyOptionalParameterAsEmpty()
    {
        Dictionary<string, string>? variables = null;

        var warnings = PipelineRunService.ValidateAndApplyParameters(
            Definition(Param("VERSION", required: true), Param("SERVER"), Param("REGION", def: "eu")),
            ref variables, forPreflight: true);

        Assert.Empty(warnings);
        Assert.NotNull(variables);
        Assert.Equal(string.Empty, variables["parameters.SERVER"]);
        Assert.Equal(string.Empty, variables["parameters.VERSION"]);
        Assert.Equal("eu", variables["parameters.REGION"]);
        Assert.False(variables.ContainsKey("SERVER"));
    }
}
