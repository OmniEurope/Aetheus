// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Components.SystemLogs;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>A purge without an explicit window follows the Retention:LogDays setting, and falls back
/// to 30 days whenever that setting is absent or outside the 1..3650 range the explicit parameter
/// accepts.</summary>
public sealed class SystemLogsRetentionTests
{
    private readonly ISystemLogService _logs = Substitute.For<ISystemLogService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    [Theory]
    [InlineData("7", 7)]
    [InlineData("1", 1)]
    [InlineData("3650", 3650)]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("abc", 30)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("3651", 30)]
    public async Task PurgeOldLogs_WithoutAWindow_UsesTheConfiguredRetention(string? configured, int expectedDays)
    {
        _settings.GetSettingValueAsync("Retention:LogDays", Arg.Any<CancellationToken>()).Returns(configured);
        _logs.PurgeOldLogsAsync(expectedDays, Arg.Any<CancellationToken>()).Returns(11);

        var result = await new SystemLogsController(_logs, _settings).PurgeOldLogs(ct: TestContext.Current.CancellationToken);

        Assert.Equal(11, Assert.IsType<OkObjectResult>(result.Result).Value);
        await _logs.Received(1).PurgeOldLogsAsync(expectedDays, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeOldLogs_WithAnExplicitWindow_DoesNotReadTheSetting()
    {
        _logs.PurgeOldLogsAsync(14, Arg.Any<CancellationToken>()).Returns(2);

        await new SystemLogsController(_logs, _settings).PurgeOldLogs(14, TestContext.Current.CancellationToken);

        await _settings.DidNotReceiveWithAnyArgs().GetSettingValueAsync(default!, TestContext.Current.CancellationToken);
    }
}
