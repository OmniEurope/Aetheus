// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The queue tab shows why mail is deferred without a shell on the host: the IPv4 and global IPv6
/// addresses, the Postfix IP policy, the retry timings and the deferred entries with their reason.</summary>
public sealed class ServerMailDeliveryPanelTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailDeliveryPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static MailDeliveryStateDto Reported() => new()
    {
        InetProtocols = "ipv4",
        SmtpAddressPreference = "any",
        MinimalBackoffTime = "60s",
        MaximalBackoffTime = "900s",
        QueueRunDelay = "60s",
        HostIpv6Addresses = ["2001:db8:abcd::25"],
        Deferred =
        [
            new MailDeferredMessageDto { QueueId = "4F2A1B3C9D", Recipient = "bob@gmail.com", Reason = "said: 421-4.7.28 rate limited" }
        ]
    };

    [Fact]
    public void Panel_ShowsAddressesPolicyRetryAndDeferredReason()
    {
        var cut = Render<ServerMailDeliveryPanel>(p => p.Add(x => x.Delivery, Reported()).Add(x => x.HostIpv4, "203.0.113.25"));

        var text = cut.Markup;
        Assert.Contains("203.0.113.25", text, StringComparison.Ordinal);
        Assert.Contains("2001:db8:abcd::25", text, StringComparison.Ordinal);
        Assert.Contains("ipv4", text, StringComparison.Ordinal);
        Assert.Contains("60s / 900s / 60s", text, StringComparison.Ordinal);
        Assert.Contains("bob@gmail.com", text, StringComparison.Ordinal);
        Assert.Contains("421-4.7.28", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_SaysThereIsNoIpv6_AndNothingWasReported_ForAnOlderAgent()
    {
        var cut = Render<ServerMailDeliveryPanel>(p => p.Add(x => x.Delivery, new MailDeliveryStateDto()).Add(x => x.HostIpv4, "203.0.113.25"));

        Assert.Contains("MailDeliveryNoIpv6", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MailDeliveryNotReported", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MailDeferredEmpty", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueTab_GreysTheRetryAction_WhenTheHelperIsOlderThanSix()
    {
        var cut = Render<ServerMailQueueTab>(p => p.Add(x => x.ServerId, 10).Add(x => x.CanManage, true)
            .Add(x => x.HelperVersion, ServerMailQueueTab.RetryTuningMinimumHelperVersion - 1));

        var item = cut.FindMenuItem("MailApplyRetryTuning")!;

        Assert.True(item.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true");
    }

    [Fact]
    public void QueueTab_HostsThePanelAndOffersTheRetryActionInItsMenu()
    {
        var cut = Render<ServerMailQueueTab>(p => p.Add(x => x.ServerId, 10).Add(x => x.CanManage, true)
            .Add(x => x.Delivery, Reported()).Add(x => x.HostIpv4, "203.0.113.25"));

        Assert.Contains("2001:db8:abcd::25", cut.Markup, StringComparison.Ordinal);
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 90 });

        // Load stays the visible action; flush and the retry tuning sit in the "More actions" menu (R3-41).
        _dialog.ConfirmResult = false;
        cut.FindMenuItem("MailApplyRetryTuning")!.Click();
        Assert.DoesNotContain(_handler.RequestDetails, r => r.Url.EndsWith("mail/action", StringComparison.Ordinal));

        _dialog.ConfirmResult = true;
        cut.FindMenuItem("MailApplyRetryTuning")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Url.EndsWith("mail/action", StringComparison.Ordinal)
            && r.Body!.Contains($"\"action\":{(int)MailAction.ApplyRetryTuning}", StringComparison.Ordinal)));
        Assert.NotNull(cut.FindMenuItem("FlushQueue"));
    }
}
