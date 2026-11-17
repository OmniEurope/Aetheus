// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// PLAN-005 lot 4 (R-21): a block of the build planner's night view is moved with a real mouse drag, or through the calendar's own move
/// button (pick up, then "place here", the keyboard-and-touch path of OmniScheduler, which reports the same
/// move as a drag), and the plan read again after a reload shows the new time. The pipeline is a test pipeline
/// created and deleted through the API; its schedule is 22:00 Paris time, so its block is always in the evening
/// part of the night grid whatever the hour the test runs at.
/// </summary>
[Category("E2E")]
[Category("Pipelines")]
public class PipelineScheduleMoveTests : E2ETestBase
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    public override BrowserNewContextOptions ContextOptions()
    {
        var options = base.ContextOptions();
        options.TimezoneId = "Europe/Paris";
        return options;
    }

    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [TestCase(false)]
    // R-3 (2026-11-05): the mouse case is back once OmniScheduler lets the pointer through a dragged block only after its drag runs
    // (OE f7b448b3): Chromium 1243 abandoned a drag whose source turned transparent to the pointer during dragstart.
    [TestCase(true)]
    [NewSinceDeployedBaseline("R-21")]
    public async Task MovingABlockOfTheNightView_WritesTheNewTime_AndThePlanReadAfterAReloadShowsIt(bool withTheMouse)
    {
        using var http = AuthenticatedClient();
        var projectId = await FirstProjectIdAsync(http);
        var organizationId = await DefaultOrganizationIdAsync(http);
        var name = $"e2e-plan-{Guid.NewGuid():N}"[..20];
        // 22:00 Paris time, written in UTC: what the engine reads and what the planner converts back.
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Paris).Date;
        var utc = TimeZoneInfo.ConvertTimeToUtc(today.AddHours(22), Paris);
        var movedUtc = TimeZoneInfo.ConvertTimeToUtc(today.AddHours(22).AddMinutes(30), Paris);
        var yaml = $$"""
            name: {{name}}
            trigger: schedule
            schedule: "0 {{utc.Minute}} {{utc.Hour}} * * *"
            stages:
              - name: Orchestrate
                steps:
                  - name: call-missing
                    type: trigger
                    pipeline: e2e-pipeline-that-does-not-exist
            """;
        using var created = await http.PostAsJsonAsync($"{BackendUrl}/api/pipelines", new { name, projectId, yamlDefinition = yaml });
        created.EnsureSuccessStatusCode();
        var pipelineId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        try
        {
            await NavigateToAsync("pipelines/schedule");
            var night = Page.Locator("#schedule-night");
            var block = night.Locator($"[data-omni-scheduler-appointment^='{pipelineId}|']");
            await Expect(block).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Expect(block).ToHaveAttributeAsync("data-start", new Regex(@"T22:00:00"));

            var halfPastRow = night.Locator("tr", new() { Has = Page.Locator("time[datetime='22:30']") });
            if (withTheMouse)
            {
                // The one-hour block covers its own half past: it is taken by its top, and the mouse is moved by hand,
                // because OmniScheduler lets the pointer through the block only once the drag has begun.
                var from = await block.BoundingBoxAsync() ?? throw new InvalidOperationException("The block has no box.");
                var x = from.X + (from.Width / 2);
                await RecordDragEventsAsync();
                try
                {
                    await Page.Mouse.MoveAsync(x, from.Y + 3);
                    await Page.Mouse.DownAsync();
                    await Page.Mouse.MoveAsync(x, from.Y + 12, new() { Steps = 3 });
                    // The drag is on once the scheduler has drawn it so (its blocks then let the pointer through), and
                    // the drop target is the one under the pointer once the scheduler has marked it: each is waited for
                    // as a state of the page, so a drop that does not write says which of the two never happened.
                    await Expect(night).ToHaveClassAsync(new Regex("omni-scheduler--dragging"), new() { Timeout = 5000 });
                    // R-25: the cell is measured now, once the drag is drawn, and again after the move: the page can shift while
                    // the drag begins or under load (the full -ta run of 2026-11-09 logged a drag enter on a cell of the grid but the
                    // 22:30 cell never took the drop mark), so a box read before the drag could point at the cell beside it.
                    var halfPastCell = halfPastRow.Locator(".omni-scheduler-grid__cell");
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        var to = await halfPastCell.BoundingBoxAsync() ?? throw new InvalidOperationException("The half past row has no box.");
                        await Page.Mouse.MoveAsync(x, to.Y + (to.Height / 2), new() { Steps = 5 });
                        var settled = await halfPastCell.BoundingBoxAsync() ?? throw new InvalidOperationException("The half past row has no box.");
                        if (Math.Abs(settled.Y - to.Y) < 1 && Math.Abs(settled.Height - to.Height) < 1) break;
                    }

                    await Expect(halfPastCell).ToHaveClassAsync(new Regex("omni-scheduler__drop"), new() { Timeout = 5000 });
                    await Page.Mouse.UpAsync();
                    await Expect(Page.Locator($"[data-testid='schedule-card-{pipelineId}'] [data-testid='schedule-frequency']"))
                        .ToContainTextAsync("10:30", new() { Timeout = 15000 });
                }
                catch (Exception exception) when (exception is PlaywrightException or AssertionException)
                {
                    // What the browser did of the drag, in order: the cause of a drop that wrote nothing.
                    throw new AssertionException($"{exception.Message}{System.Environment.NewLine}Drag events seen by the page:{System.Environment.NewLine}{await ReadDragEventsAsync()}", exception);
                }
            }
            else
            {
                await block.Locator(".omni-scheduler__move").ClickAsync();
                var halfPast = halfPastRow.Locator(".omni-scheduler__place");
                await Expect(halfPast).ToHaveCountAsync(1);
                await halfPast.ClickAsync();
            }

            // The write went through the page's one path: the card says the new time at once.
            await Expect(Page.Locator($"[data-testid='schedule-card-{pipelineId}'] [data-testid='schedule-frequency']"))
                .ToContainTextAsync("10:30", new() { Timeout = 15000 });

            // The plan read again, from a fresh page: the server holds the new time, the block is drawn there.
            await Page.ReloadAsync();
            await Expect(Page.Locator($"#schedule-night [data-omni-scheduler-appointment^='{pipelineId}|']"))
                .ToHaveAttributeAsync("data-start", new Regex(@"T22:30:00"), new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
            var plan = await http.GetFromJsonAsync<JsonElement>(
                $"{BackendUrl}/api/pipelines/schedule-plan?organizationId={organizationId}"
                + $"&from={Uri.EscapeDataString(DateTime.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTime.UtcNow.AddDays(2).ToString("O"))}");
            var task = plan.GetProperty("tasks").EnumerateArray().Single(item => item.GetProperty("pipelineId").GetInt32() == pipelineId);
            Assert.That(
                (task.GetProperty("hour").GetInt32(), task.GetProperty("minute").GetInt32()),
                Is.EqualTo((movedUtc.Hour, movedUtc.Minute)),
                "The engine's UTC time is the dropped 22:30 Paris time, not the old one.");
        }
        finally
        {
            using var deleted = await http.DeleteAsync($"{BackendUrl}/api/pipelines/{pipelineId}");
        }
    }

    /// <summary>Records, in the page, each drag event the browser fires and what the page did with it (bubble phase on the window).</summary>
    private async Task RecordDragEventsAsync() => await Page.EvaluateAsync("""
        () => {
            window.__dragLog = [];
            for (const type of ['dragstart', 'dragenter', 'dragover', 'drop', 'dragend']) {
                window.addEventListener(type, event => {
                    const last = window.__dragLog[window.__dragLog.length - 1];
                    if (type === 'dragover' && last && last.startsWith('dragover')) return;
                    const target = event.target instanceof Element ? event.target.tagName + '.' + String(event.target.className).split(' ')[0] : '?';
                    // The time of the grid row the event is over, so a drop mark that lands beside the expected cell is named.
                    const row = event.target instanceof Element ? event.target.closest('tr') : null;
                    const time = row ? (row.querySelector('time')?.getAttribute('datetime') ?? '?') : '-';
                    window.__dragLog.push(type + ' on ' + target + ' row=' + time + ' defaultPrevented=' + event.defaultPrevented);
                });
            }
        }
        """);

    private async Task<string> ReadDragEventsAsync()
    {
        try
        {
            var lines = await Page.EvaluateAsync<string[]>("() => window.__dragLog || []");
            return lines.Length == 0 ? "(no drag event reached the page)" : string.Join(System.Environment.NewLine, lines);
        }
        catch (PlaywrightException exception)
        {
            return $"(the drag log could not be read: {exception.Message})";
        }
    }

    private static HttpClient AuthenticatedClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        using var storageState = JsonDocument.Parse(E2EAuthSession.StorageStateJson!);
        var token = storageState.RootElement.GetProperty("origins")[0]
            .GetProperty("localStorage")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "aetheus_auth_token")
            .GetProperty("value")
            .GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<int> FirstProjectIdAsync(HttpClient http)
    {
        var projects = await http.GetFromJsonAsync<JsonElement>($"{BackendUrl}/api/projects?page=1&pageSize=1");
        var items = projects.GetProperty("items");
        if (items.GetArrayLength() == 0)
            throw new InvalidOperationException("The E2E database has no project to attach a pipeline to.");
        return items[0].GetProperty("id").GetInt32();
    }
}
