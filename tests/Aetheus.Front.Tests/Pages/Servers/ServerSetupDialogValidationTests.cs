// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// FRONT-019: renders the real setup dialog and submits its form, so the validation the user meets is
/// the one tested (the section tests hand back a dialog result without ever validating it). The form
/// yields before raising OnValidSubmit, so every outcome is awaited, refusals included.
/// </summary>
public class ServerSetupDialogValidationTests : BunitContext
{
    public ServerSetupDialogValidationTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<ServerSetupDialog> RenderDialog(ServerSetupKind kind, ServerSetupDialogModel model)
    {
        var cut = Render<ServerSetupDialog>(p => p.Add(x => x.Kind, kind).Add(x => x.Model, model));
        BunitTestHelper.Interactive(this);
        return cut;
    }

    private ServerSetupDialogModel SubmittedModel(IRenderedComponent<ServerSetupDialog> cut)
    {
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.IsType<ServerSetupDialogModel>(Spy().LastResult));
        return (ServerSetupDialogModel)Spy().LastResult!;
    }

    private static void AssertRefused(IRenderedComponent<ServerSetupDialog> cut, SpyDialogService spy, string field)
    {
        cut.Find("form").Submit();
        var context = cut.FindComponent<EditForm>().Instance.EditContext!;
        cut.WaitForAssertion(() => Assert.NotEmpty(context.GetValidationMessages(context.Field(field))));
        Assert.False(spy.Closed);
    }

    [Fact]
    public void Rkhunter_WithAMailAndNoPorts_SubmitsTheModel()
    {
        var cut = RenderDialog(ServerSetupKind.Rkhunter, new ServerSetupDialogModel());
        cut.Find("input").Input("ops@example.com");

        Assert.Equal("ops@example.com", SubmittedModel(cut).MailOnWarning);
    }

    [Fact]
    public void Rkhunter_WithoutAMail_SubmitsTheModel()
    {
        var cut = RenderDialog(ServerSetupKind.Rkhunter, new ServerSetupDialogModel());

        Assert.Equal(string.Empty, SubmittedModel(cut).MailOnWarning);
    }

    [Fact]
    public void Rkhunter_WithAnInvalidMail_IsRefused()
    {
        var cut = RenderDialog(ServerSetupKind.Rkhunter, new ServerSetupDialogModel());
        cut.Find("input").Input("not-an-address");

        AssertRefused(cut, Spy(), nameof(ServerSetupDialogModel.MailOnWarning));
    }

    [Fact]
    public void Portsentry_WithEmptyPorts_IsRefused()
    {
        var cut = RenderDialog(ServerSetupKind.Portsentry, new ServerSetupDialogModel());

        AssertRefused(cut, Spy(), nameof(ServerSetupDialogModel.TcpPorts));
    }

    [Fact]
    public void Portsentry_WithBothPortLists_SubmitsTheModel()
    {
        var cut = RenderDialog(ServerSetupKind.Portsentry,
            new ServerSetupDialogModel { TcpPorts = "1,7,9", UdpPorts = "1,7" });

        Assert.Equal("1,7,9", SubmittedModel(cut).TcpPorts);
    }
}
