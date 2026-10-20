// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class MonitoringServiceTests
{
    private readonly IMonitoringRepository _repoMock = Substitute.For<IMonitoringRepository>();
    private readonly MonitoringService _sut;

    public MonitoringServiceTests()
    {
        _sut = new MonitoringService(_repoMock, TimeProvider.System);
        _repoMock.GetRecentProjectsAsync(Arg.Any<int>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task GetDashboardAsync_AggregatesAllData()
    {
        var servers = new List<ServerDto>
        {
            new() { Id = 1, Name = "S1", Hostname = "h1", Status = ServerStatus.Online, Type = ServerType.Normal },
            new() { Id = 2, Name = "S2", Hostname = "h2", Status = ServerStatus.Offline, Type = ServerType.Docker },
            new() { Id = 3, Name = "S3", Hostname = "h3", Status = ServerStatus.Online, Type = ServerType.Build }
        };
        _repoMock.GetDashboardServersAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(servers);
        _repoMock.CountPendingTasksAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(5);
        _repoMock.CountRunningPipelinesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(2);
        _repoMock.CountProjectsAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(14);
        _repoMock.GetRecentRunsAsync(10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetDashboardAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalServers);
        Assert.Equal(2, result.OnlineServers);
        Assert.Equal(1, result.OfflineServers);
        Assert.Equal(5, result.PendingTasks);
        Assert.Equal(2, result.RunningPipelines);
        // The tile badge counts every readable project, not only the ten recent ones listed.
        Assert.Equal(14, result.TotalProjects);
        Assert.Equal(3, result.Servers.Count);
    }

    [Fact]
    public async Task GetDashboardAsync_MapsRecentRuns()
    {
        _repoMock.GetDashboardServersAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.CountPendingTasksAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.CountRunningPipelinesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);

        var runs = new List<PipelineRunDto>
        {
            new()
            {
                Id = 1, PipelineId = 10, PipelineName = "CI", Status = PipelineStatus.Success,
                StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
                Steps = [new PipelineStepRunDto { Id = 1, StepName = "Build", StageName = "build", Status = TaskExecutionStatus.Success, TriggeredRunId = 2 }]
            }
        };
        _repoMock.GetRecentRunsAsync(10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(runs);

        var result = await _sut.GetDashboardAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result.RecentRuns);
        Assert.Equal("CI", result.RecentRuns[0].PipelineName);
        Assert.Single(result.RecentRuns[0].Steps);
        Assert.Equal("Build", result.RecentRuns[0].Steps[0].StepName);
        Assert.Equal(2, result.RecentRuns[0].Steps[0].TriggeredRunId);
    }

    [Fact]
    public async Task GetDashboardAsync_EmptyServers_ReturnsZeroCounts()
    {
        _repoMock.GetDashboardServersAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.CountPendingTasksAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.CountRunningPipelinesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.GetRecentRunsAsync(10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetDashboardAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalServers);
        Assert.Equal(0, result.OnlineServers);
        Assert.Equal(0, result.OfflineServers);
        Assert.Empty(result.RecentRuns);
        Assert.Empty(result.Servers);
    }

    [Fact]
    public async Task GetDashboardAsync_MapsServerFields()
    {
        var now = DateTime.UtcNow;
        var servers = new List<ServerDto>
        {
            new() { Id = 5, Name = "Web", Hostname = "web.local", OsDescription = "Ubuntu", Status = ServerStatus.Online, Type = ServerType.Docker, LastHeartbeat = now, CreatedAt = now }
        };
        _repoMock.GetDashboardServersAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(servers);
        _repoMock.CountPendingTasksAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.CountRunningPipelinesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.GetRecentRunsAsync(10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetDashboardAsync(ct: TestContext.Current.CancellationToken);

        var s = result.Servers[0];
        Assert.Equal(5, s.Id);
        Assert.Equal("Web", s.Name);
        Assert.Equal("web.local", s.Hostname);
        Assert.Equal("Ubuntu", s.OsDescription);
        Assert.Equal(ServerStatus.Online, s.Status);
        Assert.Equal(ServerType.Docker, s.Type);
        Assert.Equal(now, s.LastHeartbeat);
    }

    [Fact]
    public async Task GetServerMetricsAsync_ReturnsMappedMetrics()
    {
        var ts = DateTime.UtcNow;
        var metrics = new List<ServerMetric>
        {
            new()
            {
                ServerId = 1, CpuPercent = 45.5, MemoryUsedMb = 2048, MemoryTotalMb = 8192,
                DiskUsedGb = 50, DiskTotalGb = 200, BuildCacheBytes = 1234,
                BuildCacheReclaimableBytes = 567, DockerVolumesBytes = 890, Timestamp = ts
            }
        };
        _repoMock.GetServerMetricsSinceAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(metrics);

        var result = await _sut.GetServerMetricsAsync(1, 24, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(45.5, result[0].CpuPercent);
        Assert.Equal(2048, result[0].MemoryUsedMb);
        Assert.Equal(8192, result[0].MemoryTotalMb);
        Assert.Equal(50, result[0].DiskUsedGb);
        Assert.Equal(200, result[0].DiskTotalGb);
        Assert.Equal(1234, result[0].BuildCacheBytes);
        Assert.Equal(567, result[0].BuildCacheReclaimableBytes);
        Assert.Equal(890, result[0].DockerVolumesBytes);
        Assert.Equal(ts, result[0].Timestamp);
    }

    [Fact]
    public async Task GetServerMetricsAsync_EmptyMetrics_ReturnsEmpty()
    {
        _repoMock.GetServerMetricsSinceAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetServerMetricsAsync(1, 12, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDashboardAsync_NullPipelineName_FallsBackToEmpty()
    {
        _repoMock.GetDashboardServersAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.CountPendingTasksAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);
        _repoMock.CountRunningPipelinesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(0);

        var runs = new List<PipelineRunDto>
        {
            new() { Id = 1, PipelineId = 1, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow, PipelineName = string.Empty, Steps = [] }
        };
        _repoMock.GetRecentRunsAsync(10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns(runs);

        var result = await _sut.GetDashboardAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result.RecentRuns);
        Assert.Equal(string.Empty, result.RecentRuns[0].PipelineName);
    }
}

