// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class CachedMailServiceTests
{
    private readonly IMailService _inner = Substitute.For<IMailService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CachedMailService _sut;

    public CachedMailServiceTests()
    {
        _sut = new CachedMailService(_inner, _cache);
    }

    [Fact]
    public async Task GetStateAsync_CachesResult()
    {
        var state = new MailDataDto();
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(state);

        var r1 = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        var r2 = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Same(r1, r2);
        await _inner.Received(1).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDomainsAsync_CachesResult()
    {
        var domains = new List<MailDomainDto> { new() { Id = 1, Name = "test.com" } };
        _inner.GetDomainsAsync(1, Arg.Any<CancellationToken>()).Returns(domains);

        var r1 = await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);
        var r2 = await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(r1);
        await _inner.Received(1).GetDomainsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccountsAsync_CachesResult()
    {
        var accounts = new List<MailAccountDto> { new() { Id = 1, Email = "a@b.c" } };
        _inner.GetAccountsAsync(1, Arg.Any<CancellationToken>()).Returns(accounts);

        var r1 = await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);
        var r2 = await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(r1);
        await _inner.Received(1).GetAccountsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDomainAsync_NotCached_PassesThrough()
    {
        var domain = new MailDomainDto { Id = 1, Name = "test.com" };
        _inner.GetDomainAsync(1, 10, Arg.Any<CancellationToken>()).Returns(domain);

        await _sut.GetDomainAsync(1, 10, ct: TestContext.Current.CancellationToken);
        await _sut.GetDomainAsync(1, 10, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetDomainAsync(1, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDnsRecordsAsync_NotCached_PassesThrough()
    {
        var dns = new MailDnsRecordsDto();
        _inner.GetDnsRecordsAsync(1, 10, Arg.Any<CancellationToken>()).Returns(dns);

        await _sut.GetDnsRecordsAsync(1, 10, ct: TestContext.Current.CancellationToken);
        await _sut.GetDnsRecordsAsync(1, 10, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetDnsRecordsAsync(1, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAliasesAsync_NotCached_PassesThrough()
    {
        _inner.GetAliasesAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        await _sut.GetAliasesAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.GetAliasesAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetAliasesAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDomainAsync_InvalidatesCache()
    {
        var state = new MailDataDto();
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(state);
        _inner.CreateDomainAsync(1, Arg.Any<CreateMailDomainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailDomainDto { Id = 1, Name = "new.com" });

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.CreateDomainAsync(1, new CreateMailDomainRequest { Name = "new.com" }, ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateDomainAsync_InvalidatesCache()
    {
        _inner.GetDomainsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _inner.UpdateDomainAsync(1, 10, Arg.Any<UpdateMailDomainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailDomainDto { Id = 10 });

        await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.UpdateDomainAsync(1, 10, new UpdateMailDomainRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetDomainsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteDomainAsync_InvalidatesCache()
    {
        _inner.GetDomainsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _inner.DeleteDomainAsync(1, 10, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.DeleteDomainAsync(1, 10, ct: TestContext.Current.CancellationToken);
        await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetDomainsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAccountAsync_InvalidatesCache()
    {
        _inner.GetAccountsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _inner.CreateAccountAsync(1, Arg.Any<CreateMailAccountRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAccountDto { Id = 1, Email = "a@b.c" });

        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.CreateAccountAsync(1, new CreateMailAccountRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetAccountsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAccountAsync_InvalidatesCache()
    {
        _inner.GetAccountsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _inner.UpdateAccountAsync(1, 10, Arg.Any<UpdateMailAccountRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAccountDto { Id = 10 });

        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.UpdateAccountAsync(1, 10, new UpdateMailAccountRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetAccountsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAccountAsync_InvalidatesCache()
    {
        _inner.GetAccountsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _inner.DeleteAccountAsync(1, 10, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.DeleteAccountAsync(1, 10, ct: TestContext.Current.CancellationToken);
        await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetAccountsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.ExecuteActionAsync(1, Arg.Any<MailActionRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto());

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.ExecuteActionAsync(1, new MailActionRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.SetupAsync(1, Arg.Any<MailSetupRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto { TaskId = 5 });

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.SetupAsync(1, new MailSetupRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAliasAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.CreateAliasAsync(1, 10, Arg.Any<CreateMailAliasRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAliasDto());

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.CreateAliasAsync(1, 10, new CreateMailAliasRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAliasAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.UpdateAliasAsync(1, 5, Arg.Any<UpdateMailAliasRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MailAliasDto());

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.UpdateAliasAsync(1, 5, new UpdateMailAliasRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAliasAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.DeleteAliasAsync(1, 5, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.DeleteAliasAsync(1, 5, ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RotateDkimKeyAsync_InvalidatesCache()
    {
        _inner.GetStateAsync(1, Arg.Any<CancellationToken>()).Returns(new MailDataDto());
        _inner.RotateDkimKeyAsync(1, 10, Arg.Any<DkimRotationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DkimRotationResultDto());

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.RotateDkimKeyAsync(1, 10, new DkimRotationRequest(), ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        await _inner.Received(2).GetStateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLogsAsync_NotCached_PassesThrough()
    {
        _inner.GetLogsAsync(1, Arg.Any<MailLogRequest>(), Arg.Any<CancellationToken>()).Returns(new MailTaskQueuedDto());

        await _sut.GetLogsAsync(1, new MailLogRequest(), ct: TestContext.Current.CancellationToken);

        await _inner.Received(1).GetLogsAsync(1, Arg.Any<MailLogRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentServerIds_CachedIndependently()
    {
        _inner.GetStateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MailDataDto());

        await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);
        await _sut.GetStateAsync(2, ct: TestContext.Current.CancellationToken);

        await _inner.Received(1).GetStateAsync(1, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetStateAsync(2, Arg.Any<CancellationToken>());
    }
}
