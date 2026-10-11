// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class MailServiceTests
{
    private readonly IMailRepository _repo = Substitute.For<IMailRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IServerRepository _serverRepo = Substitute.For<IServerRepository>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly MailService _sut;

    public MailServiceTests()
    {
        // Identity "encryption" so EnvironmentVariables stays inspectable in assertions; with a real
        // IEncryptionService the setup env is AES-encrypted at rest (F11b).
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        // Default: a mail-setup-capable server so SetupAsync passes the capability gate (F6).
        _serverRepo.FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = true });
        _sut = new MailService(_repo, _audit, _encryption, _serverRepo, _taskService);
    }

    // --- GetStateAsync ---

    [Fact]
    public async Task GetStateAsync_NoState_ReturnsEmptyDto()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((MailState?)null);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
        Assert.Empty(result.Domains);
        Assert.Empty(result.Accounts);
    }

    [Fact]
    public async Task GetStateAsync_WithState_ReturnsPopulatedDto()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new MailState
        {
            ServerId = 1,
            IsPostfixRunning = true,
            IsDovecotRunning = true,
            PostfixVersion = "3.5.6",
            DovecotVersion = "2.3.19",
            QueueSize = 5
        });
        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsPostfixRunning);
        Assert.True(result.IsDovecotRunning);
        Assert.Equal("3.5.6", result.PostfixVersion);
        Assert.Equal(5, result.QueueSize);
        Assert.Empty(result.Domains);
        Assert.Empty(result.Accounts);
        await _repo.DidNotReceive().GetDomainsAsync(1, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetAccountsAsync(1, Arg.Any<CancellationToken>());
    }

    // --- GetDomainsAsync ---

    [Fact]
    public async Task GetDomainsAsync_MapsDomainEntities()
    {
        _repo.GetDomainsAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new MailDomain { Id = 1, ServerId = 1, Name = "test.com", IsActive = true, DkimSelector = "mail", HasSpf = true }
        ]);

        var result = await _sut.GetDomainsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("test.com", result[0].Name);
        Assert.True(result[0].HasSpf);
        Assert.Equal("mail", result[0].DkimSelector);
    }

    [Fact]
    public async Task GetDomainsAsync_EmptyServer_ReturnsEmpty()
    {
        _repo.GetDomainsAsync(99, TestContext.Current.CancellationToken).Returns([]);

        var result = await _sut.GetDomainsAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    // --- GetDomainAsync ---

    [Fact]
    public async Task GetDomainAsync_NotFound_Throws()
    {
        _repo.GetDomainAsync(1, 999, TestContext.Current.CancellationToken).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetDomainAsync(1, 999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDomainAsync_Found_ReturnsDto()
    {
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "test.com", DkimSelector = "default" });

        var result = await _sut.GetDomainAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("test.com", result.Name);
    }

    // --- CreateDomainAsync ---

    [Fact]
    public async Task CreateDomainAsync_InvalidDomain_Throws()
    {
        var request = new CreateMailDomainRequest { Name = "not valid!" };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateDomainAsync(1, request, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddDomainAsync(Arg.Any<MailDomain>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateDomainAsync_ValidDomain_CreatesDomainAndTask()
    {
        var request = new CreateMailDomainRequest { Name = "example.com", DkimSelector = "mail" };

        var result = await _sut.CreateDomainAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("example.com", result.Name);
        Assert.Equal("mail", result.DkimSelector);
        await _repo.Received(1).AddDomainAsync(Arg.Is<MailDomain>(d =>
            d.Name == "example.com" && d.ServerId == 1), TestContext.Current.CancellationToken);
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Command.Contains("example.com") && t.Status == TaskExecutionStatus.Pending), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailAddDomain", "Mail", 1, "example.com", TestContext.Current.CancellationToken);
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    // --- UpdateDomainAsync ---

    [Fact]
    public async Task UpdateDomainAsync_NotFound_Throws()
    {
        _repo.GetDomainAsync(1, 999, TestContext.Current.CancellationToken).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateDomainAsync(1, 999, new UpdateMailDomainRequest { IsActive = false }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateDomainAsync_InvalidDkim_Throws()
    {
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "test.com" });

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateDomainAsync(1, 1, new UpdateMailDomainRequest { DkimSelector = "bad selector!" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateDomainAsync_ValidUpdate_UpdatesAndAudits()
    {
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "test.com", IsActive = true, DkimSelector = "default" });

        var result = await _sut.UpdateDomainAsync(1, 1, new UpdateMailDomainRequest { IsActive = false, DkimSelector = "newsel" }, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsActive);
        Assert.Equal("newsel", result.DkimSelector);
        await _repo.Received(1).UpdateDomainAsync(Arg.Any<MailDomain>(), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailUpdateDomain", "Mail", 1, "test.com", TestContext.Current.CancellationToken);
    }

    // --- DeleteDomainAsync ---

    [Fact]
    public async Task DeleteDomainAsync_NotFound_Throws()
    {
        _repo.GetDomainAsync(1, 999, TestContext.Current.CancellationToken).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteDomainAsync(1, 999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteDomainAsync_Found_DeletesAndCreatesRemoveTask()
    {
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "test.com" });

        await _sut.DeleteDomainAsync(1, 1, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteDomainAsync(Arg.Is<MailDomain>(d => d.Name == "test.com"), TestContext.Current.CancellationToken);
        // Typed op via mail-manage (not the old dead sed/postconf shell): kind MailRemoveDomain, domain in Command.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1
            && t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.MailRemoveDomain
            && t.Command == "test.com"), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailRemoveDomain", "Mail", 1, "test.com", TestContext.Current.CancellationToken);
    }

    // --- GetAccountsAsync ---

    [Fact]
    public async Task GetAccountsAsync_MapsAccountEntities()
    {
        _repo.GetAccountsAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new MailAccount { Id = 1, Email = "user@test.com", QuotaMb = 512, IsActive = true, MailDomain = new MailDomain { Name = "test.com" } }
        ]);

        var result = await _sut.GetAccountsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("user@test.com", result[0].Email);
    }

    // --- CreateAccountAsync ---

    [Fact]
    public async Task CreateAccountAsync_InvalidEmail_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateAccountAsync(1, new CreateMailAccountRequest { Email = "bad", Password = "strong-password" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAccountAsync_DomainNotConfigured_Throws()
    {
        _repo.GetDomainsAsync(1, TestContext.Current.CancellationToken).Returns([]);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateAccountAsync(1, new CreateMailAccountRequest { Email = "user@unknown.com", Password = "strong-password" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAccountAsync_Valid_CreatesAccountAndTask()
    {
        _repo.GetDomainsAsync(1, TestContext.Current.CancellationToken).Returns(
        [
            new MailDomain { Id = 10, ServerId = 1, Name = "example.com" }
        ]);

        var result = await _sut.CreateAccountAsync(1, new CreateMailAccountRequest
        {
            Email = "user@example.com",
            Password = "secure-password",
            QuotaMb = 2048
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("user@example.com", result.Email);
        Assert.Equal(2048, result.QuotaMb);
        await _repo.Received(1).AddAccountAsync(Arg.Is<MailAccount>(a =>
            a.MailDomainId == 10 && a.Email == "user@example.com"), TestContext.Current.CancellationToken);
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Command.Contains("user@example.com") && t.Command.Contains("example.com")), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailAddAccount", "Mail", 1, "user@example.com", TestContext.Current.CancellationToken);
    }

    // --- UpdateAccountAsync ---

    [Fact]
    public async Task UpdateAccountAsync_NotFound_Throws()
    {
        _repo.GetAccountAsync(999, TestContext.Current.CancellationToken).Returns((MailAccount?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAccountAsync(1, 999, new UpdateMailAccountRequest { IsActive = false }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAccountAsync_WrongServer_Throws()
    {
        _repo.GetAccountAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAccount { Id = 1, Email = "user@test.com", MailDomain = new MailDomain { ServerId = 99, Name = "test.com" } });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAccountAsync(1, 1, new UpdateMailAccountRequest { IsActive = false }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAccountAsync_WithPassword_CreatesPasswordChangeTask()
    {
        _repo.GetAccountAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAccount { Id = 1, Email = "user@test.com", QuotaMb = 1024, MailDomain = new MailDomain { ServerId = 1, Name = "test.com" } });

        await _sut.UpdateAccountAsync(1, 1, new UpdateMailAccountRequest { NewPassword = "new-password-1" }, ct: TestContext.Current.CancellationToken);

        // S-TECH-MCPW: typed MailChangePassword op - the email is the target; the password rides in the
        // encrypted env (never in the command/argv) and is piped to the helper over stdin.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailChangePassword
            && t.Command.Contains("user@test.com")
            && !t.Command.Contains("new-password-1")), TestContext.Current.CancellationToken);
        await _repo.Received(1).UpdateAccountAsync(Arg.Any<MailAccount>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UpdateAccountAsync_QuotaAndActive_UpdatesFields()
    {
        _repo.GetAccountAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAccount { Id = 1, Email = "user@test.com", QuotaMb = 1024, IsActive = true, MailDomain = new MailDomain { ServerId = 1, Name = "test.com" } });

        var result = await _sut.UpdateAccountAsync(1, 1, new UpdateMailAccountRequest { QuotaMb = 2048, IsActive = false }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2048, result.QuotaMb);
        Assert.False(result.IsActive);
    }

    // --- DeleteAccountAsync ---

    [Fact]
    public async Task DeleteAccountAsync_NotFound_Throws()
    {
        _repo.GetAccountAsync(999, TestContext.Current.CancellationToken).Returns((MailAccount?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteAccountAsync(1, 999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAccountAsync_WrongServer_Throws()
    {
        _repo.GetAccountAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAccount { Id = 1, Email = "user@test.com", MailDomain = new MailDomain { ServerId = 99, Name = "test.com" } });

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteAccountAsync(1, 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAccountAsync_Valid_DeletesAndCreatesTask()
    {
        _repo.GetAccountAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAccount { Id = 1, Email = "user@test.com", MailDomain = new MailDomain { ServerId = 1, Name = "test.com" } });

        await _sut.DeleteAccountAsync(1, 1, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteAccountAsync(Arg.Any<MailAccount>(), TestContext.Current.CancellationToken);
        // Typed op via mail-manage: kind MailDeleteAccount, email in Command, parent domain in env.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.MailDeleteAccount
            && t.Command == "user@test.com"
            && t.EnvironmentVariables.Contains("test.com")), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailDeleteAccount", "Mail", 1, "user@test.com", TestContext.Current.CancellationToken);
    }

    // --- ExecuteActionAsync ---

    [Theory]
    [InlineData(MailAction.StartPostfix, OperationKind.MailStartPostfix)]
    [InlineData(MailAction.StopDovecot, OperationKind.MailStopDovecot)]
    [InlineData(MailAction.FlushQueue, OperationKind.MailFlushQueue)]
    [InlineData(MailAction.TestConfig, OperationKind.MailTestConfig)]
    public async Task ExecuteActionAsync_CreatesCorrectTask(MailAction action, OperationKind expectedOperation)
    {
        await _sut.ExecuteActionAsync(1, new MailActionRequest { Action = action }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Operation == expectedOperation && t.Status == TaskExecutionStatus.Pending), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync($"Mail{action}", "Mail", 1, action.ToString(), TestContext.Current.CancellationToken);
    }

    // --- GetLogsAsync ---

    [Fact]
    public async Task GetLogsAsync_CreatesOperationTask()
    {
        await _sut.GetLogsAsync(1, new MailLogRequest { LogType = "postfix", Lines = 200, Filter = "4F2A1B3C9D" }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailGetLogs && t.Command == "postfix" && t.ServerId == 1
            && t.EnvironmentVariables.Contains("\"200\"") && t.EnvironmentVariables.Contains("4F2A1B3C9D")), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetLogsAsync_DovecotLogs_UsesCorrectTarget()
    {
        await _sut.GetLogsAsync(1, new MailLogRequest { LogType = "dovecot", Lines = 50 }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailGetLogs && t.Command == "dovecot"), TestContext.Current.CancellationToken);
    }

    // --- SetupAsync ---

    [Fact]
    public async Task SetupAsync_InvalidHostname_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SetupAsync(1, new MailSetupRequest
            {
                Hostname = "bad!",
                Domain = "example.com",
                AdminEmail = "admin@example.com",
                AdminPassword = "strong-password"
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupAsync_InvalidDomain_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SetupAsync(1, new MailSetupRequest
            {
                Hostname = "mail.example.com",
                Domain = "bad!",
                AdminEmail = "admin@example.com",
                AdminPassword = "strong-password"
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupAsync_InvalidAdminEmail_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SetupAsync(1, new MailSetupRequest
            {
                Hostname = "mail.example.com",
                Domain = "example.com",
                AdminEmail = "not-an-email",
                AdminPassword = "strong-password"
            }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupAsync_Valid_CreatesTaskDomainAndAccount()
    {
        var request = new MailSetupRequest
        {
            Hostname = "mail.example.com",
            Domain = "example.com",
            DkimSelector = "default",
            AdminEmail = "admin@example.com",
            AdminPassword = "secure-password",
            QuotaMb = 2048
        };

        await _sut.SetupAsync(1, request, ct: TestContext.Current.CancellationToken);

        // S-FEAT-W8KN: setup now dispatches the typed MailSetup operation (domain in Command/target,
        // the rest in env vars) instead of a free-form shell pipeline the non-root agent couldn't run.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == ExecutorType.Operation
            && t.Operation == OperationKind.MailSetup
            && t.Command == "example.com"
            && t.TimeoutSeconds == 900
            && t.EnvironmentVariables.Contains(MailSetupEnv.SpamFilter)
            && t.EnvironmentVariables.Contains("mail.example.com")
            && t.EnvironmentVariables.Contains(MailSetupEnv.AdminPassword)), TestContext.Current.CancellationToken);
        await _repo.Received(1).AddDomainAsync(Arg.Is<MailDomain>(d =>
            d.Name == "example.com" && d.DkimSelector == "default"), TestContext.Current.CancellationToken);
        await _repo.Received(1).AddAccountAsync(Arg.Is<MailAccount>(a =>
            a.Email == "admin@example.com" && a.QuotaMb == 2048), TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync("MailSetup", "Mail", 1, "example.com", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SetupAsync_CapabilityMissing_Throws_AndQueuesNoTask()
    {
        _serverRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = false });

        var request = new MailSetupRequest
        {
            Hostname = "mail.example.com",
            Domain = "example.com",
            DkimSelector = "default",
            AdminEmail = "admin@example.com",
            AdminPassword = "secure-password",
            QuotaMb = 2048
        };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SetupAsync(1, request, ct: TestContext.Current.CancellationToken));
        // The gate must short-circuit before any side effect (no task, no domain/account records).
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddDomainAsync(Arg.Any<MailDomain>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupAsync_WhileASetupOrReinstallRuns_IsRefused_AndQueuesNoTask()
    {
        // Recette R2-085: tasks 23257 and 23258, two full setups of one domain 3 s apart, the second
        // started while the first ran.
        _repo.HasOpenTaskAsync(1, Arg.Is<IReadOnlyCollection<OperationKind>>(k =>
                k.Contains(OperationKind.MailSetup) && k.Contains(OperationKind.MailPurge)), Arg.Any<CancellationToken>())
            .Returns(true);
        var request = new MailSetupRequest
        {
            Hostname = "mail.example.com",
            Domain = "example.com",
            DkimSelector = "default",
            AdminEmail = "admin@example.com",
            AdminPassword = "secure-password",
            QuotaMb = 2048
        };

        await Assert.ThrowsAsync<ConflictException>(() => _sut.SetupAsync(1, request, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddDomainAsync(Arg.Any<MailDomain>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2101_SetupAsync_OnADomainAlreadyRecorded_UpdatesTheRows_ThenQueuesTheTask()
    {
        // Recette R2-101: a setup re-run on a domain already recorded inserted it again and died on the
        // unique (ServerId, Name) index with HTTP 500, after its task had been queued.
        var domain = new MailDomain { Id = 7, ServerId = 1, Name = "example.com", DkimSelector = "old", IsActive = false };
        var account = new MailAccount { Id = 9, MailDomainId = 7, MailDomain = domain, Email = "admin@example.com", QuotaMb = 512, IsActive = false };
        _repo.FindDomainByNameAsync(1, "example.com", Arg.Any<CancellationToken>()).Returns(domain);
        _repo.FindAccountByEmailAsync("admin@example.com", Arg.Any<CancellationToken>()).Returns(account);

        await _sut.SetupAsync(1, SetupRequest(), ct: TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddDomainAsync(Arg.Any<MailDomain>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddAccountAsync(Arg.Any<MailAccount>(), Arg.Any<CancellationToken>());
        Assert.Equal("default", domain.DkimSelector);
        Assert.True(domain.IsActive);
        Assert.Equal(2048, account.QuotaMb);
        Assert.True(account.IsActive);
        Received.InOrder(() =>
        {
            _repo.UpdateDomainAsync(domain, Arg.Any<CancellationToken>());
            _repo.UpdateAccountAsync(account, Arg.Any<CancellationToken>());
            _repo.AddTaskAsync(Arg.Is<ServerTask>(t => t.Operation == OperationKind.MailSetup), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task R2101_SetupAsync_WhenTheRecordsCannotBeSaved_QueuesNoTask()
    {
        _repo.AddDomainAsync(Arg.Any<MailDomain>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("save failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.SetupAsync(1, SetupRequest(), ct: TestContext.Current.CancellationToken));

        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2101_SetupAsync_AnAddressRecordedOnAnotherServer_IsAConflict_AndQueuesNothing()
    {
        _repo.FindAccountByEmailAsync("admin@example.com", Arg.Any<CancellationToken>()).Returns(new MailAccount
        {
            Email = "admin@example.com",
            MailDomain = new MailDomain { ServerId = 2, Name = "example.com" }
        });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.SetupAsync(1, SetupRequest(), ct: TestContext.Current.CancellationToken));

        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddDomainAsync(Arg.Any<MailDomain>(), Arg.Any<CancellationToken>());
    }

    private static MailSetupRequest SetupRequest() => new()
    {
        Hostname = "mail.example.com",
        Domain = "example.com",
        DkimSelector = "default",
        AdminEmail = "admin@example.com",
        AdminPassword = "secure-password",
        QuotaMb = 2048
    };

    // --- GetDnsRecordsAsync ---

    [Fact]
    public async Task GetDnsRecordsAsync_NotFound_Throws()
    {
        _repo.GetDomainAsync(1, 999, TestContext.Current.CancellationToken).Returns((MailDomain?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetDnsRecordsAsync(1, 999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDnsRecordsAsync_Found_ReturnsRecordsAndCreatesDkimTask()
    {
        _serverRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = true, Status = ServerStatus.Online });
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "example.com", DkimSelector = "mail" });

        var result = await _sut.GetDnsRecordsAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("example.com", result.Domain);
        Assert.Contains("mail", result.DkimSelector);
        Assert.Contains("spf1", result.SpfRecord);
        Assert.Contains("DMARC1", result.DmarcRecord);
        Assert.Contains("example.com", result.MxRecord);
        // Key still unknown: typed mail-manage dkim-read, domain in Command, selector in env.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.MailDkimRead
            && t.Command == "example.com"
            && t.EnvironmentVariables.Contains("\"mail\"")), TestContext.Current.CancellationToken);
        Assert.Equal(MailDkimKeyStatus.Reading, result.DkimKeyStatus);
    }

    [Fact]
    public async Task GetDnsRecordsAsync_UnknownKey_AgentOffline_ReturnsTheComputedRecords_WithDkimUnavailable_AndQueuesNothing()
    {
        // SITE-OFFLINE-001: the queue refuses a task for an offline agent (409); reading the records must not fail.
        _serverRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = true, Status = ServerStatus.Offline });
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "example.com", DkimSelector = "mail" });
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new MailState { ServerId = 1, Hostname = "mx.example.com" });

        var result = await _sut.GetDnsRecordsAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(MailDkimKeyStatus.AgentOffline, result.DkimKeyStatus);
        Assert.Empty(result.DkimPublicKey);
        Assert.Equal("10 mx.example.com.", result.MxRecord);
        Assert.Equal(MailDnsRecordFactory.SpfRecord, result.SpfRecord);
        Assert.Equal("mail._domainkey.example.com", result.DkimRecord);
        Assert.Contains("DMARC1", result.DmarcRecord);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().EnsureServerOnlineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDnsRecordsAsync_KnownKey_AgentOffline_ReturnsTheFullDkimRecord()
    {
        _serverRepo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", Status = ServerStatus.Offline });
        _repo.GetDomainAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new MailDomain { Id = 1, ServerId = 1, Name = "example.com", DkimSelector = "mail", DkimPublicKey = "v=DKIM1; k=rsa; p=MIIB" });

        var result = await _sut.GetDnsRecordsAsync(1, 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(MailDkimKeyStatus.Available, result.DkimKeyStatus);
        Assert.Equal("v=DKIM1; k=rsa; p=MIIB", result.DkimPublicKey);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- DeleteAliasAsync ---

    [Fact]
    public async Task DeleteAliasAsync_WrongServer_Throws()
    {
        _repo.GetAliasAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAlias { Id = 1, SourceEmail = "alias@test.com", MailDomain = new MailDomain { ServerId = 2, Name = "test.com" } });

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeleteAliasAsync(1, 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAliasAsync_Valid_DeletesAndCreatesRemoveTask()
    {
        _repo.GetAliasAsync(1, TestContext.Current.CancellationToken).Returns(
            new MailAlias { Id = 1, SourceEmail = "alias@test.com", MailDomain = new MailDomain { ServerId = 1, Name = "test.com" } });

        await _sut.DeleteAliasAsync(1, 1, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteAliasAsync(Arg.Any<MailAlias>(), TestContext.Current.CancellationToken);
        // Typed op via mail-manage: kind MailRemoveAlias, alias source email in Command.
        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.MailRemoveAlias
            && t.Command == "alias@test.com"), TestContext.Current.CancellationToken);
    }
}
