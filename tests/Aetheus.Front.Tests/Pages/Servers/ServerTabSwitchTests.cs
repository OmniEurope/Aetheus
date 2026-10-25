// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.Sections;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-16: going from one tab of a server (Mail) to another (Services) keeps the sheet mounted.</summary>
public class ServerTabSwitchTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTabSwitchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
    }

    /// <summary>What the router does: the layout stays, the page it holds changes with the route.</summary>
    private sealed class TabHost : ComponentBase
    {
        [Parameter] public Type Page { get; set; } = typeof(Mail);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ServerDetailLayout>(0);
            builder.AddAttribute(1, nameof(LayoutComponentBase.Body), (RenderFragment)(body =>
            {
                body.OpenComponent(0, Page);
                body.AddAttribute(1, "Id", 5);
                body.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private static ServerDetailDto MailServer() => new()
    {
        Id = 5,
        Name = "mail-srv",
        Hostname = "10.0.0.5",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        AgentVersion = "1.0.1552",
        Tags = [],
        Services = [],
        Mail = new MailDataDto { IsInstalled = true }
    };

    private int ServerFetches() =>
        _handler.RequestsSnapshot().Count(r => r.Method == "GET" && r.Url.EndsWith("api/servers/5", StringComparison.Ordinal));

    [Fact]
    public async Task SwitchingFromMailToServices_KeepsTheSheet_AndDoesNotFetchTheServerAgain()
    {
        _handler.SetJsonResponse("api/monitoring/servers/5/metrics", new List<ServerMetricDto>());
        _handler.SetJsonResponse("api/servers/5", MailServer());
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/5/mail");

        var cut = Render<TabHost>(p => p.Add(x => x.Page, typeof(Mail)));
        await _handler.WhenGetAsync("api/servers/5");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".omni-detail-shell__loading")), BunitTestHelper.AsyncRenderWait);
        Assert.Equal(1, ServerFetches());

        var seenWithoutServer = false;
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        loader.OnChanged += () => seenWithoutServer |= loader.Server is null;

        nav.NavigateTo("/servers/5/services");
        cut.Render(p => p.Add(x => x.Page, typeof(Aetheus.Front.Components.Servers.Sections.Services)));

        Assert.Empty(cut.FindAll(".omni-detail-shell__loading"));
        Assert.False(seenWithoutServer, "the loader dropped the server while switching tabs");
        Assert.Equal(1, ServerFetches());
    }
}
