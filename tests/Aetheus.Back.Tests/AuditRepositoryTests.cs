// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class AuditRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuditRepository _repo;

    public AuditRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new AuditRepository(_db);

        _db.AuditLogs.AddRange(
            new AuditLog { Username = "admin", Action = "Created", EntityType = "Server", EntityId = 1, Details = "Created server Alpha", Timestamp = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc) },
            new AuditLog { Username = "admin", Action = "Updated", EntityType = "Server", EntityId = 1, Details = "Updated hostname", Timestamp = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc) },
            new AuditLog { Username = "ops", Action = "Deleted", EntityType = "Pipeline", EntityId = 2, Details = "Deleted pipeline Beta", Timestamp = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc) },
            new AuditLog { Username = "ops", Action = "Created", EntityType = "Task", EntityId = 3, Timestamp = new DateTime(2026, 2, 10, 10, 0, 0, DateTimeKind.Utc) },
            new AuditLog { Username = "admin", Action = "Updated", EntityType = "Project", EntityId = 4, Details = "Renamed project", Timestamp = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc) }
        );
        _db.SaveChanges();
    }

    [Fact]
    public async Task HeaderFilters_TimestampRangeWithHours_ActionList_AndText_AreAppliedToPageAndCount()
    {
        // Timestamp ranges include hours; action lists and text columns travel as column filters
        // alongside typed parameters.
        IReadOnlyList<GridFilter> filters =
        [
            new GridFilter
            {
                Field = "Timestamp", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-01-10T09:00:00Z",
                SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-02-01T10:00:00Z"
            },
            new GridFilter { Field = "Action", Operator = GridFilterOperator.In, Value = $"Created{GridFilter.ListSeparator}Updated" },
            new GridFilter { Field = "Username", Operator = GridFilterOperator.Contains, Value = "ADM" }
        ];

        var logs = await _repo.GetPagedAsync(0, 10, ct: TestContext.Current.CancellationToken, filters: filters);
        var count = await _repo.CountAsync(ct: TestContext.Current.CancellationToken, filters: filters);
        var typedAndFiltered = await _repo.CountAsync(entityType: "Server",
            ct: TestContext.Current.CancellationToken,
            filters: [new GridFilter { Field = "Details", Operator = GridFilterOperator.Contains, Value = "hostname" }]);

        Assert.Equal(2, count);
        Assert.Equal(["Updated", "Created"], logs.Select(log => log.Action));
        Assert.Equal(1, typedAndFiltered);
    }

    [Fact]
    public async Task GetPagedAsync_ReturnsAllOrderedByTimestampDesc()
    {
        var logs = await _repo.GetPagedAsync(0, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, logs.Count);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), logs[0].Timestamp);
    }

    [Fact]
    public async Task GetPagedAsync_RespectsPagination()
    {
        var page1 = await _repo.GetPagedAsync(0, 2, ct: TestContext.Current.CancellationToken);
        var page2 = await _repo.GetPagedAsync(2, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.True(page1[0].Timestamp > page2[0].Timestamp);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersBySearch()
    {
        var logs = await _repo.GetPagedAsync(0, 10, search: "Alpha", ct: TestContext.Current.CancellationToken);
        Assert.Single(logs);
        Assert.Equal("Created server Alpha", logs[0].Details);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersBySearchInUsername()
    {
        var logs = await _repo.GetPagedAsync(0, 10, search: "ops", ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByAction()
    {
        var logs = await _repo.GetPagedAsync(0, 10, action: "Updated", ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("Updated", l.Action));
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByEntityType()
    {
        var logs = await _repo.GetPagedAsync(0, 10, entityType: "Server", ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("Server", l.EntityType));
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByDateFrom()
    {
        var logs = await _repo.GetPagedAsync(0, 10, dateFrom: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByDateTo()
    {
        var logs = await _repo.GetPagedAsync(0, 10, dateTo: new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByDateRange()
    {
        var logs = await _repo.GetPagedAsync(0, 10,
            dateFrom: new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            dateTo: new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task GetPagedAsync_CombinesFilters()
    {
        var logs = await _repo.GetPagedAsync(0, 10, action: "Created", entityType: "Server", ct: TestContext.Current.CancellationToken);
        Assert.Single(logs);
        Assert.Equal("admin", logs[0].Username);
    }

    [Fact]
    public async Task CountAsync_ReturnsTotal()
    {
        var count = await _repo.CountAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task CountAsync_RespectsSameFiltersAsGetPaged()
    {
        var count = await _repo.CountAsync(action: "Deleted", ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountAsync_FiltersByDateRange()
    {
        var count = await _repo.CountAsync(
            dateFrom: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            dateTo: new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetDistinctActionsAsync_ReturnsDistinctSorted()
    {
        var actions = await _repo.GetDistinctActionsAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, actions.Count);
        Assert.Equal(["Created", "Deleted", "Updated"], actions);
    }

    [Fact]
    public async Task GetDistinctEntityTypesAsync_ReturnsDistinctSorted()
    {
        var types = await _repo.GetDistinctEntityTypesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, types.Count);
        Assert.Equal(["Pipeline", "Project", "Server", "Task"], types);
    }

    [Fact]
    public async Task AddAsync_PersistsLog()
    {
        var log = new AuditLog { Username = "test", Action = "Created", EntityType = "Setting" };
        await _repo.AddAsync(log, ct: TestContext.Current.CancellationToken);

        var count = await _repo.CountAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(6, count);
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
