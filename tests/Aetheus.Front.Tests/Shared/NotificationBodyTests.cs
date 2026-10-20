// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Show more depends on whether the four-line clamp hides content. bUnit supplies the
/// browser measurement because it performs no layout.</summary>
public sealed class NotificationBodyTests : BunitContext
{
    public NotificationBodyTests() => BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ShowMore_AppearsOnlyWhenTheClampHidesText(bool clamped, int buttons)
    {
        JSInterop.Setup<bool>("Aetheus.isClamped", _ => true).SetResult(clamped);

        var cut = Render<NotificationBody>(parameters => parameters
            .Add(body => body.Summary, "Deployment failed")
            .Add(body => body.Detail, string.Join('\n', Enumerable.Range(1, clamped ? 12 : 2).Select(i => $"line {i}"))));

        cut.WaitForAssertion(() => Assert.Equal(buttons, cut.FindAll(".notification-body-more").Count));
        Assert.Contains("notification-body-text", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogsLink_IsOnlyThereWhenGiven()
    {
        JSInterop.Setup<bool>("Aetheus.isClamped", _ => true).SetResult(false);

        var cut = Render<NotificationBody>(parameters => parameters
            .Add(body => body.Detail, "x")
            .Add(body => body.LogsHref, "/admin/system-logs?search=abc"));

        Assert.Contains("href=\"/admin/system-logs?search=abc\"", cut.Markup, StringComparison.Ordinal);
    }
}
