// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-16: a run notice travels as a code, so the front says it in French.</summary>
public sealed class PipelineRunNoticesTests
{
    [Fact]
    public void ANotice_RoundTripsItsCode_WithOrWithoutDetail()
    {
        Assert.Equal(PipelineRunNotices.BaselineAbsent, PipelineRunNotices.CodeOf(PipelineRunNotices.Compose(PipelineRunNotices.BaselineAbsent)));
        Assert.Equal(
            PipelineRunNotices.BaselineAbsent,
            PipelineRunNotices.CodeOf(PipelineRunNotices.Compose(PipelineRunNotices.BaselineAbsent, "Restore-artifacts step 'x': a | b")));
    }

    [Fact]
    public void TheEnglishWarningOfEarlierRuns_IsReadAsTheSameNotice()
    {
        const string legacy = "Restore-artifacts step 'baseline': the selected release contract does not require this retained artifact; explicit fallback selected.";

        Assert.Equal(PipelineRunNotices.BaselineAbsent, PipelineRunNotices.CodeOf(legacy));
    }

    [Theory]
    [InlineData("no online server matches agent 'linux'")]
    [InlineData("Restore-artifacts step 'baseline': the requested artifact could not be resolved.")]
    [InlineData("")]
    public void AnOrdinaryWarning_IsNotANotice(string warning) =>
        Assert.Null(PipelineRunNotices.CodeOf(warning));
}
