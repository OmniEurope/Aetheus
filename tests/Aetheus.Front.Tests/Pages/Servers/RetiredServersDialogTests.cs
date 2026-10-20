// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;
using ServersPage = Aetheus.Front.Components.Servers.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Retired servers have a separate list where permanent deletion requires
/// server-administrator permission.</summary>
public sealed class RetiredServersDialogTests : BunitContext
{
    private BunitTestHelper.TestHandler _handler = null!;

    private static PaginatedResult<RetiredServerDto> OneRetired() => new()
    {
        Items =
        [
            new RetiredServerDto
            {
                Id = 4,
                Name = "vps2577917",
                Hostname = "vps2577917",
                RetiredAt = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Local),
                HasMachineIdentity = true
            }
        ],
        TotalCount = 1
    };

    private IRenderedComponent<RetiredServersDialog> RenderDialog(bool isAdmin)
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/retired", OneRetired());
        var cut = Render<RetiredServersDialog>();
        cut.WaitForAssertion(() => Assert.Contains("vps2577917", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void ListsRetiredServers_WithTheHintThatAReinstallRestoresThem()
    {
        var cut = RenderDialog(isAdmin: true);

        Assert.Contains("RetiredServersHint", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MachineIdentityKnown", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(_handler.Requests, request => request.Method == "GET" && request.Url.Contains("api/servers/retired", StringComparison.Ordinal));
    }

    [Fact]
    public void PermanentDeletion_IsOfferedToAServerAdminOnly()
    {
        var admin = RenderDialog(isAdmin: true);
        Assert.Single(admin.FindAll("button[aria-label='PurgeServer']"));
    }

    [Fact]
    public void PermanentDeletion_IsNotOfferedWithoutServerAdmin()
    {
        var reader = RenderDialog(isAdmin: false);
        Assert.Empty(reader.FindAll("button[aria-label='PurgeServer']"));
    }

    [Fact]
    public async Task ConfirmedPurge_CallsThePermanentEndpoint_AndDropsTheRow()
    {
        var cut = RenderDialog(isAdmin: true);
        _handler.SetResponse(HttpMethod.Delete, "api/servers/4/permanent", System.Net.HttpStatusCode.NoContent);
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/retired", new PaginatedResult<RetiredServerDto>());

        await cut.InvokeAsync(() => cut.Instance.PurgeServerConfirmedAsync(OneRetired().Items[0]));

        Assert.Contains(_handler.Requests, request => request.Method == "DELETE" && request.Url.EndsWith("api/servers/4/permanent", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal(0, cut.FindComponent<AetheusDataGrid<RetiredServerDto>>().Instance.Count));
        var notification = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Success, notification.Severity);
    }

    [Fact]
    public async Task RefusedPurge_KeepsTheRow_AndSaysSo()
    {
        var cut = RenderDialog(isAdmin: true);
        _handler.SetResponse(HttpMethod.Delete, "api/servers/4/permanent", System.Net.HttpStatusCode.Conflict);

        await cut.InvokeAsync(() => cut.Instance.PurgeServerConfirmedAsync(OneRetired().Items[0]));

        cut.WaitForAssertion(() => Assert.Contains("vps2577917", cut.Markup, StringComparison.Ordinal));
        var notification = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, notification.Severity);
    }

    [Fact]
    public void ServersPage_OffersTheRetiredServersList_AndRetiresInsteadOfDeleting()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 5, Name = "vps-other", Hostname = "vps-other" }],
            TotalCount = 1
        });
        var cut = Render<ServersPage>();
        cut.WaitForAssertion(() => Assert.Contains("vps-other", cut.Markup, StringComparison.Ordinal));
        var opened = new List<string>();
        Services.GetRequiredService<OmniDialogService>().OnOpen += (title, _, _, _) => opened.Add(title ?? string.Empty);

        cut.Find(".omni-overflow-menu__trigger").Click();
        cut.FindAll(".omni-menu__item").Single(item => item.TextContent.Contains("RetiredServers", StringComparison.Ordinal)).Click();

        Assert.Equal(["RetiredServers"], opened);
        // STD-BTN detection 11: retiring a server is an entry of the row's "More actions" menu.
        Assert.NotNull(cut.FindMenuItem("RetireServer"));
    }
}
