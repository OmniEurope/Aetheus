// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>"Réinstaller la messagerie" (recette R2-083 / R2-088): the purge is queued only with the typed
/// domain, a helper that knows the command and no setup or purge already running; it drops the rows that
/// mirror the erased stack and is audited.</summary>
public sealed class MailStackPurgeServiceTests : IDisposable
{
    private const int ServerId = 6;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly IServerRepository _servers = Substitute.For<IServerRepository>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MailStackPurgeService _sut;

    public MailStackPurgeServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options, _clock);
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = true });
        _sut = new MailStackPurgeService(new MailRepository(_db), new MailInventoryRepository(_db), _servers, _tasks, _audit, _cache);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedStackAsync(int helperVersion = 3)
    {
        _db.MailStates.Add(new MailState { ServerId = ServerId, Hostname = "mx.sonytumen.com", HelperVersion = helperVersion });
        var domain = new MailDomain { ServerId = ServerId, Name = "sonytumen.com" };
        domain.Accounts.Add(new MailAccount { Email = "admin@sonytumen.com", QuotaMb = 1024 });
        domain.Aliases.Add(new MailAlias { SourceEmail = "info@sonytumen.com", DestinationEmail = "admin@sonytumen.com" });
        _db.MailDomains.Add(domain);
        _db.MailDomains.Add(new MailDomain { ServerId = 99, Name = "other.test" });
        await _db.SaveChangesAsync(Ct);
    }

    private Task<bool> AnyPurgeTaskAsync() => _db.Tasks.AnyAsync(t => t.Operation == OperationKind.MailPurge, Ct);

    [Fact]
    public async Task TheTypedDomain_QueuesOnePurge_DropsTheServersMailRows_AndAudits()
    {
        await SeedStackAsync();

        var queued = await _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = " SonyTumen.com " }, Ct);

        var task = await _db.Tasks.SingleAsync(Ct);
        Assert.Equal(queued.TaskId, task.Id);
        Assert.Equal((OperationKind.MailPurge, "-", ServerId), (task.Operation, task.Command, task.ServerId));
        Assert.Equal(MailStackPurgeService.TimeoutSeconds, task.TimeoutSeconds);
        Assert.False(await _db.MailDomains.AnyAsync(d => d.ServerId == ServerId, Ct));
        Assert.False(await _db.MailAccounts.AnyAsync(Ct));
        Assert.False(await _db.MailAliases.AnyAsync(Ct));
        // Another server's mail is not this purge's business.
        Assert.True(await _db.MailDomains.AnyAsync(d => d.ServerId == 99, Ct));
        await _audit.Received(1).LogAsync("MailPurge", "Mail", ServerId, "SonyTumen.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheMailHostName_IsAcceptedToo()
    {
        await SeedStackAsync();

        await _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "mx.sonytumen.com" }, Ct);

        Assert.True(await AnyPurgeTaskAsync());
    }

    [Fact]
    public async Task AnyOtherWord_IsRefused_AndErasesNothing()
    {
        await SeedStackAsync();

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "other.test" }, Ct));

        Assert.False(await AnyPurgeTaskAsync());
        Assert.True(await _db.MailDomains.AnyAsync(d => d.ServerId == ServerId, Ct));
        await _audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHelperWithoutPurge_IsRefused()
    {
        // Version 2 answers "unknown sub-command: purge" after the rows are gone: refuse before anything.
        await SeedStackAsync(helperVersion: 2);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "sonytumen.com" }, Ct));

        Assert.False(await AnyPurgeTaskAsync());
    }

    [Fact]
    public async Task WithoutTheMailCapability_IsRefused()
    {
        await SeedStackAsync();
        _servers.FindServerAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = ServerId, Name = "mx", Hostname = "mx", MailSetupAvailable = false });

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "sonytumen.com" }, Ct));

        Assert.False(await AnyPurgeTaskAsync());
    }

    [Theory]
    [InlineData(OperationKind.MailSetup)]
    [InlineData(OperationKind.MailPurge)]
    public async Task WhileASetupOrPurgeRuns_ASecondIsRefused(OperationKind running)
    {
        // Recette R2-085: two full setups 3 s apart. A second click must not start a second rebuild.
        await SeedStackAsync();
        _db.Tasks.Add(new ServerTask { ServerId = ServerId, Name = "running", Operation = running, Command = "-", Status = TaskExecutionStatus.Running });
        await _db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "sonytumen.com" }, Ct));

        Assert.Equal(1, await _db.Tasks.CountAsync(Ct));
        Assert.True(await _db.MailDomains.AnyAsync(d => d.ServerId == ServerId, Ct));
    }

    [Fact]
    public async Task AFinishedSetup_DoesNotBlockThePurge()
    {
        await SeedStackAsync();
        _db.Tasks.Add(new ServerTask { ServerId = ServerId, Name = "setup", Operation = OperationKind.MailSetup, Command = "sonytumen.com", Status = TaskExecutionStatus.Failed });
        await _db.SaveChangesAsync(Ct);

        await _sut.PurgeAsync(ServerId, new MailPurgeRequest { Confirmation = "sonytumen.com" }, Ct);

        Assert.True(await AnyPurgeTaskAsync());
    }
}
