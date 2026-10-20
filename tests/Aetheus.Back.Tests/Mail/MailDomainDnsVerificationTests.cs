// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Verifying a domain's DNS uses the mail hostname recorded for the server and the server's
/// public address, then saves what the verifier changed on the tracked domain.</summary>
public sealed class MailDomainDnsVerificationTests
{
    private readonly IMailInventoryRepository _inventory = Substitute.For<IMailInventoryRepository>();
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly IMailDnsVerifier _verifier = Substitute.For<IMailDnsVerifier>();
    private readonly MailDiagnosticsService _sut;

    public MailDomainDnsVerificationTests()
    {
        _sut = new MailDiagnosticsService(
            _inventory,
            Substitute.For<IMailRepository>(),
            _servers,
            Substitute.For<IMailDnsResolver>(),
            _verifier,
            TimeProvider.System);
    }

    [Fact]
    public async Task UnknownDomain_IsNotFound_AndNothingIsVerifiedOrSaved()
    {
        _inventory.GetTrackedDomainAsync(1, 9, Arg.Any<CancellationToken>()).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.VerifyDomainDnsAsync(1, 9, TestContext.Current.CancellationToken));

        await _inventory.DidNotReceiveWithAnyArgs().SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_verifier.ReceivedCalls());
    }

    [Fact]
    public async Task KnownDomain_IsVerifiedAgainstTheRecordedHostnameAndServerAddress_ThenSaved()
    {
        var domain = new MailDomain { Id = 5, ServerId = 1, Name = "example.com" };
        var expected = new MailDnsCheckDto { DomainId = 5, Domain = "example.com" };
        _inventory.GetTrackedDomainAsync(1, 5, Arg.Any<CancellationToken>()).Returns(domain);
        _inventory.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailState { Hostname = "mail.example.com" });
        _servers.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server { Id = 1, IpAddress = "203.0.113.9" });
        _verifier.VerifyAsync(domain, "mail.example.com", "203.0.113.9", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.VerifyDomainDnsAsync(1, 5, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        await _inventory.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DomainOfAServerWithoutStateOrRecord_IsStillVerifiedWithoutHostnameOrAddress()
    {
        var domain = new MailDomain { Id = 5, ServerId = 1, Name = "example.com" };
        var expected = new MailDnsCheckDto { DomainId = 5, Domain = "example.com" };
        _inventory.GetTrackedDomainAsync(1, 5, Arg.Any<CancellationToken>()).Returns(domain);
        _inventory.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns((MailState?)null);
        _servers.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns((Server?)null);
        _verifier.VerifyAsync(domain, null, null, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.VerifyDomainDnsAsync(1, 5, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        await _inventory.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
