// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

/// <summary>Exercise notifications through the real OmniOverlayService with controlled clock
/// advancement for severity durations, mapped text, correlation ids and administrator log
/// links.</summary>
public sealed class NotifyHelperOverlayTests : BunitContext
{
    private readonly FakeTimeProvider _clock = new();
    private readonly OmniOverlayService _overlay;
    private readonly NotifyHelper _sut;

    public NotifyHelperOverlayTests()
    {
        _overlay = new OmniOverlayService(_clock);
        _sut = new NotifyHelper(_overlay, new BunitTestHelper.StubLocalizer());
    }

    [Theory]
    [InlineData(OmniSeverity.Info, 5)]
    [InlineData(OmniSeverity.Success, 5)]
    [InlineData(OmniSeverity.Warning, 7)]
    [InlineData(OmniSeverity.Danger, 10)]
    public async Task EachSeverity_StaysItsDuration_ThenGoes(OmniSeverity severity, int seconds)
    {
        // Raised off any synchronization context, so the expiry that the clock completes runs to its end
        // inside Advance instead of being posted to the test runner's context and awaited by polling.
        await Task.Run(() => _sut.Notify(severity, "Title", "Message"), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(_overlay.Notifications).Duration);

        _clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(100));
        Assert.Single(_overlay.Notifications);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(_overlay.Notifications);
    }

    [Fact]
    public void ADetail_IsTheMessage_UnderTheSummaryAsTitle()
    {
        _sut.Warning("Summary", "Detail");

        var notification = Assert.Single(_overlay.Notifications);
        Assert.Equal("Summary", notification.Title);
        Assert.Equal("Detail", notification.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDetail_TheSummaryIsTheMessage_WithNoTitle(string detail)
    {
        // OE refuses an empty message: a summary on its own becomes the message.
        _sut.Notify(OmniSeverity.Success, "Copied", detail);

        var notification = Assert.Single(_overlay.Notifications);
        Assert.Null(notification.Title);
        Assert.Equal("Copied", notification.Message);
    }

    [Fact]
    public void FiveToasts_AreAllShown_WithNoAggregateToast()
    {
        for (var i = 0; i < 5; i++) _sut.Success("Saved", $"Item {i}");

        Assert.Equal(5, _overlay.Notifications.Count);
        Assert.All(_overlay.Notifications, notification => Assert.Equal("Saved", notification.Title));
        Assert.Equal(
            new[] { "Item 0", "Item 1", "Item 2", "Item 3", "Item 4" },
            _overlay.Notifications.Select(notification => notification.Message));
    }

    [Fact]
    public void ErrorForAdmin_LinksTheSystemLogsFilteredOnItsCorrelationId()
    {
        var helper = RegisteredHelper(isAdmin: true, out _);

        helper.ErrorRaw("Failure", "Details", "request 42", reportClientError: false);

        var notification = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.Equal(OmniSeverity.Danger, notification.Severity);
        Assert.Equal("admin/system-logs?search=request%2042", notification.DetailsHref);
    }

    [Fact]
    public void ErrorForNonAdmin_HasNoLogsLink()
    {
        var helper = RegisteredHelper(isAdmin: false, out _);

        helper.ErrorRaw("Failure", "Details", "request-42", reportClientError: false);

        Assert.Null(Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications).DetailsHref);
    }

    [Fact]
    public void AToastOtherThanAnError_HasNoLogsLink_EvenForAnAdmin()
    {
        var helper = RegisteredHelper(isAdmin: true, out _);

        helper.Warning("Careful", "Details");

        Assert.Null(Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications).DetailsHref);
    }

    [Fact]
    public async Task AnError_IsReportedUnderTheCorrelationIdOfItsLogsLink()
    {
        var helper = RegisteredHelper(isAdmin: true, out var handler);
        handler.SetResponse(HttpMethod.Post, "api/client-errors", HttpStatusCode.OK);

        helper.Error("SaveFailedTitle", "SaveFailed");

        await handler.WhenRequestedAsync(request => request.Url.Contains("api/client-errors", StringComparison.Ordinal), 1, "the error report");
        var report = handler.RequestDetails.ToList().Single(request => request.Url.Contains("api/client-errors"));
        Assert.Equal("POST", report.Method);
        using var body = JsonDocument.Parse(report.Body!);
        var correlationId = body.RootElement.GetProperty("correlationId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", correlationId);
        Assert.Equal("SaveFailedTitle", body.RootElement.GetProperty("summary").GetString());
        Assert.Equal("SaveFailed", body.RootElement.GetProperty("message").GetString());
        var notification = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.Equal($"admin/system-logs?search={correlationId}", notification.DetailsHref);
    }

    [Fact]
    public void AnError_AskedNotToBeReported_SendsNothing()
    {
        var helper = RegisteredHelper(isAdmin: false, out var handler);

        helper.ErrorRaw("Failure", "Details", reportClientError: false);

        Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.DoesNotContain(handler.Requests.ToList(), request => request.Url.Contains("api/client-errors"));
    }

    /// <summary>The helper as the app builds it, with the reporter and the signed-in user.</summary>
    private NotifyHelper RegisteredHelper(bool isAdmin, out BunitTestHelper.TestHandler handler)
    {
        handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        return Services.GetRequiredService<NotifyHelper>();
    }
}
