// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineRunFormattingBehaviorTests
{
    [Theory]
    [InlineData("Windows Server 2025", true)]
    [InlineData("ubuntu 24.04", false)]
    [InlineData(null, false)]
    public void IsWindows_DetectsOnlyWindowsHosts(string? os, bool expected)
        => Assert.Equal(expected, PipelineRunFormatting.IsWindows(os));

    [Theory]
    [InlineData(999L, "999 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1_048_576L, "MB")]
    [InlineData(1_073_741_824L, "GB")]
    public void FormatSize_UsesTheExpectedUnit(long bytes, string expected)
    {
        var formatted = PipelineRunFormatting.FormatSize(bytes);

        if (expected.Contains(' '))
            Assert.Equal(expected, formatted);
        else
            Assert.EndsWith(expected, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void StepBadge_DistinguishesRollbackAdvisoryFailureAndBlockingFailure()
    {
        var rolledBack = FailedStep((PipelineRunFormatting.RolledBackVar, "TRUE"));
        var advisory = new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Failed,
            ContinueOnError = true
        };
        var blocking = FailedStep();

        Assert.True(PipelineRunFormatting.IsRolledBack(rolledBack));
        Assert.Equal(OmniTone.Warning, PipelineRunFormatting.GetStepBadge(rolledBack));
        // Advisory failure remains red and uses an outline to distinguish its nonblocking outcome.
        Assert.Equal(OmniTone.Danger, PipelineRunFormatting.GetStepBadge(advisory));
        Assert.Equal(OmniFill.Outline, PipelineRunFormatting.GetStepBadgeVariant(advisory));
        Assert.False(PipelineRunFormatting.IsRolledBack(blocking));
        Assert.Equal(OmniTone.Danger, PipelineRunFormatting.GetStepBadge(blocking));
        Assert.Equal(OmniFill.Solid, PipelineRunFormatting.GetStepBadgeVariant(blocking));
        Assert.Equal(OmniFill.Solid, PipelineRunFormatting.GetStepBadgeVariant(rolledBack));
    }

    [Fact]
    public void UpstreamMetadata_ParsesValidLineageAndRejectsInvalidRunId()
    {
        var lineage = new Dictionary<string, string>
        {
            ["UPSTREAM_RUN_ID"] = "42",
            ["UPSTREAM_CHAIN"] = "3,7,9"
        };

        Assert.Equal(42, PipelineRunFormatting.UpstreamRunId(lineage));
        Assert.Equal(3, PipelineRunFormatting.UpstreamChainDepth(lineage));
        Assert.Null(PipelineRunFormatting.UpstreamRunId(new Dictionary<string, string> { ["UPSTREAM_RUN_ID"] = "invalid" }));
        Assert.Equal(0, PipelineRunFormatting.UpstreamChainDepth(new Dictionary<string, string>()));
    }

    [Fact]
    public void TextHelpers_ReturnStableCompactValues()
    {
        Assert.Equal("01234567", PipelineRunFormatting.ShortSha("0123456789abcdef"));
        Assert.Equal($"{74.9:F1}%", PipelineRunFormatting.FormatPercent(0.749));
        Assert.Equal("Pipelines/PipelineRun.cs", PipelineRunFormatting.ShortPath(@"C:\src\Pipelines\PipelineRun.cs"));
        Assert.Equal("file.cs", PipelineRunFormatting.ShortPath("file.cs"));
        Assert.Equal(string.Empty, PipelineRunFormatting.ShortPath(string.Empty));
        Assert.True(PipelineRunFormatting.IsLintWarning("Compiler WARNING CS8602"));
        Assert.False(PipelineRunFormatting.IsLintWarning("Build succeeded"));
    }

    [Theory]
    [InlineData(0.9, "var(--omni-u-text-success)", "omni-u-text-success")]
    [InlineData(0.6, "var(--omni-u-text-warning)", "omni-u-text-warning")]
    [InlineData(0.2, "var(--omni-u-text-danger)", "omni-u-text-danger")]
    public void CoverageColors_ReflectRisk(double rate, string color, string colorClass)
    {
        Assert.Equal(color, PipelineRunFormatting.CoverageColor(rate));
        Assert.Equal(colorClass, PipelineRunFormatting.CoverageColorClass(rate));
    }

    [Theory]
    [InlineData(4, "omni-u-text-success")]
    [InlineData(8, "omni-u-text-warning")]
    [InlineData(12, "omni-u-text-danger")]
    public void ComplexityColor_ReflectsRisk(double value, string expected)
        => Assert.Equal(expected, PipelineRunFormatting.ComplexityColorClass(value));

    [Theory]
    [InlineData(4, "omni-u-text-success")]
    [InlineData(20, "omni-u-text-warning")]
    [InlineData(40, "omni-u-text-danger")]
    public void CrapColor_ReflectsRisk(double value, string expected)
        => Assert.Equal(expected, PipelineRunFormatting.CrapColorClass(value));

    [Theory]
    [InlineData("https://git.example/aetheus.git", "abc123", "https://git.example/aetheus/commit/abc123")]
    [InlineData("https://git.example/aetheus/", "abc123", "https://git.example/aetheus/commit/abc123")]
    [InlineData("ssh://git.example/aetheus.git", "abc123", null)]
    [InlineData("https://git.example/aetheus.git", "", null)]
    public void BuildCommitUrl_OnlyBuildsLinksForWebRepositories(string repo, string sha, string? expected)
        => Assert.Equal(expected, PipelineRunFormatting.BuildCommitUrl(repo, sha));

    [Fact]
    public void StageRunsInParallel_DetectsOverlapButNotSequentialSteps()
    {
        var origin = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        var overlapping = Stage(
            Step(origin, origin.AddMinutes(3)),
            Step(origin.AddMinutes(2), origin.AddMinutes(4)));
        var sequential = Stage(
            Step(origin, origin.AddMinutes(2)),
            Step(origin.AddMinutes(2), origin.AddMinutes(4)),
            new PipelineStepRunDto { Status = TaskExecutionStatus.Pending });

        Assert.True(PipelineRunFormatting.StageRunsInParallel(overlapping));
        Assert.False(PipelineRunFormatting.StageRunsInParallel(sequential));
    }

    [Fact]
    public void BuiltAppVersion_PrefersTheResolvedVariableAndFallsBackToAStepOutput()
    {
        var resolved = new PipelineRunDto
        {
            ResolvedVariables = new Dictionary<string, string> { ["APP_VERSION"] = " 1.1.57 " }
        };
        var emitted = new PipelineRunDto
        {
            Steps =
            [
                new PipelineStepRunDto { OutputVariables = new Dictionary<string, string> { ["APP_VERSION"] = "1.1.12" } },
                new PipelineStepRunDto { OutputVariables = new Dictionary<string, string> { ["APP_VERSION"] = "1.1.13" } }
            ]
        };

        Assert.Equal("1.1.57", PipelineRunFormatting.BuiltAppVersion(resolved));
        Assert.Equal("1.1.13", PipelineRunFormatting.BuiltAppVersion(emitted));
        Assert.Null(PipelineRunFormatting.BuiltAppVersion(new PipelineRunDto()));
    }

    private static PipelineStepRunDto FailedStep(params (string Key, string Value)[] outputVariables) => new()
    {
        Status = TaskExecutionStatus.Failed,
        OutputVariables = outputVariables.ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    private static PipelineStepRunDto Step(DateTime startedAt, DateTime completedAt) => new()
    {
        Status = TaskExecutionStatus.Success,
        StartedAt = startedAt,
        CompletedAt = completedAt
    };

    private static StageViewModel Stage(params PipelineStepRunDto[] steps) => new()
    {
        Name = "Test",
        Steps = [.. steps]
    };
}
