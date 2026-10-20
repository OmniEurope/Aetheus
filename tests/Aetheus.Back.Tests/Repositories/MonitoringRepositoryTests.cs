// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class MonitoringRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MonitoringRepository _repo;

    public MonitoringRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new MonitoringRepository(_db);
    }

    [Fact]
    public async Task GetDashboardServersAsync_ReturnsAllServers()
    {
        _db.Servers.AddRange(
            new Server { Name = "srv1", Hostname = "h1" },
            new Server { Name = "srv2", Hostname = "h2" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetDashboardServersAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetDashboardServersAsync_Empty_ReturnsEmpty()
    {
        var result = await _repo.GetDashboardServersAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task CountPendingTasksAsync_CountsOnlyPending()
    {
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Tasks.AddRange(
            new ServerTask { Name = "t1", Command = "c", ServerId = server.Id, Status = TaskExecutionStatus.Pending },
            new ServerTask { Name = "t2", Command = "c", ServerId = server.Id, Status = TaskExecutionStatus.Running },
            new ServerTask { Name = "t3", Command = "c", ServerId = server.Id, Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.CountPendingTasksAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountRunningPipelinesAsync_CountsOnlyRunning()
    {
        var pipeline = new Pipeline { Name = "p", ProjectId = 1 };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.CountRunningPipelinesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetRecentRunsAsync_ReturnsOrderedWithIncludes()
    {
        var pipeline = new Pipeline { Name = "p", ProjectId = 1 };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow.AddHours(-2) },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow.AddHours(-1) },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentRunsAsync(2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].StartedAt > result[1].StartedAt);
        Assert.Equal("p", result[0].PipelineName);
    }

    /// <summary>A run whose snapshotted URL is an internal smart-HTTP clone URL carries the id of
    /// the repository it names, whatever host the URL was re-homed to, so the dashboard links its
    /// commit to the Aetheus page. An external URL, or a mirror path naming no repository, carries
    /// none (the front then keeps the external link, or text).</summary>
    [Fact]
    public async Task GetRecentRunsAsync_ResolvesTheInternalRepositoryOfEachRun()
    {
        var pipeline = new Pipeline { Name = "p", ProjectId = 13 };
        _db.Pipelines.Add(pipeline);
        var repository = new GitInternalRepo { ProjectId = 13, Name = "Aetheus self", Slug = "aetheus-self" };
        _db.GitInternalRepos.AddRange(
            repository,
            new GitInternalRepo { ProjectId = 14, Name = "Other", Slug = "aetheus-self" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow;
        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = now, RepositoryUrl = "https://host.docker.internal:5301/git/13/aetheus-self.git" },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = now.AddMinutes(-1), RepositoryUrl = "https://localhost:5301/git/13/aetheus-self" },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = now.AddMinutes(-2), RepositoryUrl = "https://github.com/acme/aetheus-self.git" },
            new PipelineRun { PipelineId = pipeline.Id, StartedAt = now.AddMinutes(-3), RepositoryUrl = "https://localhost:5301/git/13/unknown.git" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRecentRunsAsync(10, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(new int?[] { repository.Id, repository.Id, null, null }, result.Select(run => run.RepositoryId));
    }

    [Theory]
    [InlineData("https://host.docker.internal:5301/git/13/aetheus-self.git", 13, "aetheus-self")]
    [InlineData("https://localhost:5301/git/7/demo/", 7, "demo")]
    [InlineData("https://github.com/acme/demo.git", null, null)]
    [InlineData("https://git.example/git/0/demo.git", null, null)]
    [InlineData("/srv/repos/demo", null, null)]
    [InlineData(null, null, null)]
    public void PipelineRunRepositoryLinks_ParsesOnlyTheMirrorPath(string? url, int? projectId, string? slug)
    {
        var path = PipelineRunRepositoryLinks.TryParse(url);

        Assert.Equal(projectId, path?.ProjectId);
        Assert.Equal(slug, path?.Slug);
    }

    [Fact]
    public async Task GetServerMetricsSinceAsync_FiltersAndOrders()
    {
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow;
        _db.ServerMetrics.AddRange(
            new ServerMetric { ServerId = server.Id, CpuPercent = 10, Timestamp = now.AddHours(-5) },
            new ServerMetric { ServerId = server.Id, CpuPercent = 20, Timestamp = now.AddHours(-1) },
            new ServerMetric { ServerId = server.Id, CpuPercent = 30, Timestamp = now });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerMetricsSinceAsync(server.Id, now.AddHours(-2), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal(20, result[0].CpuPercent);
    }

    [Fact]
    public async Task GetServerMetricsSinceAsync_OtherServer_ReturnsEmpty()
    {
        var server = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.ServerMetrics.Add(new ServerMetric { ServerId = server.Id, CpuPercent = 10, Timestamp = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerMetricsSinceAsync(999, DateTime.UtcNow.AddDays(-1), ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    public void Dispose() => _db.Dispose();
}
