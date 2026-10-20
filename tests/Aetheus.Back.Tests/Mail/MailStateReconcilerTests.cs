// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>The heartbeat reconciliation adopts an existing configuration additively, deactivates
/// instead of deleting, never trusts a partial inventory and never resurrects a pending
/// removal.</summary>
public sealed class MailStateReconcilerTests : IDisposable
{
    private const int ServerId = 7;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly MailStateReconciler _sut;

    public MailStateReconcilerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options, _clock);
        _sut = new MailStateReconciler(new MailInventoryRepository(_db), _clock, NullLogger<MailStateReconciler>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static MailDataDto Inventory(
        string[] domains, string[] accounts, (string Source, string Destination)[] aliases,
        bool accountsCollected = true, bool aliasesCollected = true) => new()
        {
            IsInstalled = true,
            DomainsCollected = true,
            AccountsCollected = accountsCollected,
            AliasesCollected = aliasesCollected,
            Domains = domains.Select(d => new MailDomainDto { Name = d }).ToList(),
            Accounts = accounts.Select(a => new MailAccountDto { Email = a, Domain = a[(a.IndexOf('@') + 1)..] }).ToList(),
            Aliases = aliases.Select(a => new MailAliasDto { SourceEmail = a.Source, DestinationEmail = a.Destination }).ToList(),
            DkimKeys = [new MailDkimKeyDto { Domain = "example.com", Selector = "s1", PublicKey = "v=DKIM1; k=rsa; p=AAAA" }]
        };

    private static MailDataDto AdoptedServer() => Inventory(
        ["example.com", "second.test"],
        ["admin@example.com", "bob@second.test", "carol@second.test"],
        [("info@second.test", "bob@second.test")]);

    // Seeded rows are created "now"; age them past the grace so absence can be acted upon.
    private async Task AgeAllRowsAsync()
    {
        var old = _clock.GetUtcNow().UtcDateTime - MailStateReconciler.Grace - TimeSpan.FromMinutes(1);
        foreach (var d in _db.MailDomains) d.CreatedAt = old;
        foreach (var a in _db.MailAccounts) a.CreatedAt = old;
        foreach (var a in _db.MailAliases) a.CreatedAt = old;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AdoptsAnExistingConfiguration_ThenStaysIdempotent()
    {
        var first = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);
        var second = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.Equal(6, first.Created); // 2 domains + 3 accounts + 1 alias
        Assert.False(second.HasChanges);
        Assert.Equal(2, await _db.MailDomains.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await _db.MailAccounts.CountAsync(TestContext.Current.CancellationToken));
        Assert.All(_db.MailAccounts, a => Assert.Equal(MailRecordSource.Adopted, a.Source));
        var alias = await _db.MailAliases.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("info@second.test", "bob@second.test"), (alias.SourceEmail, alias.DestinationEmail));
        var example = await _db.MailDomains.SingleAsync(d => d.Name == "example.com", TestContext.Current.CancellationToken);
        Assert.Equal(("s1", "v=DKIM1; k=rsa; p=AAAA"), (example.DkimSelector, example.DkimPublicKey));
    }

    [Fact]
    public async Task VanishedRows_AreDeactivatedNeverDeleted_AndComeBack()
    {
        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);
        await AgeAllRowsAsync();

        var gone = Inventory(["example.com", "second.test"], ["admin@example.com", "bob@second.test"], []);
        var result = await _sut.ReconcileAsync(ServerId, gone, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.MarkedMissing); // carol + the alias
        var carol = await _db.MailAccounts.SingleAsync(a => a.Email == "carol@second.test", TestContext.Current.CancellationToken);
        Assert.False(carol.IsActive);
        Assert.NotNull(carol.MissingSince);
        Assert.Equal(1, await _db.MailAliases.CountAsync(TestContext.Current.CancellationToken));

        var back = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.Equal(2, back.Restored);
        Assert.True((await _db.MailAccounts.SingleAsync(a => a.Email == "carol@second.test", TestContext.Current.CancellationToken)).IsActive);
    }

    [Fact]
    public async Task OperatorDeactivatedRow_IsNotReactivatedByTheServerReport()
    {
        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);
        var bob = await _db.MailAccounts.SingleAsync(a => a.Email == "bob@second.test", TestContext.Current.CancellationToken);
        bob.IsActive = false;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.False(result.HasChanges);
        Assert.False((await _db.MailAccounts.SingleAsync(a => a.Email == "bob@second.test", TestContext.Current.CancellationToken)).IsActive);
    }

    [Fact]
    public async Task UncollectedLists_AreNeverUsedToDeactivate()
    {
        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);
        await AgeAllRowsAsync();

        var partial = Inventory(["example.com", "second.test"], [], [], accountsCollected: false, aliasesCollected: false);
        var result = await _sut.ReconcileAsync(ServerId, partial, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.MarkedMissing);
        Assert.All(_db.MailAccounts, a => Assert.True(a.IsActive));
    }

    [Fact]
    public async Task RecentRows_AreNotMarkedMissingDuringTheGrace()
    {
        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        var result = await _sut.ReconcileAsync(ServerId, Inventory(["example.com", "second.test"], [], []), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.MarkedMissing);
    }

    [Fact]
    public async Task PendingRemoval_IsNotResurrected()
    {
        _db.Tasks.Add(new ServerTask
        {
            ServerId = ServerId,
            Name = "Mail - delete account",
            Operation = OperationKind.MailDeleteAccount,
            Command = "carol@second.test",
            Status = TaskExecutionStatus.Pending
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.False(await _db.MailAccounts.AnyAsync(a => a.Email == "carol@second.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APurgeInProgress_AdoptsNothingBack()
    {
        // Do not recreate purged mail rows from an inventory captured while host erasure is still
        // in progress.
        _db.Tasks.Add(new ServerTask
        {
            ServerId = ServerId,
            Name = "Mail - erase the mail stack (reinstall)",
            Operation = OperationKind.MailPurge,
            Command = "-",
            Status = TaskExecutionStatus.Running
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.False(result.HasChanges);
        Assert.False(await _db.MailDomains.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AccountOwnedByAnotherServer_IsSkipped()
    {
        var other = new MailDomain { ServerId = 99, Name = "second.test" };
        other.Accounts.Add(new MailAccount { Email = "bob@second.test" });
        _db.MailDomains.Add(other);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, await _db.MailAccounts.CountAsync(a => a.Email == "bob@second.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ServerTruth_UpdatesAliasDestinationAndDkimSelector()
    {
        await _sut.ReconcileAsync(ServerId, AdoptedServer(), TestContext.Current.CancellationToken);
        var changed = Inventory(["example.com", "second.test"], ["admin@example.com", "bob@second.test", "carol@second.test"],
            [("info@second.test", "carol@second.test")]) with
        {
            DkimKeys = [new MailDkimKeyDto { Domain = "example.com", Selector = "s2", PublicKey = "v=DKIM1; k=rsa; p=BBBB" }]
        };

        var result = await _sut.ReconcileAsync(ServerId, changed, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Updated);
        Assert.Equal("carol@second.test", (await _db.MailAliases.SingleAsync(TestContext.Current.CancellationToken)).DestinationEmail);
        Assert.Equal("s2", (await _db.MailDomains.SingleAsync(d => d.Name == "example.com", TestContext.Current.CancellationToken)).DkimSelector);
    }
}
