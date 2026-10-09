// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class LogRepositoryTests : IAsyncLifetime
{
    private readonly AppDbContext _db;
    private readonly LogRepository _repo;

    public LogRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new LogRepository(_db);
    }

    public async ValueTask InitializeAsync()
    {
        _db.Servers.Add(new Server { Id = 1, Name = "srv", Hostname = "host" });
        _db.Tasks.Add(new ServerTask { Id = 1, Name = "task1", ServerId = 1, Command = "echo" });
        await _db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task GetTaskLogsAsync_ReturnsOrderedLogs()
    {
        // SaveChangesAsync stamps every Added TaskLog with the same "now", so the distinct
        // timestamps are applied after the insert (Modified state is not re-stamped). The
        // insertion order is the reverse of the timestamp order, so only a real sort passes.
        _db.TaskLogs.AddRange(
            new TaskLog { TaskId = 1, Message = "second" },
            new TaskLog { TaskId = 1, Message = "first" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var origin = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _db.TaskLogs.Local.Single(l => l.Message == "second").Timestamp = origin.AddSeconds(1);
        _db.TaskLogs.Local.Single(l => l.Message == "first").Timestamp = origin;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        var logs = await _repo.GetTaskLogsAsync(1, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
        Assert.Equal("first", logs[0].Message);
        Assert.Equal("second", logs[1].Message);
    }

    [Fact]
    public async Task GetTaskLogsAsync_Empty_ReturnsEmpty()
    {
        var logs = await _repo.GetTaskLogsAsync(999, null, ct: TestContext.Current.CancellationToken);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task AddLogAsync_PersistsLog()
    {
        var log = new TaskLog { TaskId = 1, Message = "new log", Timestamp = DateTime.UtcNow };
        var result = await _repo.AddLogAsync(log, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Id > 0);
        Assert.Equal(1, await _db.TaskLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddLogsAsync_PersistsBatch()
    {
        var logs = new List<TaskLog>
        {
            new() { TaskId = 1, Message = "a", Timestamp = DateTime.UtcNow },
            new() { TaskId = 1, Message = "b", Timestamp = DateTime.UtcNow }
        };
        await _repo.AddLogsAsync(logs, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, await _db.TaskLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPipelineRunIdForTaskAsync_WithPipelineRun_ReturnsId()
    {
        var pipeline = new Pipeline { Name = "test", YamlDefinition = "name: test" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var task = await _db.Tasks.FindAsync([1], TestContext.Current.CancellationToken);
        task!.PipelineRunId = run.Id;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPipelineRunIdForTaskAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(run.Id, result);
    }

    [Fact]
    public async Task GetPipelineRunIdForTaskAsync_NoPipelineRun_ReturnsNull()
    {
        var result = await _repo.GetPipelineRunIdForTaskAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPipelineRunIdForTaskAsync_TaskNotFound_ReturnsNull()
    {
        var result = await _repo.GetPipelineRunIdForTaskAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTaskOutputVariableLinesAsync_ReturnsOnlyMarkerLines_IncludingMidLine()
    {
        // The OutputVariablePattern regex has no leading '^' anchor, so a marker can appear mid-line
        // (leading whitespace or a prefix). The SQL filter must use Contains, not StartsWith, or
        // these user-authored markers are silently dropped.
        _db.TaskLogs.AddRange(
            new TaskLog { TaskId = 1, Message = "##aetheus[setvariable name=A]1", Timestamp = DateTime.UtcNow },
            new TaskLog { TaskId = 1, Message = "  ##aetheus[setvariable name=B]2", Timestamp = DateTime.UtcNow.AddSeconds(1) },
            new TaskLog { TaskId = 1, Message = "done: ##aetheus[setvariable name=C]3", Timestamp = DateTime.UtcNow.AddSeconds(2) },
            new TaskLog { TaskId = 1, Message = "just a normal build line", Timestamp = DateTime.UtcNow.AddSeconds(3) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var lines = await _repo.GetTaskOutputVariableLinesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("name=A"));
        Assert.Contains(lines, l => l.Contains("name=B")); // leading whitespace
        Assert.Contains(lines, l => l.Contains("name=C")); // prefix text before the marker
        Assert.DoesNotContain(lines, l => l.Contains("normal build line"));
    }

    [Fact]
    public async Task DeleteLogsOlderThanAsync_DeletesOldLogs()
    {
        // Save both entities first (SaveChangesAsync stamps all Added entities with the same Timestamp).
        _db.TaskLogs.AddRange(
            new TaskLog { TaskId = 1, Message = "old" },
            new TaskLog { TaskId = 1, Message = "recent" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Back-date the "old" log (Modified state does not re-stamp Timestamp).
        var oldLog = _db.TaskLogs.First(l => l.Message == "old");
        oldLog.Timestamp = DateTime.UtcNow.AddDays(-10);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteLogsOlderThanAsync(DateTime.UtcNow.AddDays(-5), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await _db.TaskLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteLogsOlderThanAsync_NoOldLogs_ReturnsZero()
    {
        _db.TaskLogs.Add(new TaskLog { TaskId = 1, Message = "recent", Timestamp = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _repo.DeleteLogsOlderThanAsync(DateTime.UtcNow.AddDays(-5), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.TaskLogs.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}

