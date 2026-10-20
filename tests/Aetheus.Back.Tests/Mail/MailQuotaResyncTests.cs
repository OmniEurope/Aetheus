// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Mail;

/// <summary>DEP-005: when a server's helper reaches v5, each existing account's stored quota is pushed once.
/// Runs on the real repositories (in-memory provider) so the "once" mark is the one production persists.</summary>
public sealed class MailQuotaResyncTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MailRepository _mail;
    private readonly ServerHeartbeatRepository _heartbeat;
    private readonly ITaskService _taskService = Substitute.For<ITaskService>();
    private readonly MailQuotaResync _sut;

    public MailQuotaResyncTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _mail = new MailRepository(_db);
        _heartbeat = new ServerHeartbeatRepository(_db);
        _sut = new MailQuotaResync(_mail, _taskService, TimeProvider.System);
        var domain = new MailDomain { ServerId = 1, Name = "example.com" };
        domain.Accounts.Add(new MailAccount { Email = "a@example.com", QuotaMb = 512, IsActive = true });
        domain.Accounts.Add(new MailAccount { Email = "b@example.com", QuotaMb = 0, IsActive = true });
        _db.MailDomains.Add(domain);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task Beat(int helperVersion) =>
        await _heartbeat.ReplaceMailDataAsync(1, new MailState { ServerId = 1, HelperVersion = helperVersion },
            TestContext.Current.CancellationToken);

    private async Task<List<ServerTask>> QuotaTasks() =>
        await _db.Tasks.Where(t => t.Operation == OperationKind.MailSetQuota).ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task UpgradedServer_GetsOneQuotaTaskPerAccount_Once()
    {
        var ct = TestContext.Current.CancellationToken;
        await Beat(5);
        await _db.SaveChangesAsync(ct);

        Assert.Equal(2, await _sut.ResyncAsync(1, 5, ct));

        var tasks = await QuotaTasks();
        Assert.Equal(["a@example.com", "b@example.com"], tasks.Select(t => t.Command).Order().ToArray());
        Assert.Contains(tasks, t => t.Command == "a@example.com" && t.EnvironmentVariables!.Contains("\"512\""));
        Assert.Contains(tasks, t => t.Command == "b@example.com" && t.EnvironmentVariables!.Contains("\"0\""));
    }

    [Fact]
    public async Task SecondHeartbeat_QueuesNothing_EvenAfterTheStateRowIsRewritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await Beat(5);
        await _db.SaveChangesAsync(ct);
        await _sut.ResyncAsync(1, 5, ct);

        await Beat(5); // the inventory changed: the row is replaced, the mark must survive
        await _db.SaveChangesAsync(ct);
        Assert.Equal(0, await _sut.ResyncAsync(1, 5, ct));

        Assert.Equal(2, (await QuotaTasks()).Count);
    }

    [Fact]
    public async Task ServerStillOnV4_GetsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Beat(4);
        await _db.SaveChangesAsync(ct);

        Assert.Equal(0, await _sut.ResyncAsync(1, 4, ct));
        // Even a stale report of v5 cannot claim while the stored state is still v4.
        Assert.Equal(0, await _sut.ResyncAsync(1, 5, ct));

        Assert.Empty(await QuotaTasks());
    }

    [Fact]
    public async Task DowngradeThenUpgrade_ResyncsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await Beat(5);
        await _db.SaveChangesAsync(ct);
        await _sut.ResyncAsync(1, 5, ct);
        await Beat(4);
        await _db.SaveChangesAsync(ct);
        await Beat(5);
        await _db.SaveChangesAsync(ct);

        Assert.Equal(2, await _sut.ResyncAsync(1, 5, ct));
        Assert.Equal(4, (await QuotaTasks()).Count);
    }

    [Fact]
    public async Task QueueFailure_ReleasesTheClaimSoTheNextHeartbeatRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        await Beat(5);
        await _db.SaveChangesAsync(ct);
        _taskService.EnsureServerOnlineAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("offline")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ResyncAsync(1, 5, ct));
        _taskService.EnsureServerOnlineAsync(1, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Assert.Equal(2, await _sut.ResyncAsync(1, 5, ct));
    }
}
