// SPDX-License-Identifier: EUPL-1.2
using System.Data;

namespace Aetheus.Back.Components.Tasks;

internal sealed class TaskIdentityBackfillRepository(AppDbContext db) : ITaskIdentityBackfillRepository
{
    private sealed record MissingIdentity(int Id, int ServerId, DateTime CreatedAt, Guid? Uid, int Number);

    public async Task<int?> FillNextBatchAsync(int afterId, int batchSize, CancellationToken ct = default)
    {
        // Bulk updates and row locks do not exist on InMemory, and nothing there needs a backfill.
        if (!db.Database.IsRelational()) return null;

        var rows = await db.Tasks.AsNoTracking()
            .Where(task => task.Id > afterId && (task.Uid == null || task.Number == 0))
            .OrderBy(task => task.Id)
            .Take(batchSize)
            .Select(task => new MissingIdentity(task.Id, task.ServerId, task.CreatedAt, task.Uid, task.Number))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return null;

        foreach (var server in rows.GroupBy(row => row.ServerId).OrderBy(group => group.Key))
            await FillServerAsync(server.Key, [.. server], ct).ConfigureAwait(false);

        return rows[^1].Id;
    }

    /// <summary>
    /// One transaction per server, under the row lock of its counter: a task inserted meanwhile either
    /// committed its number before the reads below, or waits for this commit.
    /// </summary>
    private async Task FillServerAsync(int serverId, List<MissingIdentity> rows, CancellationToken ct)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var servers = ServerTaskNumbering.AllServers(db).Where(server => server.Id == serverId);
        var locked = await servers
            .ExecuteUpdateAsync(setters => setters.SetProperty(server => server.TaskCounter, server => server.TaskCounter), ct)
            .ConfigureAwait(false);
        if (locked == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var counter = await servers.Select(server => server.TaskCounter).SingleAsync(ct).ConfigureAwait(false);
        var numbered = db.Tasks.Where(task => task.ServerId == serverId && task.Number > 0);
        var oldest = rows[0].Id;
        var floor = await numbered.Where(task => task.Id < oldest)
            .MaxAsync(task => (int?)task.Number, ct).ConfigureAwait(false) ?? 0;
        var held = (await numbered.Where(task => task.Number > floor && task.Number <= counter)
            .Select(task => task.Number)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        var (numbers, pastCounter) = AssignNumbers(rows, floor, counter, held);
        foreach (var row in rows)
        {
            var uid = row.Uid ?? ServerTaskUid.From(row.CreatedAt);
            var number = numbers[row.Id];
            await db.Tasks.Where(task => task.Id == row.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(task => task.Uid, uid).SetProperty(task => task.Number, number),
                    ct)
                .ConfigureAwait(false);
        }

        if (pastCounter > 0)
            await servers
                .ExecuteUpdateAsync(setters => setters.SetProperty(server => server.TaskCounter, server => server.TaskCounter + pastCounter), ct)
                .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows take the lowest free numbers after the highest one held by an older task of the server,
    /// oldest first, so the range the migration left for the history stays chronological and a restart
    /// resumes where the last batch stopped. A row the previous version inserted after the migration
    /// finds that range used and takes a number past the counter.
    /// </summary>
    private static (Dictionary<int, int> Numbers, int PastCounter) AssignNumbers(
        List<MissingIdentity> rows, int floor, int counter, HashSet<int> held)
    {
        var numbers = new Dictionary<int, int>(rows.Count);
        var candidate = floor;
        var pastCounter = 0;
        foreach (var row in rows)
        {
            if (row.Number > 0)
            {
                numbers[row.Id] = row.Number;
                continue;
            }

            do candidate++; while (candidate <= counter && held.Contains(candidate));
            numbers[row.Id] = candidate <= counter ? candidate : counter + ++pastCounter;
        }

        return (numbers, pastCounter);
    }
}
