// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Rspamd installation, lifecycle and thresholds from the spam tab.</summary>
public sealed class ServerMailSpamTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailSpamTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private IRenderedComponent<ServerMailSpamTab> RenderTab(MailSpamFilterStateDto spam) =>
        Render<ServerMailSpamTab>(p => p.Add(x => x.Spam, spam).Add(x => x.ServerId, 10).Add(x => x.CanManage, true));

    [Fact]
    public void WithoutSpamFilter_OffersTheInstallation()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/spam/install", new MailTaskQueuedDto { TaskId = 81 });
        var cut = RenderTab(new MailSpamFilterStateDto());

        cut.FindAll("button").First(b => b.TextContent.Contains("MailInstallSpamFilter", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/spam/install", StringComparison.Ordinal)));
    }

    [Fact]
    public void Thresholds_ArePutAsEntered()
    {
        _dialog.OpenResult = new MailDialogModel { Mode = MailDialogMode.SpamThresholds, RejectScore = 20, AddHeaderScore = 8.5, GreylistScore = 5 };
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Put, "api/servers/10/mail/spam", new MailTaskQueuedDto { TaskId = 82 });
        var cut = RenderTab(new MailSpamFilterStateDto { Name = "rspamd", IsInstalled = true, IsRunning = true, RejectScore = 15, AddHeaderScore = 6, GreylistScore = 4 });

        Assert.Contains("15", cut.Markup, StringComparison.Ordinal);
        // Thresholds sits in the "More actions" menu beside Learn (STD-BTN detection 11).
        cut.FindMenuItem("MailSpamThresholds")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Method == "PUT" && r.Url.EndsWith("mail/spam", StringComparison.Ordinal) && r.Body!.Contains("8.5", StringComparison.Ordinal)));
    }

    [Fact]
    public void StoppingTheFilter_QueuesTheTypedAction()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 83 });
        var cut = RenderTab(new MailSpamFilterStateDto { Name = "rspamd", IsInstalled = true, IsRunning = true });

        // Stop sits in the "More actions" menu beside Start (STD-BTN detection 11).
        cut.FindMenuItem("Stop")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Url.EndsWith("mail/action", StringComparison.Ordinal)
            && r.Body!.Contains($"\"action\":{(int)MailAction.StopSpamFilter}", StringComparison.Ordinal)));
    }
}
