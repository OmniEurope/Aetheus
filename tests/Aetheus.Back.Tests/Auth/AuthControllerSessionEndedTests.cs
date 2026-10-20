// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>The normal end of a session is information; only an anomaly is a warning.</summary>
public sealed class AuthControllerSessionEndedTests
{
    private readonly RecordingLogger<AuthController> _log = new();

    private IActionResult Report(string reason) =>
        new AuthController(
            Substitute.For<IAuthService>(),
            Substitute.For<IServerEnrollmentService>(),
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        }.SessionEnded(new SessionEndedReport { Reason = reason, CorrelationId = "tab-1" }, _log);

    [Theory]
    [InlineData(RefreshRejectionCodes.UnknownToken)]
    [InlineData(RefreshRejectionCodes.Expired)]
    [InlineData(RefreshRejectionCodes.UserInactive)]
    [InlineData(SessionEndReasons.RenewRejected)]
    [InlineData(SessionEndReasons.NoRefreshTokenAtStartup)]
    public void AnOrdinaryEndOfSession_IsInformation(string reason)
    {
        Assert.IsType<NoContentResult>(Report(reason));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains($"reason={reason}", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RefreshRejectionCodes.Replay)]
    [InlineData(SessionEndReasons.RefreshRejected)]
    public void AnAnomaly_StaysAWarning(string reason)
    {
        Assert.IsType<NoContentResult>(Report(reason));

        Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void EveryKnownReason_HasADecidedLevel()
    {
        // A reason added later is information unless someone decides it signals an anomaly.
        var warnings = SessionEndReasons.All
            .Where(reason => AuthController.SessionEndedLevel(reason) == LogLevel.Warning)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([SessionEndReasons.RefreshRejected, RefreshRejectionCodes.Replay], warnings);
    }

    [Fact]
    public void AnUnknownReason_IsRefused_AndNotLogged()
    {
        Assert.IsType<BadRequestObjectResult>(Report("made_up"));
        Assert.Empty(_log.Entries);
    }
}
