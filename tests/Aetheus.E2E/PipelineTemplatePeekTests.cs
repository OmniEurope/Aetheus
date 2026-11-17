// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// R-35: in the pipeline editor's YAML tab (Monaco, in its own frame), a Ctrl+Click on the <c>name@version</c> of an
/// <c>extends:</c> line opens the read-only definition peek on that exact template version, and the template's own
/// <c>extends:</c> line, shown in the peek, is clickable to follow the chain. The test pipeline and its two test
/// templates (a child that extends a base) are created and deleted through the API, so no seeded template is relied on.
/// What it proves end to end, in a real browser: Monaco's link click reaches the .NET editor ref with the
/// reference decoded (<c>name@1</c>, not <c>name%401</c>), and the peek resolves it through the real templates API.
/// </summary>
[Category("E2E")]
[Category("Pipelines")]
public class PipelineTemplatePeekTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    [NewSinceDeployedBaseline("R-35")]
    public async Task CtrlClickOnAnExtendsReference_OpensThatTemplateVersionInThePeek_AndItsOwnExtendsIsFollowable()
    {
        using var http = AuthenticatedClient();
        var projectId = await FirstProjectIdAsync(http);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var baseName = $"e2e-r35-base-{suffix}";
        var childName = $"e2e-r35-child-{suffix}";
        var pipelineName = $"e2e-r35-pipe-{suffix}";
        var templateIds = new List<int>();
        int? pipelineId = null;
        try
        {
            // The base is created at version 1 and updated once, so the chain pins an older version (@1) than the latest:
            // the peek must show the pinned version, not the latest one.
            var baseId = await CreateTemplateAsync(http, baseName, "name: base\nstages: []");
            templateIds.Add(baseId);
            using (var updated = await http.PutAsJsonAsync($"{BackendUrl}/api/pipelines/templates/{baseId}", new
            {
                name = baseName,
                description = string.Empty,
                category = "e2e",
                yamlContent = "name: base\nstages: []\n# second version",
                changelogEntry = "second version"
            }))
            {
                await EnsureSuccessAsync(updated);
            }

            var childId = await CreateTemplateAsync(http, childName, $"name: child\nextends: {baseName}@1\nstages: []");
            templateIds.Add(childId);

            var yaml = $"name: {pipelineName}\nextends: {childName}@1\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: echo r35\n";
            using var created = await http.PostAsJsonAsync($"{BackendUrl}/api/pipelines", new { name = pipelineName, projectId, yamlDefinition = yaml });
            await EnsureSuccessAsync(created);
            pipelineId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

            // The editor validates its YAML when it loses focus; the same call as the editor makes must accept this definition.
            using (var validated = await http.PostAsJsonAsync($"{BackendUrl}/api/pipelines/validate", new { yaml }))
            {
                await EnsureSuccessAsync(validated);
            }

            await NavigateToAsync($"pipelines/{pipelineId}?tab=edit");
            var frame = Page.FrameLocator("iframe.monaco-editor-frame");
            var extendsLine = frame.Locator(".view-line").Filter(new() { HasText = $"{childName}@1" });
            await Expect(extendsLine).ToHaveCountAsync(1, new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });

            // Monaco's link gesture: with Control held, moving over a link marks it (class detected-link-active), and the click
            // then opens it. A bare Playwright Ctrl+Click left the peek unopened in 2 runs of 3 (measured); the likely cause is
            // that it clicks before Monaco has computed the links (not isolated), so the click waits for the mark, which is
            // the state Monaco itself requires before it opens a link.
            // The line holds the text before Monaco has tokenized and laid out its spans, and a bounding box read in that
            // window is null (seen under the full suite). Wait for the reference token itself to be visible, which is the
            // state the box needs, instead of reading the box of an element that is not rendered yet.
            var referenceToken = extendsLine.Locator("span > span").Last;
            await Expect(referenceToken).ToBeVisibleAsync(new() { Timeout = PlaywrightConfig.AppReadyTimeoutMs });
            var reference = await referenceToken.BoundingBoxAsync()
                ?? throw new InvalidOperationException("The extends reference has no box.");
            var x = reference.X + (reference.Width / 2);
            var y = reference.Y + (reference.Height / 2);
            var activeLink = frame.Locator(".detected-link-active");
            await Page.Keyboard.DownAsync("Control");
            try
            {
                for (var attempt = 0; attempt < 40 && await activeLink.CountAsync() == 0; attempt++)
                {
                    await Page.Mouse.MoveAsync(x + (attempt % 2), y);
                    await Page.WaitForTimeoutAsync(250);
                }

                await Expect(activeLink).ToHaveCountAsync(1, new() { Timeout = 1000 });
                await Page.Mouse.ClickAsync(x, y);
            }
            finally
            {
                await Page.Keyboard.UpAsync("Control");
            }

            var peek = Page.Locator("[data-testid='template-peek']");
            await Expect(peek).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Expect(peek).ToContainTextAsync($"{childName}@1");
            await Expect(peek).ToContainTextAsync($"extends: {baseName}@1");

            // The child's own `extends:` line is a link of the peek: following it shows the base at the pinned version 1.
            var chainLink = peek.Locator(".yaml-pipeline-link", new() { HasTextString = $"{baseName}@1" });
            await Expect(chainLink).ToHaveCountAsync(1);
            // Moving focus from the editor to the peek makes the editor validate its YAML (on blur). That call is waited for
            // here, while the templates still exist: left in flight, it reached the API after the cleanup below had deleted
            // them and the page logged a 400 (measured on the first runs of this test).
            var validation = await Page.RunAndWaitForResponseAsync(
                async () => await chainLink.ClickAsync(),
                response => response.Url.EndsWith("/api/pipelines/validate", StringComparison.Ordinal),
                new() { Timeout = 15000 });
            Assert.That(validation.Status, Is.EqualTo(200), "The editor's own validation accepts the YAML that extends the template.");

            await Expect(peek.Locator("strong").First).ToHaveTextAsync($"{baseName}@1");
            await Expect(peek).ToContainTextAsync("name: base");
            await Expect(peek).Not.ToContainTextAsync("second version");
        }
        finally
        {
            if (pipelineId is { } id)
                using (await http.DeleteAsync($"{BackendUrl}/api/pipelines/{id}")) { }
            foreach (var templateId in Enumerable.Reverse(templateIds))
                using (await http.DeleteAsync($"{BackendUrl}/api/pipelines/templates/{templateId}")) { }
        }
    }

    private static async Task<int> CreateTemplateAsync(HttpClient http, string name, string yamlContent)
    {
        using var response = await http.PostAsJsonAsync($"{BackendUrl}/api/pipelines/templates", new
        {
            name,
            description = string.Empty,
            category = "e2e",
            yamlContent
        });
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Like EnsureSuccessStatusCode, but the failure carries the response body (the API's validation message).</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
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
