// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.E2E;

/// <summary>One journey through the sidebar: every destination is reached by a real click on its
/// menu entry, from the place a user clicks it, and must render a real page.</summary>
[Category("E2E")]
[Category("Dashboard")]
public sealed class SidebarNavigationTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [Test]
    [Category("Settings")]
    [Category("Servers")]
    [Category("Tasks")]
    [Category("Logs")]
    [Category("Alerts")]
    [Category("Projects")]
    [Category("Pipelines")]
    public async Task Sidebar_ReachesEveryMainDestination()
    {
        await NavigateToAsync("");

        // Settings is a top-level entry. Tasks, Logs and Alerts belong to the Servers group and
        // Pipelines to the Projects group: each group opens once the user is inside its section.
        (string Entry, string Path)[] journey =
        [
            ("Settings", "settings"),
            ("Servers", "servers"),
            ("Tasks", "tasks"),
            ("Logs", "logs"),
            ("Alerts", "alerts"),
            ("Projects", "projects"),
            ("Pipelines", "pipelines")
        ];

        foreach (var (entry, path) in journey)
        {
            await ClickSidebarNavItemAsync(entry);
            await Expect(Page).ToHaveURLAsync(
                new Regex($"^{Regex.Escape(FrontendUrl.TrimEnd('/'))}/{path}(?:[?#].*)?$"),
                new() { Timeout = 5000 });
            await WaitForNoSpinnerAsync();
            await Expect(Page.Locator(".error-card"))
                .ToHaveCountAsync(0, new() { Timeout = 5000 });
            await Expect(Page.Locator(".not-found-container"))
                .ToHaveCountAsync(0, new() { Timeout = 5000 });
        }
    }
}
