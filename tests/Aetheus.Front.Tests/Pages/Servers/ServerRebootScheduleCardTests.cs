// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-8 (option B): the editor of a server's daily reboot schedule on the server page. The tests run
/// the card against the HTTP double (real ApiClient, real request bodies); the backend side is proved by its own HTTP
/// integration tests.</summary>
public sealed class ServerRebootScheduleCardTests : BunitContext
{
    private const string Endpoint = "api/servers/5/reboot-schedule";
    private BunitTestHelper.TestHandler _handler = default!;

    private ImmediateDialogService Dialogs => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ServerRebootScheduleDto Schedule(bool enabled = true, bool canManage = true, bool agentCanReboot = true) => new()
    {
        ServerId = 5,
        ServerName = "vps-1",
        Enabled = enabled,
        Hour = 4,
        Minute = 30,
        TimeZoneId = "Europe/Paris",
        Version = 2,
        NextRunUtc = new DateTime(2026, 11, 8, 3, 30, 0, DateTimeKind.Utc),
        CanManage = canManage,
        AgentCanReboot = agentCanReboot
    };

    private IRenderedComponent<ServerRebootScheduleCard> RenderCard(ServerRebootScheduleDto? existing, bool isAdmin = true)
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        BunitTestHelper.UseImmediateDialogs(this);
        if (existing is null) _handler.SetResponse(HttpMethod.Get, Endpoint, HttpStatusCode.NoContent);
        else _handler.SetJsonResponse(HttpMethod.Get, Endpoint, existing);
        var cut = Render<ServerRebootScheduleCard>(p => p.Add(c => c.ServerId, 5).Add(c => c.ServerName, "vps-1"));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static AngleSharp.Dom.IElement? ByTestId(IRenderedComponent<ServerRebootScheduleCard> cut, string id) =>
        cut.FindAll($"[data-testid='{id}']").FirstOrDefault();

    private static async Task ClickMenuItemAsync(IRenderedComponent<ServerRebootScheduleCard> cut, string label)
    {
        cut.Find(".omni-overflow-menu__trigger").Click();
        var item = cut.FindAll(".omni-menu__item").Single(entry => entry.TextContent.Contains(label, StringComparison.Ordinal));
        await item.ClickAsync(new());
    }

    private SaveServerRebootScheduleRequest SentBody(string method)
    {
        var sent = Assert.Single(_handler.RequestDetails, request =>
            request.Method == method && request.Url.Contains(Endpoint, StringComparison.Ordinal));
        return JsonSerializer.Deserialize<SaveServerRebootScheduleRequest>(sent.Body!, JsonSerializerOptions.Web)!;
    }

    [Fact]
    public void WithoutASchedule_SaysSo_AndOffersOnlyToScheduleOne()
    {
        var cut = RenderCard(existing: null);

        Assert.NotNull(ByTestId(cut, "server-reboot-none"));
        Assert.NotNull(ByTestId(cut, "server-reboot-schedule"));
        Assert.Null(ByTestId(cut, "server-reboot-edit"));
        Assert.Empty(cut.FindAll(".omni-overflow-menu__trigger"));
    }

    [Fact]
    public void WithoutASchedule_AReaderWhoDoesNotAdministerTheServerGetsNoAction()
    {
        var cut = RenderCard(existing: null, isAdmin: false);

        Assert.NotNull(ByTestId(cut, "server-reboot-none"));
        Assert.Null(ByTestId(cut, "server-reboot-schedule"));
    }

    [Fact]
    public void ASchedule_ShowsItsTimeStateAndPlannedReboot_WithOneVisibleAction_AndTheOthersInTheMenu()
    {
        var cut = RenderCard(Schedule());

        // The test localizer answers the key itself, so the sentences are asserted by key (their arguments are not covered here).
        Assert.Equal("ServerRebootDaily", ByTestId(cut, "server-reboot-when")!.TextContent.Trim());
        Assert.Equal("Enabled", ByTestId(cut, "server-reboot-state")!.TextContent.Trim());
        Assert.Equal("ServerRebootPlanned", ByTestId(cut, "server-reboot-outcome")!.TextContent.Trim());
        Assert.Null(ByTestId(cut, "server-reboot-no-capability"));
        // One visible action; enabling and deleting are in the menu, not beside it.
        Assert.Single(cut.FindAll("button[data-testid]"), button => button.GetAttribute("data-testid")!.StartsWith("server-reboot-", StringComparison.Ordinal));
        Assert.Single(cut.FindAll(".omni-overflow-menu__trigger"));
    }

    [Fact]
    public void AnAgentWithoutTheRebootCapability_IsWarned_WithoutHidingTheSchedule()
    {
        var cut = RenderCard(Schedule(agentCanReboot: false));

        Assert.NotNull(ByTestId(cut, "server-reboot-no-capability"));
        Assert.NotNull(ByTestId(cut, "server-reboot-when"));
    }

    [Fact]
    public void ADisabledSchedule_SaysItIsDisabled()
    {
        var cut = RenderCard(Schedule(enabled: false));

        Assert.Equal("Disabled", ByTestId(cut, "server-reboot-state")!.TextContent.Trim());
    }

    [Fact]
    public void AScheduleTheCallerCannotManage_OffersNoAction()
    {
        var cut = RenderCard(Schedule(canManage: false), isAdmin: false);

        Assert.NotNull(ByTestId(cut, "server-reboot-when"));
        Assert.Null(ByTestId(cut, "server-reboot-edit"));
        Assert.Empty(cut.FindAll(".omni-overflow-menu__trigger"));
    }

