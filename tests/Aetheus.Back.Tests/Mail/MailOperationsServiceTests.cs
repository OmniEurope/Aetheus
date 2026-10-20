// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Verify typed TLS, spam, delivery and queue dispatch plus guarded mailbox-usage
/// ingestion.</summary>
public sealed class MailOperationsServiceTests : IDisposable
{
    private const int ServerId = 4;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly IMailRepository _mailRepo = Substitute.For<IMailRepository>();
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly ILogService _logs = Substitute.For<ILogService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly MailOperationsService _sut;

    public MailOperationsServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options, _clock);
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = true });
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _sut = new MailOperationsService(_mailRepo, new MailInventoryRepository(_db), _servers, _tasks, _logs,
            Substitute.For<IAuditService>(), _encryption, _clock);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedStateAsync(string hostname = "mail.example.com")
    {
        _db.MailStates.Add(new MailState { ServerId = ServerId, Hostname = hostname, TlsCertPath = "/etc/ssl/certs/ssl-cert-snakeoil.pem" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InstallCertificate_TargetsTheReportedHostnameWithTheAcmeEmail()
    {
        await SeedStateAsync();

        await _sut.InstallCertificateAsync(ServerId, "ops@example.com", TestContext.Current.CancellationToken);

        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailInstallCertificate
            && t.Command == "mail.example.com"
            && t.EnvironmentVariables.Contains(MailSetupEnv.CertificateEmail)
            && t.EnvironmentVariables.Contains("ops@example.com")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallCertificate_RefusesWhenTheHostnameIsUnknown()
    {
        await SeedStateAsync(hostname: string.Empty);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.InstallCertificateAsync(ServerId, null, TestContext.Current.CancellationToken));
        await _mailRepo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSpamFilter_RefusesUnorderedThresholds()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateSpamFilterAsync(ServerId,
            new UpdateSpamFilterRequest { RejectScore = 5, AddHeaderScore = 8, GreylistScore = 2 }, TestContext.Current.CancellationToken));

        await _sut.UpdateSpamFilterAsync(ServerId,
            new UpdateSpamFilterRequest { RejectScore = 20, AddHeaderScore = 8.5, GreylistScore = 5 }, TestContext.Current.CancellationToken);
        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailSpamConfigure && t.EnvironmentVariables.Contains("\"8.5\"")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LearnSpam_EncryptsTheSampleAtRest()
    {
        await _sut.LearnSpamAsync(ServerId, new LearnSpamRequest { IsSpam = false, RawMessage = "Subject: hi\n\nham" },
            TestContext.Current.CancellationToken);

        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailSpamLearn && t.Command == "ham" && t.EnvironmentVariables.StartsWith("enc:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendTest_RequiresASenderFromAServerDomain()
    {
        _mailRepo.GetDomainsAsync(ServerId, Arg.Any<CancellationToken>()).Returns([new MailDomain { Name = "example.com" }]);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SendTestAsync(ServerId,
            new MailTestDeliveryRequest { From = "ceo@elsewhere.org", To = "bob@example.org" }, TestContext.Current.CancellationToken));

        await _sut.SendTestAsync(ServerId, new MailTestDeliveryRequest { From = "admin@example.com", To = "bob@example.org" },
            TestContext.Current.CancellationToken);
        await _mailRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.MailSendTest && t.Command == "bob@example.org"
            && t.EnvironmentVariables.Contains("admin@example.com")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operations_RefuseWithoutTheMailCapability()
    {
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = false });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.RefreshQueueAsync(ServerId, TestContext.Current.CancellationToken));
        await _mailRepo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestQuota_StoresUsageFromASuccessfulReportOfTheSameServer()
    {
        var domain = new MailDomain { ServerId = ServerId, Name = "second.test" };
        domain.Accounts.Add(new MailAccount { Email = "bob@second.test", QuotaMb = 1024 });
        _db.MailDomains.Add(domain);
        var task = new ServerTask { ServerId = ServerId, Name = "usage", Operation = OperationKind.MailQuotaReport, Status = TaskExecutionStatus.Success };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _tasks.GetTaskAsync(task.Id, Arg.Any<CancellationToken>())
            .Returns(new ServerTaskDto { Id = task.Id, ServerId = ServerId, Status = TaskExecutionStatus.Success });
        _logs.GetTaskLogsAsync(task.Id, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([new TaskLogDto { Message = "QUOTA\tbob@second.test\t2049" }]);

        var updated = await _sut.IngestQuotaReportAsync(ServerId, task.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        var bob = await _db.MailAccounts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, bob.UsedMb); // 2049 KiB rounded up to MiB
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, bob.UsageMeasuredAt);
    }

    [Fact]
    public async Task IngestQuota_RefusesATaskOfAnotherKindOrServer()
    {
        var task = new ServerTask { ServerId = ServerId, Name = "logs", Operation = OperationKind.MailGetLogs, Status = TaskExecutionStatus.Success };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _tasks.GetTaskAsync(task.Id, Arg.Any<CancellationToken>())
            .Returns(new ServerTaskDto { Id = task.Id, ServerId = ServerId, Status = TaskExecutionStatus.Success });
        _tasks.GetTaskAsync(999, Arg.Any<CancellationToken>())
            .Returns(new ServerTaskDto { Id = 999, ServerId = ServerId + 1, Status = TaskExecutionStatus.Success });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.IngestQuotaReportAsync(ServerId, task.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.IngestQuotaReportAsync(ServerId, 999, TestContext.Current.CancellationToken));
    }
}
