// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Playwright;

namespace Aetheus.E2E;

[Category("E2E")]
[Category("Agents")]
[RollbackRelevant]
public class AgentEnrollmentTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    public async Task AgentWizard_OpensAndGeneratesRegistrationToken()
    {
        await Page.GotoAsync($"{FrontendUrl}/servers");
        await Page.WaitForSelectorAsync(".omni-data-grid", new() { Timeout = 10000 });
        var addBtn = Page.Locator("button:has-text('Add')").First;
        await addBtn.ClickAsync();
        await Expect(Page.Locator(".wizard-dialog, .omni-dialog")).ToBeVisibleAsync();
        await Page.WaitForSelectorAsync(".wizard-dialog", new() { Timeout = 10000 });

        // The token is minted only on the Install step (step 2). Step 0 = Platform: clicking a platform
        // card auto-advances to Capabilities (step 1); Next then reaches Install, which calls the
        // CreateRegistrationToken API and renders the token block.
        await Page.Locator(".omni-selectable-card").First.ClickAsync();
        await Page.Locator(".omni-wizard__next").ClickAsync();

        // The token block renders as .wizard-token-code (NOT .wizard-token-block, which never existed)
        // once the token API call returns.
        await Expect(Page.Locator(".wizard-token-code")).ToBeVisibleAsync(new() { Timeout = 15000 });
    }
}
