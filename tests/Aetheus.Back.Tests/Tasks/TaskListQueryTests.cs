// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary> The task lists' column header filters (server, executor and status lists, creation
/// range, name and id) are applied by the query, before the count, inside the caller's scope.
/// </summary>
public sealed class TaskListQueryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public void Dispose() => _db.Dispose();

    private static GridFilter In(string field, params string[] values) =>
        new() { Field = field, Operator = GridFilterOperator.In, Value = string.Join(GridFilter.ListSeparator, values) };

    private async Task SeedAsync()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "web-1", Hostname = "w1" },
            new Server { Id = 2, Name = "db-1", Hostname = "d1" });
        _db.Tasks.AddRange(
            new ServerTask { Id = 1, ServerId = 1, Name = "build", Command = "c", Status = TaskExecutionStatus.Failed, Executor = ExecutorType.Docker },
            new ServerTask { Id = 2, ServerId = 1, Name = "test", Command = "c", Status = TaskExecutionStatus.Success, Executor = ExecutorType.Shell },
            new ServerTask { Id = 3, ServerId = 2, Name = "backup", Command = "c", Status = TaskExecutionStatus.Failed, Executor = ExecutorType.Shell });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Repository_AppliesTheHeaderFilters_BeforeTheCount()
    {
        await SeedAsync();
        var repo = new TaskRepository(_db, TimeProvider.System);

        var (items, total) = await repo.GetTasksPagedAsync(
            null, 1, 10, ct: TestContext.Current.CancellationToken,
            columnFilters: [In("ServerName", "WEB-1"), In("Status", "Failed", "Timeout")]);

        Assert.Equal(1, total);
        Assert.Equal("build", Assert.Single(items).Name);
    }

    [Fact]
    public async Task Repository_KeepsTheCallersScope_UnderAFilter()
    {
        await SeedAsync();
        var repo = new TaskRepository(_db, TimeProvider.System);

        var (_, total) = await repo.GetTasksPagedAsync(
            null, 1, 10, accessibleServerIds: [2], ct: TestContext.Current.CancellationToken,
            columnFilters: [In("Status", "Failed")]);

        Assert.Equal(1, total);
    }

    [Fact]
    public async Task FilterValues_ListTheServerNamesOfTheScope()
    {
        await SeedAsync();
        var repo = new TaskRepository(_db, TimeProvider.System);

        var all = await repo.GetTaskFilterValuesAsync(null, null, TestContext.Current.CancellationToken);
        var scoped = await repo.GetTaskFilterValuesAsync([2], null, TestContext.Current.CancellationToken);

        Assert.Equal(["db-1", "web-1"], all.ServerNames);
        Assert.Equal(["db-1"], scoped.ServerNames);
    }

    [Fact]
    public void CreationRange_ExecutorList_AndId_Filter()
    {
        var server = new Server { Name = "web-1" };
        var tasks = new List<ServerTask>
        {
            new() { Id = 1, Name = "a", Server = server, Executor = ExecutorType.Docker, CreatedAt = new DateTime(2026, 9, 1, 7, 59, 0, DateTimeKind.Utc) },
            new() { Id = 2, Name = "b", Server = server, Executor = ExecutorType.Docker, CreatedAt = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc) },
            new() { Id = 3, Name = "c", Server = server, Executor = ExecutorType.Shell, CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ExitCode = 2 }
        }.AsQueryable();

        var inRange = TaskListQuery.Columns.ApplyFilters(tasks,
        [
            new GridFilter
            {
                Field = "CreatedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01T08:00:00Z",
                SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-01T09:00:00Z"
            },
            In("Executor", "Docker")
        ]).ToList();
        var byId = TaskListQuery.Columns.ApplyFilters(tasks, [new GridFilter { Field = "Id", Operator = GridFilterOperator.Equals, Value = "3" }]).ToList();
        var byExitCode = TaskListQuery.Columns.ApplyFilters(tasks, [new GridFilter { Field = "ExitCode", Operator = GridFilterOperator.Equals, Value = "2" }]).ToList();

        Assert.Equal(2, Assert.Single(inRange).Id);
        Assert.Equal(3, Assert.Single(byId).Id);
        Assert.Equal(3, Assert.Single(byExitCode).Id);
    }

    [Fact]
    public void AColumnOutsideTheMap_IsRefused()
    {
        var tasks = new List<ServerTask>().AsQueryable();

        Assert.Throws<BadRequestException>(() => TaskListQuery.Columns.ApplyFilters(tasks,
            [new GridFilter { Field = "Command", Operator = GridFilterOperator.Contains, Value = "rm" }]).ToList());
    }
}
