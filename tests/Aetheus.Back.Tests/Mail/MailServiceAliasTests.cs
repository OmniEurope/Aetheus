// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class MailServiceAliasTests
{
    private readonly IMailRepository _repo = Substitute.For<IMailRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IServerRepository _serverRepo = Substitute.For<IServerRepository>();
    private readonly MailService _sut;

    public MailServiceAliasTests()
    {
        // S-FEAT-W8KN: incremental mail ops now gate on EnsureMailManageableAsync, which looks up the
        // server and requires MailSetupAvailable. Mock a mail-manageable server #1 so the valid-path
        // tests pass the gate; the negative tests short-circuit (validation/NotFound) before it.
        _serverRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, MailSetupAvailable = true });
        _sut = new MailService(_repo, _audit, _encryption, _serverRepo, Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Substitute.For<IMailMasterSecrets>());
    }

    private static MailDomain Domain(int id = 1, int serverId = 1) =>
        new() { Id = id, ServerId = serverId, Name = "example.com" };

    [Fact]
    public async Task GetAliasesAsync_MapsRepositoryAliases()
    {
        _repo.GetAliasesAsync(1, Arg.Any<CancellationToken>()).Returns([
            new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "b@example.com", MailDomain = Domain() }
        ]);

        var result = await _sut.GetAliasesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("a@example.com", result[0].SourceEmail);
    }

    [Fact]
    public async Task CreateAliasAsync_DomainNotFound_Throws()
    {
        _repo.GetDomainAsync(1, 5, Arg.Any<CancellationToken>()).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CreateAliasAsync(1, 5, new CreateMailAliasRequest { SourceEmail = "a@example.com", DestinationEmail = "b@example.com" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAliasAsync_InvalidSourceEmail_ThrowsBadRequest()
    {
        _repo.GetDomainAsync(1, 1, Arg.Any<CancellationToken>()).Returns(Domain());

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateAliasAsync(1, 1, new CreateMailAliasRequest { SourceEmail = "not-an-email", DestinationEmail = "b@example.com" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAliasAsync_InvalidDestinationEmail_ThrowsBadRequest()
    {
        _repo.GetDomainAsync(1, 1, Arg.Any<CancellationToken>()).Returns(Domain());

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateAliasAsync(1, 1, new CreateMailAliasRequest { SourceEmail = "a@example.com", DestinationEmail = "bad" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAliasAsync_Valid_PersistsAndReturnsDto()
    {
        _repo.GetDomainAsync(1, 1, Arg.Any<CancellationToken>()).Returns(Domain());

        var result = await _sut.CreateAliasAsync(1, 1,
            new CreateMailAliasRequest { SourceEmail = "a@example.com", DestinationEmail = "b@example.com" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("a@example.com", result.SourceEmail);
        await _repo.Received(1).AddAliasAsync(Arg.Any<MailAlias>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAliasAsync_NotFound_Throws()
    {
        _repo.GetAliasAsync(9, Arg.Any<CancellationToken>()).Returns((MailAlias?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteAliasAsync(1, 9, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAliasAsync_WrongServer_Throws()
    {
        _repo.GetAliasAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "b@example.com", MailDomain = Domain(serverId: 2) });

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteAliasAsync(1, 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAliasAsync_Valid_DeletesAlias()
    {
        _repo.GetAliasAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "b@example.com", MailDomain = Domain(serverId: 1) });

        await _sut.DeleteAliasAsync(1, 1, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteAliasAsync(Arg.Any<MailAlias>(), Arg.Any<CancellationToken>());
    }

    // --- RotateDkimKeyAsync ---

    [Fact]
    public async Task RotateDkim_DomainNotFound_Throws()
    {
        _repo.GetDomainAsync(1, 5, Arg.Any<CancellationToken>()).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.RotateDkimKeyAsync(1, 5, new DkimRotationRequest { NewSelector = "sel2026" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RotateDkim_InvalidSelector_ThrowsBadRequest()
    {
        _repo.GetDomainAsync(1, 1, Arg.Any<CancellationToken>()).Returns(Domain());

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.RotateDkimKeyAsync(1, 1, new DkimRotationRequest { NewSelector = "bad selector!" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RotateDkim_Valid_ReturnsRotationResultWithDnsRecord()
    {
        var domain = Domain();
        domain.DkimSelector = "old";
        _repo.GetDomainAsync(1, 1, Arg.Any<CancellationToken>()).Returns(domain);

        var result = await _sut.RotateDkimKeyAsync(1, 1, new DkimRotationRequest { NewSelector = "sel2026" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("old", result.OldSelector);
        Assert.Equal("sel2026", result.NewSelector);
        Assert.Contains("sel2026._domainkey.example.com", result.DnsRecordName);
    }

    // --- UpdateAliasAsync ---

    [Fact]
    public async Task UpdateAlias_NotFound_Throws()
    {
        _repo.GetAliasAsync(9, Arg.Any<CancellationToken>()).Returns((MailAlias?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAliasAsync(1, 9, new UpdateMailAliasRequest { IsActive = false }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAlias_WrongServer_Throws()
    {
        _repo.GetAliasAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "b@example.com", MailDomain = Domain(serverId: 2) });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAliasAsync(1, 1, new UpdateMailAliasRequest { IsActive = false }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAlias_InvalidDestination_ThrowsBadRequest()
    {
        _repo.GetAliasAsync(1, Arg.Any<CancellationToken>()).Returns(
            new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "b@example.com", MailDomain = Domain(serverId: 1) });

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateAliasAsync(1, 1, new UpdateMailAliasRequest { DestinationEmail = "not-email" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAlias_Valid_UpdatesAndReturnsDto()
    {
        var alias = new MailAlias { Id = 1, MailDomainId = 1, SourceEmail = "a@example.com", DestinationEmail = "old@example.com", MailDomain = Domain(serverId: 1) };
        _repo.GetAliasAsync(1, Arg.Any<CancellationToken>()).Returns(alias);

        var result = await _sut.UpdateAliasAsync(1, 1, new UpdateMailAliasRequest { IsActive = false, DestinationEmail = "new@example.com" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new@example.com", alias.DestinationEmail);
        Assert.False(alias.IsActive);
    }
}
