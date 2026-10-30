// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;

namespace Aetheus.E2E;

/// <summary> A status count badge is a number badge (recette R-44, rule of 2026-10-25): the number and
/// the status name, no icon, the name visible on a phone too. Measured on the dashboard's Monitored
/// applications tile, whose counts are fixed here (the rest of the summary is the real response).
/// </summary>
[Category("E2E")]
[Category("Dashboard")]
[NonParallelizable]
public sealed class StatusCountBadgeLayoutTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        await Page.RouteAsync("**/api/appmonitoring/summary", async route =>
        {
            var response = await route.FetchAsync();
            var summary = JsonNode.Parse(await response.TextAsync())!.AsObject();
            summary["upCount"] = 20;
            summary["downCount"] = 5;
            summary["degradedCount"] = 0;
            summary["unknownCount"] = 0;
            await route.FulfillAsync(new() { Response = response, Body = summary.ToJsonString() });
        });
    }

    [TestCase(375)]
    [TestCase(1280)]
    public async Task StatusCountBadge_ShowsTheNumberAndTheName_WithoutIcon_AtEveryWidth(int width)
    {
        await Page.SetViewportSizeAsync(width, 812);
        await NavigateToAsync("");

        var tile = Page.Locator(".dashboard-tile").Filter(new() { Has = Page.GetByRole(Microsoft.Playwright.AriaRole.Heading, new() { Name = "Monitored applications" }) });
        var badges = tile.Locator("[data-testid=status-count-badge]");
        await Expect(badges).ToHaveCountAsync(2, new() { Timeout = 15000 });
        var json = await badges.EvaluateAllAsync<string>("""
            badges => JSON.stringify(badges.map(badge => {
                const label = badge.querySelector('.status-count-badge__label').getBoundingClientRect();
                return {
                    text: badge.textContent.trim().replace(/\s+/g, ' '),
                    title: badge.getAttribute('title'),
                    iconCount: badge.querySelectorAll('.omni-icon').length,
                    labelWidth: label.width,
                    top: badge.getBoundingClientRect().top
                };
            }))
            """);
        var measured = System.Text.Json.JsonSerializer.Deserialize<BadgeLayout[]>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Multiple(() =>
        {
            foreach (var badge in measured)
            {
                Assert.That(badge.Title, Is.Not.Empty, "The status name is the tooltip.");
                Assert.That(badge.Text, Does.EndWith(badge.Title!), "The status name is the visible text.");
                Assert.That(badge.IconCount, Is.EqualTo(0), "A badge that carries a number has no icon.");
                Assert.That(badge.LabelWidth, Is.GreaterThan(1), "The status name stays visible, on a phone too.");
            }
            Assert.That(measured.Select(b => b.Text), Is.EquivalentTo(new[] { "20 Up", "5 Down" }));
            // The two badges share one line instead of wrapping under the title.
            Assert.That(measured.Select(b => b.Top).Distinct().Count(), Is.EqualTo(1));
        });
    }

    private sealed record BadgeLayout(string Text, string? Title, int IconCount, double LabelWidth, double Top);
}
