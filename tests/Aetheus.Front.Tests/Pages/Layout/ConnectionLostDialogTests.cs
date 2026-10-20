// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Pages.Layout;

public class ConnectionLostDialogTests : BunitContext
{
    public ConnectionLostDialogTests() => BunitTestHelper.RegisterServices(this, authenticated: false);

    [Fact]
    public void NotVisible_RendersNothing()
    {
        var cut = Render<ConnectionLostDialog>(ps => ps.Add(p => p.Visible, false));

        Assert.Empty(cut.FindAll(".omni-connection-overlay"));
    }

    [Fact]
    public void Visible_RendersOverlayTitleAndReconnectButton()
    {
        var cut = Render<ConnectionLostDialog>(ps => ps.Add(p => p.Visible, true));

        Assert.Single(cut.FindAll(".omni-connection-overlay"));
        Assert.Single(cut.FindAll(".omni-connection-overlay__icon svg"));
        Assert.Equal("alertdialog", cut.Find(".omni-connection-overlay__card").GetAttribute("role"));
        // StubLocalizer echoes the resource key, so the presence of these keys proves the strings render.
        Assert.Contains("ConnectionLost", cut.Markup);
        Assert.Single(cut.FindAll(".omni-connection-overlay__action"));
    }

    [Fact]
    public void Countdown_ShowsOnlyWhenPositive_InASlotThatStays()
    {
        var atZero = Render<ConnectionLostDialog>(ps => ps
            .Add(p => p.Visible, true)
            .Add(p => p.ReconnectCountdown, 0));
        // The slot stays so the card does not move; at zero it holds no text.
        var slot = atZero.Find(".omni-connection-overlay__countdown");
        Assert.Equal("true", slot.GetAttribute("aria-hidden"));
        Assert.DoesNotContain("0", slot.TextContent, StringComparison.Ordinal);

        var counting = Render<ConnectionLostDialog>(ps => ps
            .Add(p => p.Visible, true)
            .Add(p => p.ReconnectCountdown, 5));
        Assert.Contains("5", counting.Find(".omni-connection-overlay__countdown").TextContent);
    }

    [Fact]
    public void ManualReconnectButton_InvokesCallback()
    {
        var clicked = false;
        var cut = Render<ConnectionLostDialog>(ps => ps
            .Add(p => p.Visible, true)
            .Add(p => p.OnManualReconnect, EventCallback.Factory.Create(this, () => clicked = true)));

        cut.Find(".omni-connection-overlay__action").Click();

        Assert.True(clicked);
    }
}
