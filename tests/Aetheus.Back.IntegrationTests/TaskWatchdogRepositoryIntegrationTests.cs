// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aetheus.Back.IntegrationTests;

public sealed class TaskWatchdogRepositoryIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task GetStaleRunningTasksAsync_FiltersAndBoundsInsideOnePostgresCommand()
    {
        await fixture.ResetAsync();
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var seedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using (var seed = new AppDbContext(seedOptions))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var organizationId = await seed.Organizations.OrderBy(item => item.Id)
                .Select(item => item.Id)
                .FirstAsync(TestContext.Current.CancellationToken);
            var server = new Server
            {
                OrganizationId = organizationId,
                Name = "watchdog-budget",
                Hostname = "watchdog-budget.local",
                Status = ServerStatus.Online
            };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            seed.Tasks.AddRange(Enumerable.Range(1, 1005).Select(index => new ServerTask
            {
                ServerId = server.Id,
                Name = $"stale-{index}",
                Command = "scan",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddHours(-2).AddSeconds(index),
                TimeoutSeconds = 300
            }));
            seed.Tasks.Add(new ServerTask
            {
                ServerId = server.Id,
                Name = "fresh",
                Command = "scan",
                Status = TaskExecutionStatus.Running,
                StartedAt = now.UtcDateTime.AddMinutes(-1),
                TimeoutSeconds = 300
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var counter = new CommandCounter();
        var measuredOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        await using var measured = new AppDbContext(measuredOptions);

        var tasks = await new TaskRepository(measured, new FixedTimeProvider(now))
            .GetStaleRunningTasksAsync(
                TimeSpan.FromMinutes(30),
                TestContext.Current.CancellationToken);

        Assert.Equal(1000, tasks.Count);
        Assert.DoesNotContain(tasks, task => task.Name == "fresh");
        Assert.Equal(1, counter.Count);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
