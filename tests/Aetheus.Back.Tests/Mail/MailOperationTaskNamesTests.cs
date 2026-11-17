// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-12: the mail page finds again, when the user comes back, the tasks of the spam training, the certificate,
/// the delivery test and the mailbox usage by the names in <see cref="ServerTaskNames"/>. The backend names those tasks
/// from the same constants, so a renamed task cannot silently hide an action in progress.
/// </summary>
public sealed class MailOperationTaskNamesTests : IDisposable
{
    private const int ServerId = 4;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly IMailRepository _mailRepo = Substitute.For<IMailRepository>();
    private readonly List<string> _names = [];
    private readonly MailOperationsService _sut;

    public MailOperationTaskNamesTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, _clock);
        var servers = Substitute.For<IServerRepository>();
        servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = true });
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _mailRepo.AddTaskAsync(Arg.Do<ServerTask>(task => _names.Add(task.Name)), Arg.Any<CancellationToken>());
        _mailRepo.GetDomainsAsync(ServerId, Arg.Any<CancellationToken>()).Returns([new MailDomain { Name = "example.com" }]);
        _db.MailStates.Add(new MailState { ServerId = ServerId, Hostname = "mail.example.com" });
        _db.SaveChanges();
        _sut = new MailOperationsService(_mailRepo, new MailInventoryRepository(_db), servers, Substitute.For<ITaskService>(),
            Substitute.For<ILogService>(), Substitute.For<IAuditService>(), encryption, _clock);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TheLearningCertificateDeliveryAndUsageTasks_AreNamedFromTheSharedNames()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sut.LearnSpamAsync(ServerId, new LearnSpamRequest { IsSpam = true, RawMessage = "Subject: x\n\nspam" }, ct);
        await _sut.LearnSpamAsync(ServerId, new LearnSpamRequest { IsSpam = false, RawMessage = "Subject: x\n\nham" }, ct);
        await _sut.InstallCertificateAsync(ServerId, null, ct);
        await _sut.SendTestAsync(ServerId, new MailTestDeliveryRequest { From = "admin@example.com", To = "bob@example.org" }, ct);
        await _sut.RefreshQuotaAsync(ServerId, ct);

        Assert.Equal(
        [
            ServerTaskNames.MailLearn(isSpam: true),
            ServerTaskNames.MailLearn(isSpam: false),
            ServerTaskNames.MailTlsCertificate("mail.example.com"),
            ServerTaskNames.MailDeliveryTestPrefix + "bob@example.org",
            ServerTaskNames.MailMailboxUsage
        ], _names);
    }

    [Fact]
    public async Task TheSpamTraining_IsAdoptedByTheMailPage()
    {
        await _sut.LearnSpamAsync(ServerId, new LearnSpamRequest { IsSpam = true, RawMessage = "Subject: x\n\nspam" },
            TestContext.Current.CancellationToken);

        var name = Assert.Single(_names);
        Assert.Contains(ServerTaskNames.MailAdoptedPrefixes, prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
