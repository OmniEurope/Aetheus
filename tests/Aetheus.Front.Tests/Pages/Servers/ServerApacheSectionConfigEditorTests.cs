// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Opening a site's configuration from the Apache section: a queued read leaves the editor waiting for
/// the agent's answer (the content arrives later over SignalR, a timeout guards it), a refused read stops
/// the wait at once and says so; either way the editor dialog opens on that site.
/// </summary>
public sealed class ServerApacheSectionConfigEditorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerApacheSectionConfigEditorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static ServerDetailDto Server() => new()
    {
        Id = 30,
        Name = "apache-srv",
        Hostname = "10.0.0.3",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Apache = new ApacheDataDto
        {
            Modules = [],
            VirtualHosts =
            [
                new ApacheVirtualHostDto { ServerName = "example.com", Port = 80, IsEnabled = true, DocumentRoot = "/var/www/html", ConfigFile = "/etc/apache2/sites-available/example.conf" }
            ]
        }
    };

    private async Task<ApacheConfigEditorModel> OpenEditorAsync()
    {
        var cut = Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.Server, Server())
            .Add(section => section.ServerId, 30));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title='EditConfig']")));

        await cut.FindAll("button[title='EditConfig']")[0].ClickAsync(new());

        Assert.Equal(typeof(ApacheConfigEditorDialog), _dialog.LastComponent);
        Assert.Equal("EditConfig - example.conf", _dialog.LastTitle);
        return Assert.IsType<ApacheConfigEditorModel>(Assert.Contains("Model", _dialog.LastParameters!));
    }

    [Fact]
    public async Task AQueuedRead_KeepsTheEditorWaitingForTheAgent_WithoutAnError()
    {
        _handler.SetResponse(HttpMethod.Get, "api/servers/30/apache/vhosts/example.conf/config", System.Net.HttpStatusCode.Accepted);

        var model = await OpenEditorAsync();

        Assert.Equal("example.conf", model.SiteName);
        Assert.True(model.Loading);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public async Task ARefusedRead_StopsTheWait_AndReportsTheFailure()
    {
        _handler.SetResponse(HttpMethod.Get, "api/servers/30/apache/vhosts/example.conf/config", System.Net.HttpStatusCode.Conflict);

        var model = await OpenEditorAsync();

        Assert.False(model.Loading);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);
    }
}
