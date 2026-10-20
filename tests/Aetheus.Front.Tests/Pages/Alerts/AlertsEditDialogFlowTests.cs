// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using AlertsPage = Aetheus.Front.Components.Alerts.Alerts;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The alert rules page opens one edit dialog for a new rule and for an existing one; only a dialog
/// that saved (answered true) reloads the rules, a dismissed one leaves the list as it was.</summary>
public sealed class AlertsEditDialogFlowTests : BunitContext
{
    private readonly ImmediateDialogService _dialogs;
    private int _reads;

    public AlertsEditDialogFlowTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        handler.SetAsyncJsonResponse(HttpMethod.Get, "api/alerts", _ =>
        {
            _reads++;
            List<AlertRuleDto> rules = _reads == 1
                ? [new AlertRuleDto { Id = 4, Name = "cpu-high" }]
                : [new AlertRuleDto { Id = 4, Name = "cpu-high" }, new AlertRuleDto { Id = 5, Name = "disk-full" }];
            return Task.FromResult(rules);
        });
    }

    private IRenderedComponent<AlertsPage> RenderLoaded()
    {
        var cut = Render<AlertsPage>();
        cut.WaitForAssertion(() => Assert.Contains("cpu-high", cut.Markup));
        return cut;
    }

    private static Task ClickCreateAsync(IRenderedComponent<AlertsPage> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

    [Fact]
    public async Task ASavedNewRule_ReloadsTheRules()
    {
        var cut = RenderLoaded();
        _dialogs.OpenResult = true;

        await ClickCreateAsync(cut);

        Assert.Equal(typeof(AlertEditDialog), _dialogs.LastComponent);
        Assert.Equal("Create", _dialogs.LastTitle);
        Assert.Null(_dialogs.LastParameters!["Alert"]);
        cut.WaitForAssertion(() => Assert.Contains("disk-full", cut.Markup));
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task ADismissedDialog_LeavesTheRulesUnread()
    {
        var cut = RenderLoaded();
        _dialogs.OpenResult = null;

        await ClickCreateAsync(cut);

        Assert.Equal(1, _dialogs.OpenCount);
        Assert.Equal(1, _reads);
        Assert.DoesNotContain("disk-full", cut.Markup);
    }

    [Fact]
    public async Task EditingARule_OpensTheDialogOnThatRule()
    {
        var cut = RenderLoaded();
        _dialogs.OpenResult = false;

        await cut.Find("button[title='Edit']").ClickAsync(new());

        Assert.Equal("Edit", _dialogs.LastTitle);
        Assert.Equal(4, Assert.IsType<AlertRuleDto>(_dialogs.LastParameters!["Alert"]).Id);
        Assert.Equal(1, _reads);
    }
}
