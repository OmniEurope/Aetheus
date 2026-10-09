// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Pipelines")]
public sealed class MonacoCspTests : E2ETestBase
{
    [Test]
    public async Task Editor_RendersInsideItsOwnPolicy_WhileTheApplicationRemainsStrict()
    {
        await LoginAsync();
        await NavigateToAsync("pipelines/1");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Edit" }).ClickAsync();

        var frame = Page.Locator("iframe.monaco-editor-frame").First;
        await Expect(frame).ToBeVisibleAsync(new() { Timeout = 10000 });
        var editor = Page.FrameLocator("iframe.monaco-editor-frame").Locator(".monaco-editor");
        await Expect(editor).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page.FrameLocator("iframe.monaco-editor-frame").Locator(".view-line").First)
            .ToBeVisibleAsync(new() { Timeout = 10000 });
        var yaml = await Page.EvaluateAsync<string>("""
            () => document.querySelector('iframe.monaco-editor-frame')
                .contentWindow.monacoInterop.getValue('monaco-root')
            """);
        Assert.That(yaml, Is.Not.Empty);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Show Diff" }).ClickAsync();
        await Expect(Page.Locator("iframe.monaco-editor-frame")).ToHaveCountAsync(2);
        await Expect(Page.Locator("iframe.monaco-editor-frame").Nth(1)).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("""
            () => Array.from(document.querySelectorAll('iframe.monaco-editor-frame'))
                .every(frame => frame.contentWindow?.document.querySelector('.monaco-editor'))
            """);

        // E2E-001: both policies are response headers of the production static server
        // (deploy/docker, MonacoFramePolicy). The local development host serves neither, and the
        // launchers say so through E2E_REQUIRE_FRONTEND_SECURITY_HEADERS=false, as for
        // ProductionSmokeTests; the editor rendering above is asserted on both hosts.
        if (!PlaywrightConfig.RequireFrontendSecurityHeaders)
        {
            TestContext.Progress.WriteLine(
                "Monaco rendered in its frame; CSP headers are outside the local development host's scope.");
            return;
        }

        var shellResponse = await Page.APIRequest.GetAsync(FrontendUrl);
        var frameResponse = await Page.APIRequest.GetAsync($"{FrontendUrl}/monaco-frame.html");
        Assert.Multiple(() =>
        {
            Assert.That(shellResponse.Headers["content-security-policy"], Does.Contain("style-src-attr 'none'"));
            // R-526: the shell allows no inline style at all; only the editor's own document does.
            Assert.That(shellResponse.Headers["content-security-policy"], Does.Not.Contain("unsafe-inline"));
            Assert.That(frameResponse.Headers["content-security-policy"], Does.Contain("style-src-attr 'unsafe-inline'"));
            Assert.That(frameResponse.Headers["content-security-policy"], Does.Contain("frame-ancestors 'self'"));
            Assert.That(frameResponse.Headers["x-frame-options"], Is.EqualTo("SAMEORIGIN"));
        });
    }
}
