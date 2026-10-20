// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary> The header bell shows the unread count of the signed-in user, next to the task count.
/// The count the API answers is fixed here (the rest of the response is the real one), so the test
/// reads what the shell does with it, whatever notifications the E2E database holds. </summary>
[Category("E2E")]
[Category("Dashboard")]
public class HeaderNotificationBellTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        await Page.RouteAsync("**/api/notifications/me/unread-count", async route =>
        {
            var response = await route.FetchAsync();
            await route.FulfillAsync(new() { Response = response, Body = "3" });
        });
        await NavigateToAsync("");
    }

    [Test]
    public async Task Bell_ShowsTheUnreadCountOfTheSignedInUser()
    {
        var count = Page.Locator(".notification-bell .notification-bell-count");
        await Expect(count).ToHaveTextAsync("3", new() { Timeout = 15000 });
        await Expect(count).ToBeVisibleAsync();
    }
}