    [Fact]
    public void AReadThatFails_SaysSo_InsteadOfClaimingThereIsNoSchedule()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetResponse(HttpMethod.Get, Endpoint, HttpStatusCode.InternalServerError);

        var cut = Render<ServerRebootScheduleCard>(p => p.Add(c => c.ServerId, 5));

        cut.WaitForAssertion(() => Assert.NotNull(ByTestId(cut, "server-reboot-load-failed")));
        Assert.Null(ByTestId(cut, "server-reboot-none"));
        Assert.Null(ByTestId(cut, "server-reboot-schedule"));
    }

    [Fact]
    public async Task SchedulingARebootThroughTheDialog_SendsItsRequest_AndShowsTheSavedSchedule()
    {
        var cut = RenderCard(existing: null);
        Dialogs.OpenResult = new SaveServerRebootScheduleRequest { Hour = 4, Minute = 30, TimeZoneId = "Europe/Paris", Enabled = true };
        _handler.SetJsonResponse(HttpMethod.Put, Endpoint, Schedule());

        await ByTestId(cut, "server-reboot-schedule")!.ClickAsync(new());

        Assert.Equal(typeof(ServerRebootScheduleDialog), Dialogs.LastComponent);
        Assert.Null(Dialogs.LastParameters!["Schedule"]);
        Assert.Equal("ServerRebootDialogCreateTitle", Dialogs.LastTitle);
        var body = SentBody("PUT");
        Assert.Equal((4, 30, "Europe/Paris", true), (body.Hour, body.Minute, body.TimeZoneId, body.Enabled));
        cut.WaitForAssertion(() => Assert.NotNull(ByTestId(cut, "server-reboot-edit")));
        Assert.Null(ByTestId(cut, "server-reboot-none"));
    }

    [Fact]
    public async Task ADismissedDialog_WritesNothing()
    {
        var cut = RenderCard(existing: null);
        Dialogs.OpenResult = null;

        await ByTestId(cut, "server-reboot-schedule")!.ClickAsync(new());

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
        Assert.NotNull(ByTestId(cut, "server-reboot-none"));
    }

    [Fact]
    public async Task ChangingTheSchedule_OpensTheDialogOnTheCurrentOne()
    {
        var current = Schedule();
        var cut = RenderCard(current);
        Dialogs.OpenResult = null;

        await ByTestId(cut, "server-reboot-edit")!.ClickAsync(new());

        Assert.Equal("ServerRebootDialogEditTitle", Dialogs.LastTitle);
        Assert.Equal(current.Hour, Assert.IsType<ServerRebootScheduleDto>(Dialogs.LastParameters!["Schedule"]).Hour);
    }

    [Fact]
    public async Task TheMenuDisablesAnEnabledSchedule_KeepingItsTimeAndZone()
    {
        var cut = RenderCard(Schedule());
        _handler.SetJsonResponse(HttpMethod.Put, Endpoint, Schedule(enabled: false));

        await ClickMenuItemAsync(cut, "ServerRebootDisableAction");

        var body = SentBody("PUT");
        Assert.Equal((4, 30, "Europe/Paris", false), (body.Hour, body.Minute, body.TimeZoneId, body.Enabled));
        cut.WaitForAssertion(() => Assert.Equal("Disabled", ByTestId(cut, "server-reboot-state")!.TextContent.Trim()));
    }

    [Fact]
    public async Task TheMenuEnablesADisabledSchedule()
    {
        var cut = RenderCard(Schedule(enabled: false));
        _handler.SetJsonResponse(HttpMethod.Put, Endpoint, Schedule());

        await ClickMenuItemAsync(cut, "ServerRebootEnableAction");

        Assert.True(SentBody("PUT").Enabled);
        cut.WaitForAssertion(() => Assert.Equal("Enabled", ByTestId(cut, "server-reboot-state")!.TextContent.Trim()));
    }

    [Fact]
    public async Task DeletingTheSchedule_AsksFirst_ThenSendsTheDelete_AndGoesBackToNoSchedule()
    {
        var cut = RenderCard(Schedule());
        Dialogs.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, Endpoint, HttpStatusCode.NoContent);

        await ClickMenuItemAsync(cut, "ServerRebootDeleteAction");

        Assert.True(Dialogs.LastConfirmOptions!.Destructive);
        Assert.Equal("ServerRebootDeleteConfirm", Dialogs.LastConfirmMessage);
        Assert.Single(_handler.RequestDetails, request => request.Method == "DELETE" && request.Url.Contains(Endpoint, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.NotNull(ByTestId(cut, "server-reboot-none")));
    }

    [Fact]
    public async Task ADeleteTheCallerDeclines_SendsNothing()
    {
        var cut = RenderCard(Schedule());
        Dialogs.ConfirmResult = false;

        await ClickMenuItemAsync(cut, "ServerRebootDeleteAction");

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "DELETE");
        Assert.NotNull(ByTestId(cut, "server-reboot-when"));
    }

    [Fact]
    public async Task ADeleteTheBackendRefuses_KeepsTheScheduleOnScreen()
    {
        var cut = RenderCard(Schedule());
        Dialogs.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, Endpoint, HttpStatusCode.Forbidden);

        await ClickMenuItemAsync(cut, "ServerRebootDeleteAction");

        Assert.NotNull(ByTestId(cut, "server-reboot-when"));
        Assert.Null(ByTestId(cut, "server-reboot-none"));
    }
}
