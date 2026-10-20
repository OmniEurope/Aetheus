// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.Mail;
using NSubstitute;

namespace Aetheus.Back.Tests.Mail;

/// <summary>SPF evaluation over a resolver double: the redirect modifier, and the ten-lookup budget that
/// stops the evaluation before a later mechanism could pass.</summary>
public class MailSpfAuthorizationTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("203.0.113.5");
    private readonly IMailDnsResolver _dns = Substitute.For<IMailDnsResolver>();

    [Fact]
    public async Task ARedirect_IsFollowedWhenNoMechanismMatched()
    {
        _dns.TxtAsync("_spf.example.com", Arg.Any<CancellationToken>()).Returns(["v=spf1 ip4:203.0.113.0/24 -all"]);

        Assert.True(await new MailSpfAuthorization(_dns).AuthorizesAsync(
            "example.com", "v=spf1 ip4:198.51.100.1 redirect=_spf.example.com", Sender, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARedirectWrittenWithAMacro_IsNotFollowed()
    {
        Assert.False(await new MailSpfAuthorization(_dns).AuthorizesAsync(
            "example.com", "v=spf1 redirect=%{d}.spf.example.com", Sender, TestContext.Current.CancellationToken));
        await _dns.DidNotReceiveWithAnyArgs().TxtAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnceTheTenLookupsAreSpent_TheEvaluationStopsBeforeALaterPass()
    {
        _dns.AddressesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(["198.51.100.9"]);
        var lookups = string.Join(' ', Enumerable.Range(1, MailSpfAuthorization.MaxLookups + 1).Select(i => $"a:host{i}.example.com"));

        Assert.False(await new MailSpfAuthorization(_dns).AuthorizesAsync(
            "example.com", $"v=spf1 {lookups} +all", Sender, TestContext.Current.CancellationToken));
        Assert.Equal(MailSpfAuthorization.MaxLookups, _dns.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IMailDnsResolver.AddressesAsync)));
    }

    [Fact]
    public async Task ModifiersOtherThanRedirect_AreIgnored()
    {
        Assert.True(await new MailSpfAuthorization(_dns).AuthorizesAsync(
            "example.com", "v=spf1 exp=explain.example.com ip4:203.0.113.5 -all", Sender, TestContext.Current.CancellationToken));
    }
}
