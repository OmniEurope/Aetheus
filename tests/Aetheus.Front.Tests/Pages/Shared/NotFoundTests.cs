// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class NotFoundTests : BunitContext
{
    [Fact]
    public void Renders_NotFoundMessage()
    {
        BunitTestHelper.RegisterServices(this);
        var cut = Render<NotFound>();

        Assert.Contains("NotFound", cut.Markup);
        Assert.Contains("NotFoundMessage", cut.Markup);
    }

    [Fact]
    public void TheTitleIsTheHeaderOnly_NotRepeatedInTheBody()
    {
        // A page has one title, the OmniPageHeader's; a second heading under it repeats it.
        BunitTestHelper.RegisterServices(this);
        var cut = Render<NotFound>();

        Assert.Empty(cut.FindAll(".not-found-container .rz-text-h4"));
    }
}

