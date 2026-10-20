// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The collector reports the outbound delivery facts that explain a deferral (IP policy, retry
/// timings, deferred entries with their reason) and the global IPv6 addresses of the host, bounded.</summary>
public sealed class MailDeliveryCollectorTests
{
    private const string Reason = "host gmail-smtp-in.l.google.com[142.250.1.27] said: 421-4.7.28 Gmail has detected an unusually high rate of mail";

    private static string Json(string queue, string id, string address, string reason) =>
        "{\"queue_name\": \"" + queue + "\", \"queue_id\": \"" + id + "\", \"arrival_time\": 1792400000, \"message_size\": 1234, "
        + "\"sender\": \"a@example.com\", \"recipients\": [{\"address\": \"" + address + "\", \"delay_reason\": \"" + reason + "\"}]}\n";

    private static IShellRunner Shell(string jsonQueue, string textQueue = "Mail queue is empty\n", int jsonExit = 0)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "which" || x == "where"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/x\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "mail_version" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "mail_version = 3.7.11\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_domains")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "example.com\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_maps")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0,
                "myhostname = mail.example.com\ninet_protocols = ipv4\nsmtp_address_preference = any\n"
                + "minimal_backoff_time = 60s\nmaximal_backoff_time = 900s\nqueue_run_delay = 60s\n", string.Empty));
        shell.RunExecAsync("dovecot", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "2.3.19.1 (9b53102964)\n", string.Empty));
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "active\n", string.Empty));
        shell.RunExecAsync("postqueue", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-j" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(jsonExit, jsonQueue, string.Empty));
        shell.RunExecAsync("postqueue", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-p" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, textQueue, string.Empty));
        return shell;
    }

    private static Task<MailDataDto> CollectAsync(IShellRunner shell, params IPAddress[] hostAddresses) =>
        new MailCollector(NullLogger<MailCollector>.Instance, shell,
                fileExists: path => path == "/etc/postfix/main.cf",
                readText: _ => null,
                hostAddresses: () => hostAddresses)
            .CollectAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Collect_ReportsThePostfixIpPolicyAndRetryTimings()
    {
        var data = await CollectAsync(Shell("Mail queue is empty\n"));

        Assert.Equal(("ipv4", "any", "60s", "900s", "60s"),
            (data.Delivery.InetProtocols, data.Delivery.SmtpAddressPreference, data.Delivery.MinimalBackoffTime,
                data.Delivery.MaximalBackoffTime, data.Delivery.QueueRunDelay));
        Assert.Empty(data.Delivery.Deferred);
    }

    [Fact]
    public async Task Collect_ReadsTheDeferredEntriesWithTheirReasonAndIgnoresTheActiveQueue()
    {
        var output = Json("deferred", "4F2A1B3C9D", "bob@gmail.com", Reason) + Json("active", "AAAAAA1111", "x@example.org", "none");

        var data = await CollectAsync(Shell(output));

        var item = Assert.Single(data.Delivery.Deferred);
        Assert.Equal(("4F2A1B3C9D", "bob@gmail.com", Reason), (item.QueueId, item.Recipient, item.Reason));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792400000).UtcDateTime, item.ArrivalTime);
    }

    [Fact]
    public async Task Collect_FallsBackToThePlainListing_WhenQueueJsonIsNotSupported()
    {
        const string text =
            "-Queue ID-  --Size-- ----Arrival Time---- -Sender/Recipient-------\n"
            + "4F2A1B3C9D      1234 Tue Oct 20 10:00:00  a@example.com\n"
            + "(host gmail-smtp-in.l.google.com[142.250.1.27] said: 421-4.7.28 rate limited)\n"
            + "                                         bob@gmail.com\n"
            + "                                         carol@gmail.com\n"
            + "\n-- 2 Kbytes in 1 Request.\n";

        var data = await CollectAsync(Shell(string.Empty, text, jsonExit: 1));

        Assert.Equal(["bob@gmail.com", "carol@gmail.com"], data.Delivery.Deferred.Select(d => d.Recipient));
        Assert.All(data.Delivery.Deferred, d =>
        {
            Assert.Equal("4F2A1B3C9D", d.QueueId);
            Assert.Contains("421-4.7.28", d.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ParseQueueText_SkipsActiveAndHeldMessages()
    {
        const string text =
            "ACTIVE1111*     100 Tue Oct 20 10:00:00  a@example.com\n"
            + "                                         x@example.org\n"
            + "HELD222222!     100 Tue Oct 20 10:00:00  a@example.com\n"
            + "                                         y@example.org\n"
            + "DEFER33333      100 Tue Oct 20 10:00:00  a@example.com\n"
            + "(connect to mx.example.net[192.0.2.1]:25: Connection timed out)\n"
            + "                                         z@example.net\n";

        var items = MailDeliveryParser.ParseQueueText(text);

        var item = Assert.Single(items);
        Assert.Equal(("DEFER33333", "z@example.net"), (item.QueueId, item.Recipient));
    }

    [Fact]
    public void ParseQueueJson_BoundsTheCountAndTheReasonLength_AndSkipsGarbage()
    {
        var output = "not json\n{broken\n" + string.Concat(Enumerable.Range(0, 50)
            .Select(i => Json("deferred", $"ID{i:D6}", $"u{i}@gmail.com", new string('x', 5000))));

        var items = MailDeliveryParser.ParseQueueJson(output);

        Assert.Equal(MailInventoryLimits.MaxDeferredMessages, items.Count);
        Assert.All(items, i => Assert.Equal(MailInventoryLimits.MaxDeferralReasonLength, i.Reason.Length));
    }

    [Fact]
    public async Task Collect_ReportsOnlyGlobalIpv6Addresses()
    {
        var data = await CollectAsync(Shell("Mail queue is empty\n"),
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("203.0.113.25"),
            IPAddress.IPv6Loopback,
            IPAddress.Parse("fe80::1%2"),
            IPAddress.Parse("fd12:3456:789a::1"),
            IPAddress.Parse("::ffff:203.0.113.25"),
            IPAddress.Parse("2001:db8:abcd::25"),
            IPAddress.Parse("2001:db8:abcd::25"));

        Assert.Equal(["2001:db8:abcd::25"], data.Delivery.HostIpv6Addresses);
    }

    [Fact]
    public async Task Collect_ReportsNoIpv6_WhenTheHostHasNoGlobalAddress()
    {
        var data = await CollectAsync(Shell("Mail queue is empty\n"), IPAddress.Parse("203.0.113.25"), IPAddress.Parse("fe80::1"));

        Assert.Empty(data.Delivery.HostIpv6Addresses);
    }
}
