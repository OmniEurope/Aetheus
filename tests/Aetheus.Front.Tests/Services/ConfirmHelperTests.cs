// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

/// <summary> ConfirmHelper asks its question through the real <see cref="OmniOverlayService"/>,
/// shown by a real <see cref="OmniComponentsHost"/>, and answers with the button the user presses.
/// </summary>
public sealed class ConfirmHelperTests : BunitContext
{
    private readonly IRenderedComponent<OmniComponentsHost> _host;
    private readonly ConfirmHelper _helper;

    public ConfirmHelperTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddScoped<ConfirmHelper>();
        var overlay = Services.GetRequiredService<OmniOverlayService>();
        _host = Render<OmniComponentsHost>(parameters => parameters.Add(host => host.OverlayService, overlay));
        _helper = Services.GetRequiredService<ConfirmHelper>();
    }

    [Fact]
    public void ConfirmDeleteAsync_NoArgs_AsksWithTheDeleteTitleAndARedDeleteAction()
    {
        _ = _helper.ConfirmDeleteAsync("DeleteServerConfirm");

        _host.WaitForAssertion(() => Assert.Single(_host.FindAll(".omni-dialog")));
        Assert.Equal("ConfirmDelete", _host.Find(".omni-dialog__title").TextContent);
        Assert.Equal("DeleteServerConfirm", _host.Find(".omni-confirm__message").TextContent);
        var action = _host.Find("button.omni-confirm__action");
        Assert.Equal("Delete", action.TextContent.Trim());
        Assert.Contains("omni-button--danger", action.ClassList);
        Assert.Equal("GoBack", _host.Find("button.omni-confirm__cancel").TextContent.Trim());
        Assert.Contains(_host.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Delete);
    }

    [Fact]
    public void ConfirmDeleteAsync_WithArgs_FormatsMessage()
    {
        _ = _helper.ConfirmDeleteAsync("Delete {0}?", "ConfirmDelete", "web-01");

        _host.WaitForAssertion(() => Assert.Equal("Delete web-01?", _host.Find(".omni-confirm__message").TextContent));
    }

    [Fact]
    public void ConfirmDeleteAsync_CustomTitleKey_UsesGivenTitle()
    {
        _ = _helper.ConfirmDeleteAsync("RemoveConfirm", "RemoveTitle");

        _host.WaitForAssertion(() => Assert.Equal("RemoveTitle", _host.Find(".omni-dialog__title").TextContent));
    }

    [Fact]
    public void ConfirmAsync_NoArgs_AsksWithTheConfirmTitleAndABlueVerbAction()
    {
        _ = _helper.ConfirmAsync("Promote", "AreYouSure");

        _host.WaitForAssertion(() => Assert.Equal("Confirm", _host.Find(".omni-dialog__title").TextContent));
        Assert.Equal("AreYouSure", _host.Find(".omni-confirm__message").TextContent);
        var action = _host.Find("button.omni-confirm__action");
        // The confirm button says its verb, the dismiss button "Revenir" (GoBack).
        Assert.Equal("Promote", action.TextContent.Trim());
        Assert.Contains("omni-button--primary", action.ClassList);
        Assert.Equal("GoBack", _host.Find("button.omni-confirm__cancel").TextContent.Trim());
    }

    [Fact]
    public void ConfirmAsync_WithArgs_FormatsMessage_UnderTheGivenTitle()
    {
        _ = _helper.ConfirmAsync("Promote", "Promote {0}?", "Promote", "v1.2.3");

        _host.WaitForAssertion(() => Assert.Equal("Promote v1.2.3?", _host.Find(".omni-confirm__message").TextContent));
        Assert.Equal("Promote", _host.Find(".omni-dialog__title").TextContent);
    }

    [Fact]
    public async Task ConfirmDeleteAsync_TheActionPressed_AnswersTrue_AndClosesTheDialog()
    {
        var answer = _helper.ConfirmDeleteAsync("DeleteServerConfirm");

        _host.WaitForElement("button.omni-confirm__action").Click();

        Assert.True(await answer);
        _host.WaitForAssertion(() => Assert.Empty(_host.FindAll(".omni-dialog")));
    }

    [Fact]
    public async Task ConfirmAsync_CancelPressed_AnswersFalse()
    {
        var answer = _helper.ConfirmAsync("Promote", "AreYouSure");

        _host.WaitForElement("button.omni-confirm__cancel").Click();

        Assert.False(await answer);
    }

    [Fact]
    public async Task ConfirmDeleteAsync_ClosedWithTheCross_AnswersFalse_NotNull()
    {
        // The former library answered null when the dialog was dismissed; OE answers false. Every caller (project
        // deletion, removing a user from a role) tests "!= true", which reads both the same.
        var answer = _helper.ConfirmDeleteAsync("DeleteServerConfirm");

        _host.WaitForElement("button.omni-dialog__close").Click();

        Assert.Equal(false, await answer);
    }
}
