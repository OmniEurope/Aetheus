// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisPlatformOptionsTests
{
    [Fact]
    public void IsValid_AcceptsDocumentedRetentionDefaults()
    {
        Assert.True(new AnalysisPlatformOptions().IsValid());
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(9, 9)]
    [InlineData(5, 4)]
    public void IsValid_RejectsUnsafeReportAdmissionConcurrency(int admissions, int ingestions)
    {
        var options = new AnalysisPlatformOptions
        {
            MaxConcurrentReportAdmissions = admissions,
            MaxConcurrentIngestions = ingestions
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(0, 365, 30, 24)]
    [InlineData(730, 0, 30, 24)]
    [InlineData(730, 365, 0, 24)]
    [InlineData(730, 365, 30, 0)]
    public void IsValid_RejectsUnsafeRetentionValues(
        int findingDays,
        int sbomDays,
        int logDays,
        int ephemeralHours)
    {
        var options = new AnalysisPlatformOptions
        {
            FindingRetentionDays = findingDays,
            SbomRetentionDays = sbomDays,
            LogRetentionDays = logDays,
            EphemeralEnvironmentRetentionHours = ephemeralHours
        };

        Assert.False(options.IsValid());
    }
}
