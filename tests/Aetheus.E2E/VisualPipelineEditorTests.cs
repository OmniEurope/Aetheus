// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Pipelines")]
[RollbackRelevant]
public sealed class VisualPipelineEditorTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        await NavigateToAsync("pipelines");

        var pipeline = Page.Locator(".pipeline-name-link")
            .Filter(new() { HasText = "toto-ci" })
            .First;
        await Expect(pipeline).ToBeVisibleAsync(new() { Timeout = 10000 });
        await pipeline.ClickAsync();
        await Expect(Page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex(@"/pipelines/\d+"),
            new() { Timeout = 10000 });

        // The button prefixes the accessible name with the icon text
        // ("account_tree Visual"), so locate the tab by its exact visible label.
        var visualTab = Page.GetByRole(AriaRole.Tab)
            .Filter(new() { Has = Page.GetByText("Visual", new() { Exact = true }) });
        await Expect(visualTab).ToBeVisibleAsync(new() { Timeout = 10000 });
        await visualTab.ClickAsync();
        await Expect(Page.Locator(".vp-canvas-outer")).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.Locator(".vp-node").First).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Test]
    public async Task VisualEditor_DragPanAndPersistedPosition_WorkAcrossReload()
    {
        var canvas = Page.Locator(".vp-canvas-outer");
        var inner = Page.Locator(".vp-canvas-inner");
        var node = Page.Locator(".vp-node").First;
        var handle = node.Locator(".vp-node-drag-handle");

        var initialNodeStyle = await node.GetAttributeAsync("style");
        var handleBox = await handle.BoundingBoxAsync();
        Assert.That(handleBox, Is.Not.Null, "The first visual node must expose a real drag handle.");

        await Page.Mouse.MoveAsync(handleBox!.X + handleBox.Width / 2, handleBox.Y + handleBox.Height / 2);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(handleBox.X + handleBox.Width / 2 + 90, handleBox.Y + handleBox.Height / 2 + 55,
            new() { Steps = 8 });
        await Page.Mouse.UpAsync();

        await Expect(node).Not.ToHaveAttributeAsync("style", initialNodeStyle ?? string.Empty,
            new() { Timeout = 5000 });
        var movedNodeStyle = await node.GetAttributeAsync("style");
        Assert.That(movedNodeStyle, Is.Not.Null.And.Not.EqualTo(initialNodeStyle));
        var movedCoordinates = await node.EvaluateAsync<double[]>("""
            element => [
                parseFloat(element.style.getPropertyValue('--node-x')),
                parseFloat(element.style.getPropertyValue('--node-y'))
            ]
            """);

        // pointerup invokes the .NET callback asynchronously. Waiting only for the CSS move proves
        // the drag handler ran, but not that OnNodeMoved reached its store; an immediate reload can
        // therefore race the persistence callback. Since R1-66 the layout is a user preference kept on
        // the server (VisualPipelineLayoutStore, PreferenceKeys.VisualLayout), not a localStorage entry:
        // wait until the server answers the moved position.
        var pipelineIdMatch = System.Text.RegularExpressions.Regex.Match(Page.Url, @"/pipelines/(\d+)");
        Assert.That(pipelineIdMatch.Success, Is.True, "The visual editor URL must expose its pipeline id.");
        // PreferenceKeys.VisualLayout, joined so that no literal reads as a CSS class selector.
        var layoutKey = string.Join('.', "aetheus", "pipelines", pipelineIdMatch.Groups[1].Value, "visual-layout");
        await Page.WaitForFunctionAsync("""
            async expected => {
                const token = localStorage.getItem('aetheus_auth_token');
                const response = await fetch(`${expected.backendUrl}/api/users/me/preferences/${encodeURIComponent(expected.key)}`, {
                    headers: { 'Authorization': `Bearer ${token}` }
                });
                if (response.status !== 200) return false;
                const preference = await response.json();
                const raw = preference.value ?? preference.Value;
                if (!raw) return false;
                const blob = JSON.parse(raw);
                const point = (blob.pos ?? blob.Pos)?.['0'];
                return Array.isArray(point)
                    && Math.abs(point[0] - expected.x) < 0.01
                    && Math.abs(point[1] - expected.y) < 0.01;
            }
            """, new { backendUrl = BackendUrl, key = layoutKey, x = movedCoordinates[0], y = movedCoordinates[1] },
            new() { Timeout = 5000, PollingInterval = 200 });

        var canvasBox = await canvas.BoundingBoxAsync();
        Assert.That(canvasBox, Is.Not.Null, "The visual canvas must have a measurable viewport.");
        var transformBeforePan = await inner.EvaluateAsync<string>("element => element.style.transform");
        // Start in the empty top-left canvas margin. The bottom-right point can
        // fall on the component scrollbars, which consume the pointer sequence before
        // visualPipeline's canvas handler sees it.
        await Page.Mouse.MoveAsync(canvasBox!.X + 18, canvasBox.Y + 18);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(canvasBox.X + 98, canvasBox.Y + 68,
            new() { Steps = 6 });
        await Page.Mouse.UpAsync();
        var transformAfterPan = await inner.EvaluateAsync<string>("element => element.style.transform");
        Assert.That(transformAfterPan, Is.Not.EqualTo(transformBeforePan));

        await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForBlazorAsync();
        await Expect(Page.Locator(".vp-node").First).ToBeVisibleAsync(new() { Timeout = 10000 });

        var persistedCoordinates = await Page.Locator(".vp-node").First.EvaluateAsync<double[]>("""
            element => [
                parseFloat(element.style.getPropertyValue('--node-x')),
                parseFloat(element.style.getPropertyValue('--node-y'))
            ]
            """);
        Assert.Multiple(() =>
        {
            Assert.That(persistedCoordinates[0], Is.EqualTo(movedCoordinates[0]).Within(0.01),
                "A manually dragged node must keep its persisted X coordinate after a real page reload.");
            Assert.That(persistedCoordinates[1], Is.EqualTo(movedCoordinates[1]).Within(0.01),
                "A manually dragged node must keep its persisted Y coordinate after a real page reload.");
        });
    }
}
