// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerCertbotSectionComplexTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCertbotSectionComplexTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeServer(List<CertbotCertificateDto>? certs = null) => new()
    {
        Id = 1,
        Name = "web-01",
        Certbot = new CertbotDataDto
        {
            IsInstalled = true,
            Certificates = certs ?? []
        }
    };

    private IRenderedComponent<ServerCertbotSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeServer();
        return Render<ServerCertbotSection>(p =>
            p.Add(x => x.ServerId, 1)
             .Add(x => x.Server, s));
    }

    [Fact]
    public void Renders_Empty()
    {
        var cut = RenderSection();
        // Installed certbot with zero certs: heading + the "Installed" status badge render.
        Assert.Contains("Certbot", cut.Markup);
        Assert.Contains("Installed", cut.Markup);
    }

    [Fact]
    public void Renders_WithCertificates()
    {
        var server = MakeServer(certs:
        [
            new CertbotCertificateDto { Name = "example.com", Domains = ["example.com", "www.example.com"], ExpiryDate = DateTime.UtcNow.AddDays(60) }
        ]);
        var cut = RenderSection(server);
        // The single certificate's name is rendered in the certificates grid.
        Assert.Contains("example.com", cut.Markup);
    }

    [Fact]
    public void FilteredCertificates_WithSearch()
    {
        var server = MakeServer(certs:
        [
            new CertbotCertificateDto { Name = "example.com", Domains = ["example.com"], ExpiryDate = DateTime.UtcNow.AddDays(60) },
            new CertbotCertificateDto { Name = "api.test.com", Domains = ["api.test.com"], ExpiryDate = DateTime.UtcNow.AddDays(30) }
        ]);
        var cut = RenderSection(server);
        typeof(ServerCertbotSection).GetField("_certSearch", Priv)!.SetValue(cut.Instance, "api");
        var filtered = (List<CertbotCertificateDto>)typeof(ServerCertbotSection).GetProperty("FilteredCertificates", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(filtered);
    }

    [Fact]
    public async Task ExecuteActionAsync_Success()
    {
        var cut = RenderSection();
        var method = typeof(ServerCertbotSection).GetMethod("ExecuteActionAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [CertbotAction.RenewAll, null, null])!);
        // The action toggles _actionRunning on entry and clears it in the finally block.
        Assert.False((bool)typeof(ServerCertbotSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    // Hardened rule #2/#3: the create form now lives in ServerCertbotCreateDialog, rendered
    // in a SEPARATE host. Never WaitForState on dialog content. Start OpenCreateDialog
    // UN-AWAITED, close the dialog immediately, then await; assert only that OnOpen fired.
    [Fact]
    public async Task OpenCreateDialog_OpensDialog()
    {
        var cut = RenderSection();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ServerCertbotSection).GetMethod("OpenCreateDialog", Priv)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    // Hardened rule #4: render the dialog DIRECTLY (in-render-tree, no separate-host hang)
    // and assert the form renders. Default (webroot off) → no webroot path input.
    [Fact]
    public void CreateDialog_RendersForm()
    {
        var cut = Render<ServerCertbotCreateDialog>(p => p.Add(x => x.ServerId, 1));
        Assert.Contains("Domains", cut.Markup);
        Assert.Contains("Email", cut.Markup);
        Assert.Contains("UseWebroot", cut.Markup);
    }

    // Dialog Cancel button is wired and clickable (closes via OmniDialogService in the real flow).
    [Fact]
    public void CreateDialog_CancelButtonWired()
    {
        var cut = Render<ServerCertbotCreateDialog>(p => p.Add(x => x.ServerId, 1));
        cut.FindAll("button").First(b => b.TextContent.Contains("GoBack")).Click();
        // Cancel closes the dialog without submitting - no create POST is ever issued.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("certbot/create"));
    }

    [Fact]
    public void CertbotResourceNames_ReturnsNames()
    {
        var server = MakeServer(certs:
        [
            new CertbotCertificateDto { Name = "cert1", Domains = ["a.com"], ExpiryDate = DateTime.UtcNow.AddDays(30) },
            new CertbotCertificateDto { Name = "cert2", Domains = ["b.com"], ExpiryDate = DateTime.UtcNow.AddDays(60) }
        ]);
        var cut = RenderSection(server);
        var names = (List<string>)typeof(ServerCertbotSection).GetProperty("CertbotResourceNames", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, names.Count);
    }

    // Hardened rule #4: with webroot toggled on, the dialog renders the webroot path input.
    [Fact]
    public void CreateDialog_WithWebroot_ShowsWebrootPath()
    {
        var cut = Render<ServerCertbotCreateDialog>(p => p.Add(x => x.ServerId, 1));
        typeof(ServerCertbotCreateDialog).GetField("_webroot", Priv)!.SetValue(cut.Instance, true);
        cut.Render(p => p.Add(x => x.ServerId, 1));
        Assert.Contains("WebrootPath", cut.Markup);
    }

    [Fact]
    public void Renders_MultipleCertificates()
    {
        var server = MakeServer(certs:
        [
            new CertbotCertificateDto { Name = "cert1", Domains = ["a.com"], ExpiryDate = DateTime.UtcNow.AddDays(5) },
            new CertbotCertificateDto { Name = "cert2", Domains = ["b.com", "c.com"], ExpiryDate = DateTime.UtcNow.AddDays(90) },
            new CertbotCertificateDto { Name = "cert3", Domains = ["expired.com"], ExpiryDate = DateTime.UtcNow.AddDays(-3) }
        ]);
        var cut = RenderSection(server);
        // All three certificate names render in the grid.
        Assert.Contains("cert1", cut.Markup);
        Assert.Contains("cert2", cut.Markup);
        Assert.Contains("cert3", cut.Markup);
    }
}
