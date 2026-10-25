// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerCronSectionTests
{
    [Theory]
    [InlineData("*/5 * * * *", true)]
    [InlineData("0 0 * * *", true)]
    [InlineData("0 0 1 1 *", true)]
    public void ValidateCronExpression_ValidExpressions_ReturnsIsValidTrue(string expr, bool expected)
    {
        var result = CronSchedulePreview.ValidateCronExpression(expr);
        Assert.Equal(expected, result.IsValid);
        Assert.NotNull(result.NextRun);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("99 99 * * *")]
    [InlineData("* * * *")]
    [InlineData("*/30 * * * * *")] // 6-field (seconds): the preview mirrors the 5-field cron.d save rule
    public void ValidateCronExpression_InvalidExpressions_ReturnsError(string expr)
    {
        var result = CronSchedulePreview.ValidateCronExpression(expr);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ValidateCronExpression_EmptyInput_ReturnsIsValidFalseWithoutError()
    {
        var result = CronSchedulePreview.ValidateCronExpression("");
        Assert.False(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidateCronExpression_Null_ReturnsIsValidFalse()
    {
        var result = CronSchedulePreview.ValidateCronExpression(null);
        Assert.False(result.IsValid);
        Assert.Null(result.Error);
    }
}
