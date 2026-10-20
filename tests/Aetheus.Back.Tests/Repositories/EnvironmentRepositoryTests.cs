// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Repositories;

public class EnvironmentRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly EnvironmentRepository _repo;

    public EnvironmentRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new EnvironmentRepository(_db);
    }

    [Fact]
    public async Task GetEnvironmentsPagedAsync_AppliesColumnFilters_BeforeTheCount()
    {
        // Type list, approval yes/no and name text are column filters of the query.
        _db.Environments.AddRange(
            new Environment { Name = "prod-eu", Type = EnvironmentType.Production, RequireApproval = true },
            new Environment { Name = "prod-us", Type = EnvironmentType.Production, RequireApproval = false },
            new Environment { Name = "staging", Type = EnvironmentType.Staging, RequireApproval = true });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetEnvironmentsPagedAsync(null, null, 1, 10, ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter { Field = "Type", Operator = GridFilterOperator.In, Value = $"Production{GridFilter.ListSeparator}Testing" },
                new GridFilter { Field = "RequireApproval", Operator = GridFilterOperator.Equals, Value = "True" },
                new GridFilter { Field = "Name", Operator = GridFilterOperator.Contains, Value = "PROD" }
            ]);

        Assert.Equal(1, total);
        Assert.Equal("prod-eu", Assert.Single(items).Name);
    }

    [Fact]
    public void UpdatedAtRange_Filters()
    {
        var environments = new List<Environment>
        {
            new() { Name = "old", UpdatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Name = "new", UpdatedAt = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc) }
        }.AsQueryable();

        var matching = EnvironmentListQuery.Columns.ApplyFilters(environments,
        [
            new GridFilter
            {
                Field = "UpdatedAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-01",
                SecondOperator = GridFilterOperator.LessThan, SecondValue = "2026-09-10"
            }
        ]).ToList();

        Assert.Equal("new", Assert.Single(matching).Name);
    }

    [Fact]
    public async Task GetEnvironmentsPagedAsync_ReturnsPagedResults()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Environments.AddRange(
            new Environment { Name = "Dev", ProjectId = project.Id },
            new Environment { Name = "Staging", ProjectId = project.Id },
            new Environment { Name = "Prod", ProjectId = project.Id }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetEnvironmentsPagedAsync(null, null, 1, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetEnvironmentsPagedAsync_WithSearch_Filters()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Environments.AddRange(
            new Environment { Name = "Dev", ProjectId = project.Id },
            new Environment { Name = "Staging", ProjectId = project.Id }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetEnvironmentsPagedAsync("Dev", null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetEnvironmentsPagedAsync_WithProjectId_Filters()
    {
        var p1 = new Project { Name = "P1" };
        var p2 = new Project { Name = "P2" };
        _db.Projects.AddRange(p1, p2);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Environments.AddRange(
            new Environment { Name = "Dev", ProjectId = p1.Id },
            new Environment { Name = "Staging", ProjectId = p2.Id }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetEnvironmentsPagedAsync(null, p1.Id, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("Dev", items[0].Name);
    }

    [Fact]
    public async Task GetEnvironmentsPagedAsync_WithAccessibleIds_Filters()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Environments.AddRange(
            new Environment { Name = "Dev", ProjectId = project.Id },
            new Environment { Name = "Staging", ProjectId = project.Id }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var dev = await _db.Environments.FirstAsync(e => e.Name == "Dev", cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetEnvironmentsPagedAsync(null, null, 1, 10, [dev.Id], ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetEnvironmentWithServersAsync_Found_IncludesServers()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var env = new Environment { Name = "Dev", ProjectId = project.Id };
        _db.Environments.Add(env);
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = env.Id, ServerId = server.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetEnvironmentWithServersAsync(env.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.Servers);
    }

    [Fact]
    public async Task GetEnvironmentWithServersAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetEnvironmentWithServersAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindEnvironmentAsync_Found()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var env = new Environment { Name = "Dev", ProjectId = project.Id };
        _db.Environments.Add(env);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindEnvironmentAsync(env.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindByNameAsync_Found()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Environments.Add(new Environment { Name = "Unique", ProjectId = project.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindByNameAsync("Unique", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindByNameAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindByNameAsync("Missing", ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task AddEnvironmentAsync_Persists()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddEnvironmentAsync(new Environment { Name = "New", ProjectId = project.Id }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Environments.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveEnvironmentAsync_Removes()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var env = new Environment { Name = "Del", ProjectId = project.Id };
        _db.Environments.Add(env);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemoveEnvironmentAsync(env, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.Environments.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Environments.Add(new Environment { Name = "Pending", ProjectId = project.Id });
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Environments.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();
}
