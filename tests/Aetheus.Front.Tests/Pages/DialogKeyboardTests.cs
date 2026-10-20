// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;
using HelpCenterPage = Aetheus.Front.Components.Help.HelpCenter;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Enter and Escape on the name dialogs: Enter closes with the trimmed value (never an empty
/// one), Escape closes without a value, any other key does nothing.</summary>
public sealed class DialogKeyboardTests : BunitContext
{
    public DialogKeyboardTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Fact]
    public async Task DashboardName_Enter_ClosesWithTheTrimmedName()
    {
        var cut = Render<DashboardNameDialog>(p => p.Add(c => c.InitialName, "  Ops  "));

        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(1, Dialog.CloseCount);
        Assert.Equal("Ops", Dialog.LastCloseResult);
    }

    [Fact]
    public async Task DashboardName_EnterOnABlankName_KeepsTheDialogOpen()
    {
        var cut = Render<DashboardNameDialog>(p => p.Add(c => c.InitialName, "   "));

        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(0, Dialog.CloseCount);
    }

    [Fact]
    public async Task DashboardName_Escape_ClosesWithoutAValue()
    {
        var cut = Render<DashboardNameDialog>(p => p.Add(c => c.InitialName, "Ops"));

        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, Dialog.CloseCount);
        Assert.Null(Dialog.LastCloseResult);
    }

    [Fact]
    public async Task DashboardName_AnotherKey_DoesNothing()
    {
        var cut = Render<DashboardNameDialog>(p => p.Add(c => c.InitialName, "Ops"));

        await cut.Find("input").KeyDownAsync(new KeyboardEventArgs { Key = "a" });

        Assert.Equal(0, Dialog.CloseCount);
    }

    [Fact]
    public async Task SecretValue_Enter_ClosesWithTheGeneratedValue()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "DB_PASSWORD"));
        await cut.Find(".omni-split-button__main").ClickAsync(new());
        var generated = cut.Find("#secret-value-input").GetAttribute("value");

        await cut.Find("#secret-value-input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(1, Dialog.CloseCount);
        Assert.Equal(generated, Dialog.LastCloseResult);
        Assert.Matches("^[0-9a-f]{48}$", (string)Dialog.LastCloseResult!);
    }

    [Fact]
    public async Task SecretValue_EnterWithNoValue_KeepsTheDialogOpen()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "DB_PASSWORD"));

        await cut.Find("#secret-value-input").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(0, Dialog.CloseCount);
    }

    [Fact]
    public async Task SecretValue_Escape_ClosesWithoutAValue()
    {
        var cut = Render<SecretValueDialog>(p => p.Add(x => x.SecretKey, "DB_PASSWORD"));

        await cut.Find("#secret-value-input").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, Dialog.CloseCount);
        Assert.Null(Dialog.LastCloseResult);
    }
}
