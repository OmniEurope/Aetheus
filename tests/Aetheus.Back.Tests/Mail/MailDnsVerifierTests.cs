// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>Verdicts per record and the persisted SPF / DKIM / DMARC flags.</summary>
public sealed class MailDnsVerifierTests
{
    private readonly IMailDnsResolver _dns = Substitute.For<IMailDnsResolver>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly MailDnsVerifier _sut;

    private const string ServerIp = "203.0.113.16";

    public MailDnsVerifierTests()
    {
        _sut = new MailDnsVerifier(_dns, _clock);
        // The mail host of the domain resolves to this server.
        _dns.AddressesAsync("mail.example.com", Arg.Any<CancellationToken>()).Returns([ServerIp]);
    }

    private static MailDomain Domain() => new()
    {
        Id = 3,
        Name = "example.com",
        DkimSelector = "s1",
        DkimPublicKey = "v=DKIM1; h=sha256; k=rsa; p=MIIBIjAN"
    };

    private void Publish(string name, params string[] txt) =>
        _dns.TxtAsync(name, Arg.Any<CancellationToken>()).Returns(txt);

    [Fact]
    public async Task FullyPublishedDomain_IsOkEverywhere()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);
        Publish("example.com", "google-site-verification=abc", "v=spf1 mx a ~all");
        Publish("s1._domainkey.example.com", "v=DKIM1; k=rsa; p=MIIB IjAN");
        Publish("_dmarc.example.com", "v=DMARC1; p=quarantine");
        var domain = Domain();

        var result = await _sut.VerifyAsync(domain, "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        Assert.All(result.Records, r => Assert.Equal(MailCheckVerdict.Ok, r.Verdict));
        Assert.True(domain.HasSpf && domain.HasDkim && domain.HasDmarc);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, domain.DnsCheckedAt);
    }

    [Fact]
    public async Task MissingDmarc_OnlyDmarcIsMissing()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);
        Publish("example.com", "v=spf1 mx ~all");
        Publish("s1._domainkey.example.com", "v=DKIM1; k=rsa; p=MIIBIjAN");
        Publish("_dmarc.example.com");
        var domain = Domain();

        var result = await _sut.VerifyAsync(domain, "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        Assert.Equal(MailCheckVerdict.Missing, result.Records.Single(r => r.Kind == MailDnsRecordKind.Dmarc).Verdict);
        Assert.True(domain.HasSpf);
        Assert.True(domain.HasDkim);
        Assert.False(domain.HasDmarc);
    }

    [Fact]
    public async Task RotatedKeyNotYetPublished_IsAKeyMismatch()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);
        Publish("example.com", "v=spf1 mx ~all");
        Publish("s1._domainkey.example.com", "v=DKIM1; k=rsa; p=OLDKEY");
        Publish("_dmarc.example.com", "v=DMARC1; p=none");
        var domain = Domain();

        var result = await _sut.VerifyAsync(domain, "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        var dkim = result.Records.Single(r => r.Kind == MailDnsRecordKind.Dkim);
        Assert.Equal((MailCheckVerdict.Mismatch, "key-mismatch"), (dkim.Verdict, dkim.Detail));
        Assert.False(domain.HasDkim);
    }

    private async Task<(MailCheckVerdict Verdict, string Detail)> SpfAsync(string? serverIp, params string[] txt)
    {
        var (verdict, detail, _) = await _sut.EvaluateSpfAsync("example.com", txt, serverIp, TestContext.Current.CancellationToken);
        return (verdict, detail);
    }

    [Theory]
    [InlineData(new[] { "v=spf1 +all" }, MailCheckVerdict.Mismatch, "permissive-all")]
    [InlineData(new[] { "v=spf1 mx all" }, MailCheckVerdict.Mismatch, "permissive-all")]
    [InlineData(new[] { "v=spf1 mx ~all", "v=spf1 a ~all" }, MailCheckVerdict.Mismatch, "multiple-records")]
    [InlineData(new[] { "v=spf1 -all" }, MailCheckVerdict.Mismatch, "no-sender-mechanism")]
    [InlineData(new[] { "unrelated" }, MailCheckVerdict.Missing, "")]
    [InlineData(new[] { "v=spf1 mx a ~all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 a mx include:mx.ovh.com ~all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 ip4:203.0.113.16 -all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 ip4:203.0.113.1/24 -all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 ip4:198.51.100.0/24 -all" }, MailCheckVerdict.Mismatch, "server-not-authorized")]
    [InlineData(new[] { "v=spf1 -ip4:203.0.113.16 ip4:203.0.113.0/24 ~all" }, MailCheckVerdict.Mismatch, "server-not-authorized")]
    [InlineData(new[] { "v=spf1 ~ip4:203.0.113.16 ~all" }, MailCheckVerdict.Mismatch, "no-sender-mechanism")]
    [InlineData(new[] { "v=spf1 a:mail.example.com -all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 a:mail.example.com/24 -all" }, MailCheckVerdict.Ok, "")]
    [InlineData(new[] { "v=spf1 mx -all" }, MailCheckVerdict.Ok, "")]
    public async Task R1_10_SpfIsCompliant_OnlyWhenItAuthorisesThisServer(string[] txt, MailCheckVerdict verdict, string detail)
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);

        Assert.Equal((verdict, detail), await SpfAsync(ServerIp, txt));
    }

    [Fact]
    public async Task R1_10_TheRealCase_AnOvhIncludeAlone_DoesNotAuthoriseThisServer()
    {
        // OVH-style record listing OVH's own senders (test data): 203.0.113.16 is not among them.
        Publish("mx.ovh.com", "v=spf1 ip4:178.32.228.0/24 ip4:46.105.44.0/24 include:_spf-b.ovh.com ~all");
        Publish("_spf-b.ovh.com", "v=spf1 ip4:178.33.0.0/16 ~all");

        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), await SpfAsync(ServerIp, "v=spf1 include:mx.ovh.com ~all"));
    }

    [Fact]
    public async Task R1_10_AnIncludeThatListsThisServer_Authorises()
    {
        Publish("_spf.example.net", "v=spf1 ip4:203.0.113.0/24 -all");

        Assert.Equal((MailCheckVerdict.Ok, ""), await SpfAsync(ServerIp, "v=spf1 include:_spf.example.net ~all"));
    }

    [Fact]
    public async Task R1_10_AnIncludeWithoutSpfRecord_DoesNotAuthorise()
    {
        Publish("_spf.example.net");

        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), await SpfAsync(ServerIp, "v=spf1 include:_spf.example.net ~all"));
    }

    [Fact]
    public async Task R1_10_Ipv6Mechanism_CoversAnIpv6Server()
    {
        Assert.Equal((MailCheckVerdict.Ok, ""), await SpfAsync("2001:db8::25", "v=spf1 ip6:2001:db8::/32 -all"));
        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), await SpfAsync("2001:db9::25", "v=spf1 ip6:2001:db8::/32 -all"));
    }

    [Fact]
    public async Task R1_10_IncludeLoop_Terminates_WithoutAuthorising()
    {
        Publish("_a.example.net", "v=spf1 include:_b.example.net ~all");
        Publish("_b.example.net", "v=spf1 include:_a.example.net ~all");

        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), await SpfAsync(ServerIp, "v=spf1 include:_a.example.net ~all"));
    }

    [Fact]
    public async Task R1_10_IncludeChain_IsBoundedByTheTenLookupLimit()
    {
        for (var i = 0; i < 14; i++)
            Publish($"_s{i}.example.net", $"v=spf1 include:_s{i + 1}.example.net ~all");
        // The record that would authorise the server sits beyond the tenth lookup.
        Publish("_s14.example.net", "v=spf1 ip4:203.0.113.16 -all");

        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), await SpfAsync(ServerIp, "v=spf1 include:_s0.example.net ~all"));
        Assert.True(_dns.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IMailDnsResolver.TxtAsync)) <= MailSpfAuthorization.MaxLookups);
    }

    [Fact]
    public async Task R1_10_UnknownServerAddress_IsUnknown_NotCompliant()
    {
        Assert.Equal((MailCheckVerdict.Unknown, "no-address"), await SpfAsync("", "v=spf1 include:mx.ovh.com ~all"));
        // The record Aetheus proposes needs no address to be recognised.
        Assert.Equal((MailCheckVerdict.Ok, ""), await SpfAsync("", "v=spf1 mx a ~all"));
    }

    [Fact]
    public async Task R1_10_NotAuthorised_ProposesTheRecord_KeepingTheTermsAlreadyPublished()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);
        Publish("example.com", "v=spf1 include:mx.ovh.com ~all");
        Publish("mx.ovh.com", "v=spf1 ip4:178.32.228.0/24 ~all");
        Publish("s1._domainkey.example.com");
        Publish("_dmarc.example.com");
        var domain = Domain();

        var result = await _sut.VerifyAsync(domain, "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        var spf = result.Records.Single(r => r.Kind == MailDnsRecordKind.Spf);
        Assert.Equal((MailCheckVerdict.Mismatch, "server-not-authorized"), (spf.Verdict, spf.Detail));
        Assert.False(domain.HasSpf);
        Assert.Equal("v=spf1 mx a include:mx.ovh.com ~all", MailDnsRecordFactory.SpfToPublish(spf));
        Assert.Equal("v=spf1 mx a ~all", MailDnsRecordFactory.SpfToPublish(spf with { Detail = string.Empty, Observed = [] }));
    }

    [Fact]
    public async Task R1_14_AnMxStillShowingTheOldHost_CarriesTheRemainingTimeToLiveTheResolverReported()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mx1.ovh.net", "mx2.ovh.net"]);
        _dns.MxTimeToLiveAsync("example.com", Arg.Any<CancellationToken>()).Returns(81234);
        Publish("example.com");
        Publish("s1._domainkey.example.com");
        Publish("_dmarc.example.com");

        var result = await _sut.CheckAsync(Domain(), "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        var mx = result.Single(r => r.Kind == MailDnsRecordKind.Mx);
        Assert.Equal((MailCheckVerdict.Mismatch, 81234), (mx.Verdict, mx.TimeToLiveSeconds));
    }

    [Fact]
    public async Task R1_14_ACompliantMx_AsksNoTimeToLive_AndAnUnreportedOneStaysNull()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mail.example.com"]);
        Publish("example.com");
        Publish("s1._domainkey.example.com");
        Publish("_dmarc.example.com");

        var ok = await _sut.CheckAsync(Domain(), "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        Assert.Null(ok.Single(r => r.Kind == MailDnsRecordKind.Mx).TimeToLiveSeconds);
        await _dns.DidNotReceive().MxTimeToLiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mx1.ovh.net"]);
        _dns.MxTimeToLiveAsync("example.com", Arg.Any<CancellationToken>()).Returns((int?)null);
        var stale = await _sut.CheckAsync(Domain(), "mail.example.com", ServerIp, TestContext.Current.CancellationToken);
        Assert.Null(stale.Single(r => r.Kind == MailDnsRecordKind.Mx).TimeToLiveSeconds);
    }

    [Fact]
    public async Task ResolverFailure_IsUnknownNotMissing()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).ThrowsAsync(new MailDnsLookupException("SERVFAIL"));
        _dns.TxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new MailDnsLookupException("SERVFAIL"));
        var domain = Domain();
        domain.HasSpf = true;

        var result = await _sut.VerifyAsync(domain, "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        Assert.All(result.Records, r => Assert.Equal((MailCheckVerdict.Unknown, "resolver-error"), (r.Verdict, r.Detail)));
        Assert.False(domain.HasSpf);
    }

    [Fact]
    public async Task MxPointingElsewhere_IsAMismatch()
    {
        _dns.MxAsync("example.com", Arg.Any<CancellationToken>()).Returns(["mx.provider.net"]);
        Publish("example.com");
        Publish("s1._domainkey.example.com");
        Publish("_dmarc.example.com");

        var result = await _sut.VerifyAsync(Domain(), "mail.example.com", ServerIp, TestContext.Current.CancellationToken);

        var mx = result.Records.Single(r => r.Kind == MailDnsRecordKind.Mx);
        Assert.Equal((MailCheckVerdict.Mismatch, "wrong-target"), (mx.Verdict, mx.Detail));
        Assert.Equal(["mx.provider.net"], mx.Observed);
    }
}
