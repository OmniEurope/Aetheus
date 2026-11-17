// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// ADR-060: the number and the public identifier every task gets on insert. The row lock, the unique
/// filtered indexes and the transaction only exist on PostgreSQL.
/// </summary>
public sealed class ServerTaskNumberingIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task Save_NumbersEachServerOnItsOwn_AndTheUidCarriesTheCreationTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var (first, second) = (await SeedServerAsync(db, "first"), await SeedServerAsync(db, "second"));

        var a = NewTask(first);
        var b = NewTask(second);
        var c = NewTask(first);
        db.Tasks.AddRange(a, b, c);
        await db.SaveChangesAsync(ct);
        var d = NewTask(first);
        db.Tasks.Add(d);
        await db.SaveChangesAsync(ct);

        Assert.Equal([1, 2, 3], new[] { a, c, d }.Select(task => task.Number));
        Assert.Equal(1, b.Number);
        Assert.Equal(7, a.Uid!.Value.Version);
        Assert.Equal(
            new DateTimeOffset(a.CreatedAt).ToUnixTimeMilliseconds(),
            Convert.ToInt64(a.Uid.Value.ToString("N")[..12], 16));
        Assert.Equal(3, await CounterAsync(first, ct));
    }

    [Fact]
    public async Task ConcurrentSaves_OnOneServer_NeverShareANumber()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var setup = NewContext();
        var server = await SeedServerAsync(setup, "shared");

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext();
            db.Tasks.AddRange(NewTask(server), NewTask(server), NewTask(server));
            await db.SaveChangesAsync(ct);
        }, ct)));

        var numbers = await setup.Tasks.AsNoTracking().Select(task => task.Number).ToListAsync(ct);
        Assert.Equal(Enumerable.Range(1, 36), numbers.Order());
        Assert.Equal(36, await CounterAsync(server, ct));
    }

    /// <summary>The callers that build a task inside their own transaction keep their atomicity: a rollback
    /// removes the task and gives its number back.</summary>
    [Fact]
    public async Task AnAmbientTransaction_OwnsTheReservation_AndARollbackGivesTheNumberBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var server = await SeedServerAsync(db, "ambient");

        await using (var ambient = await db.Database.BeginTransactionAsync(ct))
        {
            db.Tasks.Add(NewTask(server));
            await db.SaveChangesAsync(ct);
            // The caller still owns the transaction: the save did not commit it.
            Assert.NotNull(db.Database.CurrentTransaction);
            await ambient.RollbackAsync(ct);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(0, await CounterAsync(server, ct));
        var next = NewTask(server);
        db.Tasks.Add(next);
        await db.SaveChangesAsync(ct);
        Assert.Equal(1, next.Number);
    }

    /// <summary>A failed insert rolls the reservation back, and the entity can be saved again.</summary>
    [Fact]
    public async Task AFailedSave_GivesTheNumberBack_AndARetryTakesItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var server = await SeedServerAsync(db, "retry");

        var task = NewTask(server);
        task.PipelineRunId = int.MaxValue; // No such run: the foreign key refuses the insert.
        db.Tasks.Add(task);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.Equal(0, await CounterAsync(server, ct));

        task.PipelineRunId = null;
        await db.SaveChangesAsync(ct);
        Assert.Equal(1, task.Number);
    }

    [Fact]
    public async Task ARetiredServer_StillNumbersItsTasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var server = await SeedServerAsync(db, "retired");
        await db.Servers.Where(row => row.Id == server)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DeletedAt, DateTime.UtcNow), ct);

        var task = NewTask(server);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);

        Assert.Equal(1, task.Number);
    }

    [Fact]
    public async Task AServerSavedWithItsFirstTasks_NumbersThemToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var organization = await SeedOrganizationAsync(db, "fresh");
        var server = NewServer("fresh", organization);
        var first = new ServerTask { Server = server, Name = "one", Command = "true" };
        var second = new ServerTask { Server = server, Name = "two", Command = "true" };
        db.Tasks.AddRange(first, second);

        await db.SaveChangesAsync(ct);

        Assert.Equal([1, 2], new[] { first, second }.Select(task => task.Number));
        Assert.Equal(2, await CounterAsync(server.Id, ct));
    }

    [Fact]
    public async Task TheUniqueIndexes_RefuseADuplicate_AndLeaveEmptyRowsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var server = await SeedServerAsync(db, "unique");
        var (first, second) = (NewTask(server), NewTask(server));
        db.Tasks.AddRange(first, second);
        await db.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Tasks.Where(task => task.Id == second.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Number, first.Number), ct));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Tasks.Where(task => task.Id == second.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Uid, first.Uid), ct));

        // Rows the backfill has not reached yet may all be empty at once.
        var emptied = await db.Tasks.Where(task => task.ServerId == server)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Number, 0).SetProperty(task => task.Uid, (Guid?)null), ct);
        Assert.Equal(2, emptied);
    }

    private static ServerTask NewTask(int serverId) => new() { ServerId = serverId, Name = "numbered", Command = "true" };

    private async Task<int> CounterAsync(int serverId, CancellationToken ct)
    {
        await using var db = NewContext();
        return await db.Servers.IgnoreQueryFilters().AsNoTracking()
            .Where(server => server.Id == serverId)
            .Select(server => server.TaskCounter)
            .SingleAsync(ct);
    }

    private static async Task<int> SeedOrganizationAsync(AppDbContext db, string name)
    {
        var organization = new Organization { Name = name, Slug = name };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return organization.Id;
    }

    private static Server NewServer(string name, int organizationId) => new()
    {
        Name = name,
        Hostname = $"{name}.test",
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task<int> SeedServerAsync(AppDbContext db, string name)
    {
        var server = NewServer(name, await SeedOrganizationAsync(db, name));
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return server.Id;
    }
}
