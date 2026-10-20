// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>Verify setup prerequisites and exact SPF, DKIM and DMARC publication
/// guidance.</summary>
public sealed class MailPrerequisitesTests
{
    private const int ServerId = 9;
    private const string ServerIp = "203.0.113.25";
    private const string Host = "mx.example.com";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly IMailDnsResolver _dns = Substitute.For<IMailDnsResolver>();
    private readonly IMailInventoryRepository _inventory = Substitute.For<IMailInventoryRepository>();
    private readonly IMailRepository _mailRepo = Substitute.For<IMailRepository>();
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly Server _server = new() { Id = ServerId, Name = "vps", Hostname = "vps", IpAddress = ServerIp, MailSetupAvailable = true };
    private readonly MailDiagnosticsService _diagnostics;
    private readonly MailOperationsService _operations;

    public MailPrerequisitesTests()
    {
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>()).Returns(_server);
        _diagnostics = new MailDiagnosticsService(_inventory, _mailRepo, _servers, _dns, new MailDnsVerifier(_dns, _clock), _clock);
        _operations = new MailOperationsService(_mailRepo, _inventory, _servers, Substitute.For<ITaskService>(),
            Substitute.For<ILogService>(), Substitute.For<IAuditService>(), Substitute.For<IEncryptionService>(), _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R2_080_ARecordOnTheServer_AndMatchingReverseDns_AreBothGreen_WithTheirRecords()
    {
        _dns.AddressesAsync(Host, Arg.Any<CancellationToken>()).Returns([ServerIp]);
        _dns.PtrAsync(ServerIp, Arg.Any<CancellationToken>()).Returns([Host]);

        var result = await _diagnostics.CheckPrerequisitesAsync(ServerId, Host, Ct);

        var a = result.Items.Single(i => i.Key == "hostname-dns");
        Assert.Equal((MailCheckVerdict.Ok, Host, "A", ServerIp), (a.Verdict, a.RecordName, a.RecordType, a.RecordValue));
        var ptr = result.Items.Single(i => i.Key == "reverse-dns");
        Assert.Equal((MailCheckVerdict.Ok, ServerIp, "PTR", Host), (ptr.Verdict, ptr.RecordName, ptr.RecordType, ptr.RecordValue));
    }

    [Fact]
    public async Task R2_080_AnARecordElsewhere_AndNoReverseDns_AreRed()
    {
        _dns.AddressesAsync(Host, Arg.Any<CancellationToken>()).Returns(["198.51.100.7"]);
        _dns.PtrAsync(ServerIp, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _diagnostics.CheckPrerequisitesAsync(ServerId, Host, Ct);

        Assert.Equal(MailCheckVerdict.Mismatch, result.Items.Single(i => i.Key == "hostname-dns").Verdict);
        Assert.Equal(MailCheckVerdict.Missing, result.Items.Single(i => i.Key == "reverse-dns").Verdict);
    }

    [Fact]
    public async Task R2_080_AResolverFailure_IsNotGreen()
    {
        _dns.AddressesAsync(Host, Arg.Any<CancellationToken>()).ThrowsAsync(new MailDnsLookupException("SERVFAIL"));
        _dns.PtrAsync(ServerIp, Arg.Any<CancellationToken>()).ThrowsAsync(new MailDnsLookupException("SERVFAIL"));

        var result = await _diagnostics.CheckPrerequisitesAsync(ServerId, Host, Ct);

        Assert.All(result.Items, item => Assert.Equal(MailCheckVerdict.Unknown, item.Verdict));
    }

    [Fact]
    public async Task R2_080_AnInvalidHostName_ReachesNoDns()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _diagnostics.CheckPrerequisitesAsync(ServerId, "mx;id", Ct));

        Assert.Empty(_dns.ReceivedCalls());
    }

    [Fact]
    public async Task R2_080_OutboundProbe_QueuesTheAgentCheckTowardsTheProbeHost()
    {
        _server.AgentCapabilitiesJson = $"[\"{AgentCapabilities.MailOutboundProbe}\",\"shell.execute\"]";

        await _operations.ProbeOutboundSmtpAsync(ServerId, Ct);

        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == ServerId
            && t.Operation == OperationKind.MailOutboundProbe
            && t.Command == MailOutboundProbe.Target), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2_080_OutboundProbe_IsRefusedByNameOnAnAgentTooOld()
    {
        _server.AgentCapabilitiesJson = "[\"shell.execute\"]";

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _operations.ProbeOutboundSmtpAsync(ServerId, Ct));

