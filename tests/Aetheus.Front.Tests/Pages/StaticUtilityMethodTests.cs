// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using PipelinesComp = Aetheus.Front.Components.Shared.PipelineHelper;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Tests for internal/public static utility methods that return non-badge values
/// (cron validation, run badge delegation, etc.).
/// </summary>
public class StaticUtilityMethodTests
{
    // --- CronSchedulePreview.ValidateCronExpression ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCronExpression_NullOrWhitespace_Invalid(string? expr)
    {
        var (isValid, nextRun, error) = CronSchedulePreview.ValidateCronExpression(expr);
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_ValidStandard_ReturnsValid()
    {
        var (isValid, nextRun, error) = CronSchedulePreview.ValidateCronExpression("*/5 * * * *");
        Assert.True(isValid);
        Assert.NotNull(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_SixFieldWithSeconds_Rejected()
    {
        // The /etc/cron.d preview accepts only five-field schedules.
        var (isValid, nextRun, error) = CronSchedulePreview.ValidateCronExpression("0 */5 * * * *");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCronExpression_Invalid_ReturnsError()
    {
        var (isValid, nextRun, error) = CronSchedulePreview.ValidateCronExpression("not-a-cron");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("0 0 * * *")]
    [InlineData("0 12 * * MON-FRI")]
    [InlineData("0 0 1 * *")]
    public void ValidateCronExpression_VariousValid_AllValid(string expr)
    {
        var (isValid, _, error) = CronSchedulePreview.ValidateCronExpression(expr);
        Assert.True(isValid);
        Assert.Null(error);
    }

    // --- PipelinesComp.GetRunBadge (delegates to PipelineHelper) ---

    [Theory]
    [InlineData(PipelineStatus.Success, OmniTone.Success)]
    [InlineData(PipelineStatus.Failed, OmniTone.Danger)]
    [InlineData(PipelineStatus.Running, OmniTone.Accent)]
    // Partial uses amber, while cancelled runs use neutral grey.
    [InlineData(PipelineStatus.Cancelled, OmniTone.Warning)]
    [InlineData(PipelineStatus.Partial, OmniTone.Warning)]
    [InlineData(PipelineStatus.Pending, OmniTone.Neutral)]
    public void Pipelines_GetRunBadge_ReturnsExpected(PipelineStatus status, OmniTone expected)
    {
        Assert.Equal(expected, PipelinesComp.GetRunBadge(status));
    }
}
