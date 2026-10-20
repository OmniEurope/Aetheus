// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The time budget and sample count of a smoke step, read after variable substitution.</summary>
public class PipelineSmokeLimitsTests
{
    private static readonly Dictionary<string, string> Vars = new(StringComparer.Ordinal) { ["BUDGET"] = "2.5", ["SAMPLES"] = "7" };

    [Theory]
    [InlineData(null, null, "", 1)]
    [InlineData("$(BUDGET)", "$(SAMPLES)", "2.5", 7)]
    [InlineData(" 30 ", "50", "30", 50)]
    [InlineData("1e1", "1", "1e1", 1)]
    public void ValidLimits_AreRead(string? maxSeconds, string? samples, string expectedBudget, int expectedSamples)
    {
        var step = new PipelineStepDefinition { Name = "smoke", MaxSeconds = maxSeconds, Samples = samples };

        Assert.True(PipelineHostOperationTaskFactory.TryReadSmokeLimits(step, Vars, out var budget, out var count, out var error));

        Assert.Equal(expectedBudget, budget);
        Assert.Equal(expectedSamples, count);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("soon", null, "invalid 'max_seconds' 'soon'.")]
    [InlineData("0", null, "invalid 'max_seconds' '0'.")]
    [InlineData("-3", "5", "invalid 'max_seconds' '-3'.")]
    [InlineData(null, "0", "'samples' must be between 1 and 50, got '0'.")]
    [InlineData(null, "51", "'samples' must be between 1 and 50, got '51'.")]
    [InlineData(null, "+5", "'samples' must be between 1 and 50, got '+5'.")]
    [InlineData(null, "many", "'samples' must be between 1 and 50, got 'many'.")]
    public void InvalidLimits_AreRefusedWithTheirValue(string? maxSeconds, string? samples, string reason)
    {
        var step = new PipelineStepDefinition { Name = "smoke", MaxSeconds = maxSeconds, Samples = samples };

        Assert.False(PipelineHostOperationTaskFactory.TryReadSmokeLimits(step, Vars, out _, out _, out var error));

        Assert.Equal(reason, error);
    }
}
