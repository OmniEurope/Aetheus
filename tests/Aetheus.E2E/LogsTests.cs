// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

[Category("E2E")]
[Category("Logs")]
[RollbackRelevant]
public class LogsTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
    }

    [Test]
    public async Task Logs_WatchLoadsSeededEntries_AndClearRemovesThem()
    {
        await NavigateToAsync("tasks");
        await WaitForDataGridAsync();
        var seededTaskRow = Page.Locator(".omni-data-grid__row")
            .Filter(new() { HasText = "Demo web diagnostic" });
        await Expect(seededTaskRow).ToBeVisibleAsync(new() { Timeout = 10000 });
        var taskIdText = (await seededTaskRow.Locator("td").First.InnerTextAsync()).Trim();
        Assert.That(int.TryParse(taskIdText, out var taskId), Is.True,
            $"Expected the first task cell to contain an id, got '{taskIdText}'.");

        await NavigateToAsync("logs");
        await Expect(Page.GetByText("Live Logs").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Live Logs"));
        var watchButton = Page.GetByRole(AriaRole.Button, new() { Name = "Watch" });
        await Expect(watchButton).ToBeVisibleAsync(new() { Timeout = 10000 });
        // Clear moved into the "..." menu (OmniOverflowMenu in Logs.razor), so it is
        // reached by opening that menu instead of from the toolbar. The assertion is kept: the
        // action still has to exist and still has to empty the panel further down.
        var moreActions = Page.Locator("button[title='More actions']");
        await Expect(moreActions).ToBeVisibleAsync(new() { Timeout = 10000 });
        var taskIdInput = Page.Locator("input.omni-numeric");
        await Expect(taskIdInput.First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var logPanel = Page.Locator(".live-log-panel");
        await Expect(logPanel).ToBeVisibleAsync();

        await taskIdInput.FillAsync(taskId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await taskIdInput.PressAsync("Tab");
        await Expect(watchButton).ToBeEnabledAsync();
        var historyResponse = Page.WaitForResponseAsync(response =>
            response.Request.Method == "GET"
            && response.Url.Contains($"/api/logs/task/{taskId}", StringComparison.Ordinal));
        await watchButton.ClickAsync();
        var response = await historyResponse.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(response.Ok, Is.True, $"Task log history returned HTTP {response.Status}.");
        await Expect(Page.GetByText("Connected", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(logPanel.Locator(".log-entry").Filter(new() { HasText = "Demo seed record" }))
            .ToBeVisibleAsync(new() { Timeout = 10000 });

        await moreActions.ClickAsync();
        await Page.GetByText("Clear", new() { Exact = true }).ClickAsync();
        await Expect(logPanel.Locator(".log-entry")).ToHaveCountAsync(0);
        await Expect(logPanel.GetByText("No log entries yet.", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = 5000 });
    }
}
