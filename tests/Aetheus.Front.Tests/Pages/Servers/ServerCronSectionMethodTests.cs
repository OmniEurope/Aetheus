// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerCronSectionMethodTests
{
    [Fact]
    public void ValidateCronExpression_Null_ReturnsFalseWithNoError()
    {
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression(null);
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_Empty_ReturnsFalseWithNoError()
    {
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression("");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_Whitespace_ReturnsFalseWithNoError()
    {
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression("   ");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_StandardFiveField_Valid()
    {
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression("0 * * * *");
        Assert.True(isValid);
        Assert.NotNull(nextRun);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCronExpression_SixFieldWithSeconds_NowRejected()
    {
        // Both preview and save enforce the five-field schedule supported by /etc/cron.d.
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression("0 0 * * * *");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCronExpression_Invalid_ReturnsError()
    {
        var (isValid, nextRun, error) = ServerCronSection.ValidateCronExpression("not-a-cron");
        Assert.False(isValid);
        Assert.Null(nextRun);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCronExpression_EveryMinute_HasNextRun()
    {
        var (isValid, nextRun, _) = ServerCronSection.ValidateCronExpression("* * * * *");
        Assert.True(isValid);
        // The next run reads in the user's culture (short date and time), not ISO.
        Assert.True(DateTime.TryParseExact(nextRun, "g", System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.DateTimeStyles.None, out var parsed), nextRun);
        Assert.InRange(parsed, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(2));
    }
}
