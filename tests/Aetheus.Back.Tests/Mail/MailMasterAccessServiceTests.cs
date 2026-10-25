// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R1-41 / R3-50: "Préparer l'accès administrateur" queues mail-manage master-set with a generated
/// password that rides encrypted, stores the same password once queued, and audits without it. Recette R3-49:
/// "Purger" deletes only the mailbox rows the server no longer reports, and audits them.
/// </summary>
public sealed class MailMasterAccessServiceTests : IDisposable
{
    private const int ServerId = 6;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IMailMasterSecrets _secrets = Substitute.For<IMailMasterSecrets>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MailMasterAccessService _sut;

    public MailMasterAccessServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options, _clock);
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = true });
        // A visible marker instead of AES: the test proves the env went through encryption.
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => "ENC(" + ci.Arg<string>() + ")");
        _sut = new MailMasterAccessService(new MailRepository(_db), new MailInventoryRepository(_db), _servers, _tasks,
            _encryption, _secrets, _audit);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedStateAsync(int helperVersion)
    {
        _db.MailStates.Add(new MailState { ServerId = ServerId, Hostname = "mx.example.test", HelperVersion = helperVersion });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Prepare_queues_master_set_with_an_encrypted_password_and_stores_the_same_one()
    {
        await SeedStateAsync(MailMasterAccess.MinimumHelperVersion);
        string? stored = null;
        await _secrets.StoreAsync(ServerId, Arg.Do<string>(value => stored = value), Arg.Any<CancellationToken>());

        var queued = await _sut.PrepareAsync(ServerId, Ct);

        var task = await _db.Tasks.SingleAsync(Ct);
        Assert.Equal(queued.TaskId, task.Id);
        Assert.Equal((OperationKind.MailMasterAccess, "-"), (task.Operation, task.Command));
        Assert.NotNull(stored);
        Assert.StartsWith("ENC(", task.EnvironmentVariables, StringComparison.Ordinal);
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables![4..^1])!;
        Assert.Equal(stored, env[MailSetupEnv.MasterPassword]);
        Assert.True(MailValidation.IsValidPassword(stored));
        await _audit.Received(1).LogAsync("MailMasterAccessPrepared", "Mail", ServerId, MailMasterAccess.MasterUser, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_audit.ReceivedCalls(), call => call.GetArguments().OfType<string>().Any(a => a.Contains(stored!)));
    }

    [Fact]
    public async Task Each_preparation_generates_a_new_password()
    {
        await SeedStateAsync(MailMasterAccess.MinimumHelperVersion);
        var stored = new List<string>();
        await _secrets.StoreAsync(ServerId, Arg.Do<string>(stored.Add), Arg.Any<CancellationToken>());

        await _sut.PrepareAsync(ServerId, Ct);
        await _sut.PrepareAsync(ServerId, Ct);

        Assert.Equal(2, stored.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task A_helper_without_master_set_is_refused_and_nothing_is_queued_or_stored(int helperVersion)
    {
        await SeedStateAsync(helperVersion);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PrepareAsync(ServerId, Ct));

        Assert.False(await _db.Tasks.AnyAsync(Ct));
        Assert.Empty(_secrets.ReceivedCalls());
    }

    [Fact]
    public async Task A_server_without_the_mail_capability_is_refused()
    {
        await SeedStateAsync(MailMasterAccess.MinimumHelperVersion);
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = false });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PrepareAsync(ServerId, Ct));
        Assert.Empty(_secrets.ReceivedCalls());
    }

    [Fact]
    public async Task A_running_setup_is_left_to_create_the_access_itself()
    {
        await SeedStateAsync(MailMasterAccess.MinimumHelperVersion);
        _db.Tasks.Add(new ServerTask
        {
            ServerId = ServerId,
            Name = "setup",
            Command = "example.test",
            Operation = OperationKind.MailSetup,
            Executor = ExecutorType.Operation,
            Status = TaskExecutionStatus.Running
        });
        await _db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.PrepareAsync(ServerId, Ct));
        Assert.Empty(_secrets.ReceivedCalls());
    }

    [Fact]
    public async Task Purge_deletes_only_the_rows_the_server_no_longer_reports_and_audits_them()
    {
        var domain = new MailDomain { ServerId = ServerId, Name = "example.test" };
        domain.Accounts.Add(new MailAccount { Email = "gone@example.test", IsActive = false, MissingSince = _clock.GetUtcNow().UtcDateTime });
        domain.Accounts.Add(new MailAccount { Email = "kept-disabled@example.test", IsActive = false });
        domain.Accounts.Add(new MailAccount { Email = "live@example.test" });
        _db.MailDomains.Add(domain);
        var other = new MailDomain { ServerId = 99, Name = "other.test" };
        other.Accounts.Add(new MailAccount { Email = "gone@other.test", IsActive = false, MissingSince = _clock.GetUtcNow().UtcDateTime });
        _db.MailDomains.Add(other);
        await _db.SaveChangesAsync(Ct);
        var purge = new MailAccountPurgeService(new MailRepository(_db), _audit, _cache);

        var result = await purge.PurgeMissingAsync(ServerId, Ct);

        Assert.Equal(1, result.Removed);
        var left = await _db.MailAccounts.Select(account => account.Email).OrderBy(email => email).ToListAsync(Ct);
        Assert.Equal(["gone@other.test", "kept-disabled@example.test", "live@example.test"], left);
        await _audit.Received(1).LogAsync("MailAccountsPurged", "Mail", ServerId, "1: gone@example.test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_with_nothing_missing_deletes_and_audits_nothing()
    {
        var domain = new MailDomain { ServerId = ServerId, Name = "example.test" };
        domain.Accounts.Add(new MailAccount { Email = "live@example.test" });
        _db.MailDomains.Add(domain);
        await _db.SaveChangesAsync(Ct);
        var purge = new MailAccountPurgeService(new MailRepository(_db), _audit, _cache);

        var result = await purge.PurgeMissingAsync(ServerId, Ct);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, await _db.MailAccounts.CountAsync(Ct));
        Assert.Empty(_audit.ReceivedCalls());
    }
}
