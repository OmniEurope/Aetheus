// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerFirewallSectionTests : BunitContext
{
    private BunitTestHelper.TestHandler? _handler;

    private static ServerFirewallDto Firewall(bool active, bool cap) => new()
    {
        Installed = true,
        Active = active,
        StatusKnown = true,
        FirewallManagementAvailable = cap,
        Rules =
        [
            new FirewallRuleDto { Number = 1, Port = 22, Protocol = "tcp", Action = "allow", Source = "Anywhere", Raw = "22/tcp ALLOW Anywhere" },
            new FirewallRuleDto { Number = 2, Port = 8080, Protocol = "tcp", Action = "deny", Source = "10.0.0.0/8", Raw = "8080/tcp DENY 10.0.0.0/8" }
        ]
    };

    private IRenderedComponent<ServerFirewallSection> RenderWith(ServerFirewallDto dto, bool? cap, bool immediateDialogs = false)
    {
        _handler = BunitTestHelper.RegisterServices(this);
        if (immediateDialogs) BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, "firewall", dto);
        return Render<ServerFirewallSection>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.FirewallManagementAvailable, cap));
    }

    [Fact]
    public void ActiveWithCapability_RendersRulesAndActiveBadge()
    {
        var cut = RenderWith(Firewall(active: true, cap: true), cap: true);

        Assert.Contains("22", cut.Markup);
        Assert.Contains("8080", cut.Markup);
        Assert.Contains("FirewallActive", cut.Markup);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.LockOpen); // add-rule form present when capable
    }

    [Fact]
    public void WithoutCapability_ShowsManageDisabledHint_AndNoForm()
    {
        var cut = RenderWith(Firewall(active: true, cap: false), cap: false);

        Assert.Contains("FirewallManageDisabledHint", cut.Markup);
        // The add-rule form is absent. Read by its buttons, not by the LockOpen icon: the OE grid now
        // draws one too, on its frozen-column toggle.
        Assert.DoesNotContain(cut.FindComponents<OmniButton>(), button => button.Markup.Contains("FirewallAddRule", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "firewall/allow")]
    [InlineData(1, "firewall/deny")]
    public async Task AddRule_IsOneSubmit_AndTheSelectedActionChoosesAllowOrDeny(int option, string endpoint)
    {
        var cut = RenderWith(Firewall(active: true, cap: true), cap: true);
        _handler!.SetJsonResponse(HttpMethod.Post, endpoint, new ServiceTaskResponse { TaskId = 4 });
        var form = cut.Find("form");

        // One submit button; Autoriser and Refuser are the options of the action selector.
        Assert.Single(form.QuerySelectorAll("button[type=submit]"));
        var options = form.QuerySelectorAll(".omni-select-bar__item");
        Assert.Equal(["FirewallAllow", "FirewallDeny"], options.Select(o => o.TextContent.Trim()).ToArray());
        await options[option].ClickAsync(new());
        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/servers/1/firewall/", StringComparison.Ordinal)));
        Assert.Contains(_handler.RequestDetails, r => r.Method == "POST" && r.Url.EndsWith(endpoint, StringComparison.Ordinal));
    }

    [Fact]
    public void NotInstalled_ShowsNotAvailable()
    {
        var cut = RenderWith(new ServerFirewallDto { Installed = false }, cap: null);

        Assert.Contains("FirewallNotInstalledTitle", cut.Markup);
    }

    [Fact]
    public void StatusUnknown_ShowsDiagnosticInsteadOfInactiveStateOrControls()
    {
        var cut = RenderWith(new ServerFirewallDto
        {
            Installed = true,
            StatusKnown = false,
            CollectionDiagnostics = "ufw status could not be read",
            FirewallManagementAvailable = true
        }, cap: true);

        Assert.Contains("FirewallStatusUnknown", cut.Markup);
        Assert.Contains("ufw status could not be read", cut.Markup);
        Assert.DoesNotContain("FirewallInactive", cut.Markup);
        Assert.DoesNotContain("lock_open", cut.Markup);
    }

    [Fact]
    public void SameServerParentRerender_DoesNotReloadFirewall()
    {
        var cut = RenderWith(Firewall(active: true, cap: true), cap: true);

        cut.Render(parameters => parameters
            .Add(component => component.ServerId, 1)
            .Add(component => component.FirewallManagementAvailable, false));

        Assert.Single(_handler!.Requests, request =>
            request.Method == "GET" && request.Url.Contains("firewall"));
    }

    [Fact]
    public async Task DeleteRule_UnknownProtocol_DoesNotSendRequest()
    {
        var cut = RenderWith(Firewall(active: true, cap: true), cap: true);
        var method = typeof(ServerFirewallSection).GetMethod(
            "DeleteRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance,
            [new FirewallRuleDto { Port = 8080, Protocol = "all", Source = "Anywhere", Raw = "8080 ALLOW Anywhere" }])!);

        Assert.DoesNotContain(_handler!.Requests, request => request.Method == "DELETE");
    }

    private ImmediateDialogService ImmediateDialogs() =>
        (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private static Task ClickAsync(IRenderedComponent<ServerFirewallSection> cut, string label) =>
        cut.FindAll("button").First(button => button.TextContent.Contains(label, StringComparison.Ordinal)).ClickAsync(new());

    [Theory]
    [InlineData(false, 0)]
    [InlineData(null, 0)]
    [InlineData(true, 1)]
    public async Task Disabling_AsksFirst_AndOnlyAConfirmedAnswerQueuesIt(bool? answer, int expectedPosts)
    {
        var cut = RenderWith(Firewall(active: true, cap: true), cap: true, immediateDialogs: true);
        var dialogs = ImmediateDialogs();
        dialogs.ConfirmResult = answer;
        _handler!.SetJsonResponse(HttpMethod.Post, "firewall/toggle", new ServiceTaskResponse { TaskId = 9 });

        await ClickAsync(cut, "FirewallDisable");

        Assert.Equal("FirewallDisableConfirm", dialogs.LastConfirmMessage);
        Assert.True(dialogs.LastConfirmOptions!.Destructive);
        var posts = _handler.RequestDetails.Where(request => request.Method == "POST" && request.Url.Contains("api/servers/1/firewall/toggle")).ToList();
        Assert.Equal(expectedPosts, posts.Count);
        if (expectedPosts == 1)
        {
            Assert.Contains("\"enabled\":false", posts[0].Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "FirewallDisableQueued");
        }
    }

    [Fact]
    public async Task Enabling_QueuesWithoutAsking_AndAnUnqueuedTaskIsReportedAsAFailure()
    {
        var cut = RenderWith(Firewall(active: false, cap: true), cap: true, immediateDialogs: true);
        var dialogs = ImmediateDialogs();
        _handler!.SetResponse(HttpMethod.Post, "firewall/toggle", System.Net.HttpStatusCode.InternalServerError);

        await ClickAsync(cut, "FirewallEnable");

        Assert.Equal(0, dialogs.OpenCount);
        var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains("firewall/toggle"));
        Assert.Contains("\"enabled\":true", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "FirewallActionFailed");
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }
}
