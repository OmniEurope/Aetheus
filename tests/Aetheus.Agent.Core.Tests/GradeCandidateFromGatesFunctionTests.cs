// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R-23 (W8): the wizard's candidate published a release with no grade and no verdict although its Quality
/// and Security gates had graded it, so every deployment stopped on the approval of a release "not qualified". The
/// function seals the gates' own grades into the candidate's verdict, run through the real executor.
/// </summary>
public sealed class GradeCandidateFromGatesFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-grade-candidate");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal)
    {
        ["APPLICATION_QUALITY_ENABLED"] = "true",
        ["APPLICATION_SECURITY_ENABLED"] = "true",
        ["ANALYSIS_QUALITY_GRADE"] = "",
        ["ANALYSIS_SECURITY_GRADE"] = "",
        ["AETHEUS_DEPLOY_MINIMUM_GRADE"] = ""
    };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private async Task<Dictionary<string, string>> PublishedAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        var (result, variables) = await _harness.RunAsync(new GradeCandidateFromGatesFunction(), _env);
        Assert.Equal(0, result.ExitCode);
        return variables;
    }

    [Theory]
    [InlineData("B", "A", "B")]
    [InlineData("A", "D", "D")]
    [InlineData("A", "A", "A")]
    [InlineData("E", "C", "E")]
    public async Task TheCandidate_TakesTheWorstGateGrade_AndIsDeployableUpToE(string quality, string security, string expected)
    {
        _env["ANALYSIS_QUALITY_GRADE"] = quality;
        _env["ANALYSIS_SECURITY_GRADE"] = security;

        var published = await PublishedAsync();

        Assert.Equal(expected, published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("true", published["CANDIDATE_DEPLOYABLE"]);
    }

    /// <summary>R-24 stays whole: a release graded F is published as not deployable, so its deployment still asks for
    /// the approval.</summary>
    [Fact]
    public async Task AGateGradedF_MakesTheCandidateNotDeployable()
    {
        _env["ANALYSIS_QUALITY_GRADE"] = "B";
        _env["ANALYSIS_SECURITY_GRADE"] = "F";

        var published = await PublishedAsync();

        Assert.Equal("F", published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("false", published["CANDIDATE_DEPLOYABLE"]);
    }

    [Theory]
    [InlineData("C", "B", "true")]
    [InlineData("C", "C", "true")]
    [InlineData("B", "C", "false")]
    public async Task TheDeploymentThreshold_DecidesTheVerdict(string minimum, string grade, string deployable)
    {
        _env["AETHEUS_DEPLOY_MINIMUM_GRADE"] = minimum;
        _env["ANALYSIS_QUALITY_GRADE"] = grade;
        _env["ANALYSIS_SECURITY_GRADE"] = "A";

        var published = await PublishedAsync();

        Assert.Equal(grade, published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal(deployable, published["CANDIDATE_DEPLOYABLE"]);
    }

    [Fact]
    public async Task TheParameters_WinOverTheVariables()
    {
        _env["ANALYSIS_QUALITY_GRADE"] = "A";
        _env["ANALYSIS_SECURITY_GRADE"] = "A";
        _parameters["quality_grade"] = "C";
        _parameters["minimum_grade"] = "B";

        var published = await PublishedAsync();

        Assert.Equal("C", published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("false", published["CANDIDATE_DEPLOYABLE"]);
    }

    /// <summary>A gate the project does not run is not waited for: the verdict is the one of the gates that ran.</summary>
    [Fact]
    public async Task AGateTheProjectDoesNotRun_IsLeftOut()
    {
        _env["APPLICATION_SECURITY_ENABLED"] = "false";
        _env["ANALYSIS_QUALITY_GRADE"] = "C";

        var published = await PublishedAsync();

        Assert.Equal("C", published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("true", published["CANDIDATE_DEPLOYABLE"]);
    }

    /// <summary>A gate that ran without a grade (incomplete, or an agent too old to publish it) never reads as passed:
    /// no grade is claimed and the candidate is not deployable, so the deployment asks for the approval.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("Z")]
    public async Task AGateThatRanWithoutAGrade_LeavesTheCandidateUngradedAndNotDeployable(string securityGrade)
    {
        _env["ANALYSIS_QUALITY_GRADE"] = "A";
        _env["ANALYSIS_SECURITY_GRADE"] = securityGrade;

        var published = await PublishedAsync();

        // Every declared output is published (the executor refuses a function that leaves one unset): "none" is no
        // letter, so the release reads no grade from it.
        Assert.Equal("none", published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("false", published["CANDIDATE_DEPLOYABLE"]);
        Assert.Contains(_harness.Log, entry => entry.Line.Contains("security", StringComparison.Ordinal));
    }

    /// <summary>With no gate at all there is nothing to grade: neither a grade nor a verdict is claimed, so the release
    /// stays not qualified, as before.</summary>
    [Fact]
    public async Task NoGate_ClaimsNoGradeAndNoVerdict()
    {
        _env["APPLICATION_QUALITY_ENABLED"] = "false";
        _env["APPLICATION_SECURITY_ENABLED"] = "false";

        var published = await PublishedAsync();

        Assert.Equal("none", published["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Equal("none", published["CANDIDATE_DEPLOYABLE"]);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("b")]
    [InlineData("AB")]
    public async Task AThresholdThatIsNotAGrade_IsRefused(string minimum)
    {
        _env["ANALYSIS_QUALITY_GRADE"] = "A";
        _env["ANALYSIS_SECURITY_GRADE"] = "A";
        _parameters["minimum_grade"] = minimum;
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);

        var (result, variables) = await _harness.RunAsync(new GradeCandidateFromGatesFunction(), _env);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains(minimum, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public void TheCatalog_DeclaresWhatTheFunctionPublishes() =>
        Assert.Equal(
            ["CANDIDATE_ASSURANCE_GRADE", "CANDIDATE_DEPLOYABLE"],
            PipelineFunctionCatalog.GradeCandidateFromGates.Outputs);
}
