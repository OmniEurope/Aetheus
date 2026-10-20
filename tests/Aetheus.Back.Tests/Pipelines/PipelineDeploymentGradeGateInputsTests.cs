// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The threshold and release a deployment grade gate reads from the run's variables, before
/// any lookup: no threshold lets the run start, a threshold that is not a grade or without a release
/// refuses it.</summary>
public class PipelineDeploymentGradeGateInputsTests
{
    private static Dictionary<string, string> Vars(string? threshold, string? release)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (threshold is not null) vars[PipelineDeploymentGradeGate.MinimumGradeVariable] = threshold;
        if (release is not null) vars[PipelineDeploymentGradeGate.ReleaseVariable] = release;
        return vars;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void NoThreshold_StopsWithoutRefusal(string? threshold)
    {
        Assert.False(PipelineDeploymentGradeGate.TryReadGateInputs(Vars(threshold, "1.0.0"), out _, out _, out var refusal));
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData("b", "'b' is not an assurance grade (A to F).")]
    [InlineData("G", "'G' is not an assurance grade (A to F).")]
    [InlineData("AB", "'AB' is not an assurance grade (A to F).")]
    [InlineData("4", "'4' is not an assurance grade (A to F).")]
    public void AThresholdThatIsNotAGrade_IsRefused(string threshold, string reason)
    {
        Assert.False(PipelineDeploymentGradeGate.TryReadGateInputs(Vars(threshold, "1.0.0"), out _, out _, out var refusal));
        Assert.EndsWith(reason, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "('')")]
    [InlineData(" ", "('')")]
    [InlineData("$(CANDIDATE)", "('$(CANDIDATE)')")]
    public void AThresholdWithoutARelease_IsRefused(string? release, string named)
    {
        Assert.False(PipelineDeploymentGradeGate.TryReadGateInputs(Vars("B", release), out _, out _, out var refusal));
        Assert.Contains($"names no release {named}", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AGradeAndARelease_AreReadTrimmed()
    {
        Assert.True(PipelineDeploymentGradeGate.TryReadGateInputs(Vars(" C ", " 2.4.0 "), out var threshold, out var selector, out var refusal));
        Assert.Equal("C", threshold);
        Assert.Equal("2.4.0", selector);
        Assert.Null(refusal);
    }
}