        Assert.Contains(AgentCapabilities.MailOutboundProbe, error.Message, StringComparison.Ordinal);
        await _mailRepo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R3_23_LeftoversCheck_QueuesTheReadOnlyAgentCheck_EvenWithoutTheMailGrant()
    {
        _server.MailSetupAvailable = false;
        _server.AgentCapabilitiesJson = $"[\"{AgentCapabilities.MailLeftoversCheck}\",\"shell.execute\"]";

        await _operations.CheckLeftoversAsync(ServerId, Ct);

        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == ServerId
            && t.Operation == OperationKind.MailLeftoversCheck
            && t.Command == "-"
            && t.Name == MailTaskNames.LeftoversCheck), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R3_23_LeftoversCheck_IsRefusedByNameOnAnAgentTooOld()
    {
        _server.AgentCapabilitiesJson = "[\"shell.execute\"]";

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _operations.CheckLeftoversAsync(ServerId, Ct));

        Assert.Contains(AgentCapabilities.MailLeftoversCheck, error.Message, StringComparison.Ordinal);
        await _mailRepo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2_090_TheStatusList_CarriesSpfDkimDmarc_WithTheRecordsToPublish_AndLeavesTheFlagsAlone()
    {
        var domain = new MailDomain
        {
            Id = 3,
            ServerId = ServerId,
            Name = "example.com",
            DkimSelector = "mail",
            DkimPublicKey = "v=DKIM1; k=rsa; p=ABC",
            HasSpf = true,
            HasDkim = true,
            HasDmarc = true
        };
        _inventory.GetStateAsync(ServerId, Arg.Any<CancellationToken>()).Returns(new MailState { ServerId = ServerId, Hostname = Host });
        _mailRepo.GetDomainsAsync(ServerId, Arg.Any<CancellationToken>()).Returns([domain]);
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns([Host]);
        _dns.TxtAsync("example.com", Arg.Any<CancellationToken>()).Returns([]);
        _dns.TxtAsync("mail._domainkey.example.com", Arg.Any<CancellationToken>()).Returns(["v=DKIM1; k=rsa; p=ABC"]);
        _dns.TxtAsync("_dmarc.example.com", Arg.Any<CancellationToken>()).Returns([]);

        var result = await _diagnostics.GetAsync(ServerId, Ct);

        var mx = result.Items.Single(i => i.Key == "mx:example.com");
        Assert.Equal((MailCheckVerdict.Ok, "MX", $"10 {Host}."), (mx.Verdict, mx.RecordType, mx.RecordValue));
        var spf = result.Items.Single(i => i.Key == "spf:example.com");
        Assert.Equal((MailCheckVerdict.Missing, "example.com", "TXT", "v=spf1 mx a ~all"),
            (spf.Verdict, spf.RecordName, spf.RecordType, spf.RecordValue));
        var dkim = result.Items.Single(i => i.Key == "dkim:example.com");
        Assert.Equal((MailCheckVerdict.Ok, "mail._domainkey.example.com", "v=DKIM1; k=rsa; p=ABC"),
            (dkim.Verdict, dkim.RecordName, dkim.RecordValue));
        var dmarc = result.Items.Single(i => i.Key == "dmarc:example.com");
        Assert.Equal((MailCheckVerdict.Missing, "_dmarc.example.com"), (dmarc.Verdict, dmarc.RecordName));
        Assert.StartsWith("v=DMARC1;", dmarc.RecordValue, StringComparison.Ordinal);
        // Reading the status is not a verification: the stored badges stay as they were.
        Assert.True(domain.HasSpf && domain.HasDmarc);
        Assert.Null(domain.DnsCheckedAt);
    }

    [Fact]
    public async Task R1_10_R1_14_TheStatusList_ChecksSpfAgainstTheServerAddress_AndCarriesTheMxTimeToLive()
    {
        var domain = new MailDomain { Id = 3, ServerId = ServerId, Name = "example.com", DkimSelector = "mail", IsActive = true };
        _inventory.GetStateAsync(ServerId, Arg.Any<CancellationToken>()).Returns(new MailState { ServerId = ServerId, Hostname = Host });
        _mailRepo.GetDomainsAsync(ServerId, Arg.Any<CancellationToken>()).Returns([domain]);
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mx1.ovh.net"]);
        _dns.MxTimeToLiveAsync("example.com", Arg.Any<CancellationToken>()).Returns(86000);
        _dns.TxtAsync("example.com", Arg.Any<CancellationToken>()).Returns(["v=spf1 include:mx.ovh.com ~all"]);
        _dns.TxtAsync("mx.ovh.com", Arg.Any<CancellationToken>()).Returns(["v=spf1 ip4:178.32.228.0/24 ~all"]);
        _dns.TxtAsync("mail._domainkey.example.com", Arg.Any<CancellationToken>()).Returns([]);
        _dns.TxtAsync("_dmarc.example.com", Arg.Any<CancellationToken>()).Returns([]);

        var result = await _diagnostics.GetAsync(ServerId, Ct);

        var spf = result.Items.Single(i => i.Key == "spf:example.com");
        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized", "v=spf1 mx a include:mx.ovh.com ~all"),
            (spf.Verdict, spf.Detail, spf.RecordValue));
        var mx = result.Items.Single(i => i.Key == "mx:example.com");
        Assert.Equal((MailCheckVerdict.Mismatch, 86000), (mx.Verdict, mx.TimeToLiveSeconds));
    }
}
