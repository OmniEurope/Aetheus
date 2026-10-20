// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
namespace Aetheus.Back.Tests;

public class ServerServiceTests
{
    private readonly IServerRepository _repoMock = Substitute.For<IServerRepository>();
    private readonly IServerHeartbeatRepository _heartbeatRepoMock = Substitute.For<IServerHeartbeatRepository>();
    private readonly IHubContext<ServerHub> _hubMock = Substitute.For<IHubContext<ServerHub>>();
    private readonly IHubContext<AlertHub> _alertHubMock = Substitute.For<IHubContext<AlertHub>>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly BackgroundServicesOptions _backgroundOptions = new();
    private readonly ServerService _sut;

    public ServerServiceTests()
    {
        var clientsMock = Substitute.For<IHubClients>();
        var clientProxyMock = Substitute.For<IClientProxy>();
        var groupMock = Substitute.For<IClientProxy>();
        clientsMock.All.Returns(clientProxyMock);
        clientsMock.Group(Arg.Any<string>()).Returns(groupMock);
        clientsMock.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(groupMock);
        _hubMock.Clients.Returns(clientsMock);

        var alertClientsMock = Substitute.For<IHubClients>();
        alertClientsMock.Group(Arg.Any<string>()).Returns(groupMock);
        alertClientsMock.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(groupMock);
        _alertHubMock.Clients.Returns(alertClientsMock);

        _sut = new ServerService(_repoMock, _heartbeatRepoMock, _hubMock, _alertHubMock, _auditMock,
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(_backgroundOptions), TimeProvider.System, Substitute.For<IDbTransactionScope>(),
            updateConfirmation: null);
    }

    // --- GetServersAsync ---

    [Fact]
    public async Task GetServersAsync_ReturnsMappedResult()
    {
        var serverDtos = new List<ServerDto>
        {
            new()
            {
                Id = 1, Name = "web-01", Hostname = "web-01.local",
                Status = ServerStatus.Online, Type = ServerType.Docker,
                Tags = ["web", "prod"]
            }
        };
        _repoMock.GetServersPagedProjectedAsync(null, null, false, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((serverDtos, 1));

        var result = await _sut.GetServersAsync(new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("web-01", result.Items[0].Name);
        Assert.Contains("web", result.Items[0].Tags);
        Assert.Contains("prod", result.Items[0].Tags);
    }

    [Fact]
    public async Task GetServersAsync_WithFilters_PassesFiltersToRepo()
    {
        _repoMock.GetServersPagedProjectedAsync("web", null, false, ServerType.Docker, ServerStatus.Online, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<ServerDto>(), 0));

        var result = await _sut.GetServersAsync(
            new PaginationRequest { Page = 1, PageSize = 10, Search = "web" },
            ServerType.Docker, ServerStatus.Online, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetServersAsync_EmptyList_ReturnsEmpty()
    {
        _repoMock.GetServersPagedProjectedAsync(null, null, false, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<ServerDto>(), 0));

        var result = await _sut.GetServersAsync(new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    // --- GetServerDetailAsync ---

    [Fact]
    public async Task GetServerDetailAsync_Found_ReturnsMappedDto()
    {
        var server = new Server
        {
            Id = 1,
            Name = "prod-01",
            Hostname = "prod-01.local",
            OsDescription = "Ubuntu 22.04",
            IpAddress = "10.0.0.1",
            AgentVersion = "1.0.0",
            Status = ServerStatus.Online,
            Type = ServerType.Docker,
            Tags = "[\"web\"]",
            Metrics = [new ServerMetric
            {
                CpuPercent = 45.5, MemoryUsedMb = 2048, MemoryTotalMb = 4096,
                DiskUsedGb = 50, DiskTotalGb = 100, BuildCacheBytes = 1234,
                BuildCacheReclaimableBytes = 567, DockerVolumesBytes = 890,
                BuildCacheAvailable = true, DockerInventoryAvailable = true,
                Timestamp = new DateTime(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc)
            }],
            Services = [new ServiceInfo { Name = "nginx", Type = ServiceType.Systemd, Status = "active", IsRunning = true }],
            Tasks = [new ServerTask { Id = 1, ServerId = 1, Name = "deploy", Command = "ls", Status = TaskExecutionStatus.Success }],
            DockerContainers = [new DockerContainer { ContainerId = "abc123", Name = "web", Image = "nginx:latest", State = "running", Status = "Up" }],
            DockerImages = [new DockerImage { ImageId = "sha256:abc", Repository = "nginx", Tag = "latest", Size = "142MB" }],
            DockerComposeStacks = [new DockerComposeStack { Name = "monitoring", Status = "running(2)", ConfigFile = "/opt/compose.yml", RunningCount = 2, TotalCount = 2 }],
            DockerNetworks = [new DockerNetwork { NetworkId = "net1", Name = "bridge", Driver = "bridge", Scope = "local" }],
            DockerVolumes = [new DockerVolume { Name = "data", Driver = "local", Mountpoint = "/var/lib/docker/volumes/data" }],
            ApacheState = new ApacheState { IsRunning = true, Version = "2.4.54", Pid = 1234, ConfigRoot = "/etc/apache2" },
            ApacheModules = [new ApacheModule { Name = "mod_ssl", Type = "shared", IsEnabled = true }],
            ApacheVirtualHosts = [new ApacheVirtualHost { ServerName = "example.com", Port = 443, DocumentRoot = "/var/www", ConfigFile = "example.conf", IsEnabled = true }]
        };
        _repoMock.GetServerDetailAsync(1, Arg.Any<CancellationToken>()).Returns(server);

        var result = await _sut.GetServerDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("prod-01", result.Name);
        Assert.Equal("Ubuntu 22.04", result.OsDescription);
        Assert.Equal(45.5, result.CpuPercent);
        Assert.Equal(2048, result.MemoryUsedMb);
        Assert.Equal(4096, result.MemoryTotalMb);
        Assert.Equal(1234, result.StorageDiagnostics.BuildCacheBytes);
        Assert.Equal(567, result.StorageDiagnostics.BuildCacheReclaimableBytes);
        Assert.Equal(890, result.StorageDiagnostics.DockerVolumesBytes);
        Assert.True(result.StorageDiagnostics.BuildCacheAvailable);
        Assert.True(result.StorageDiagnostics.DockerInventoryAvailable);
        var installedServices = result.Services.Where(s => s.IsInstalled).ToList();
        Assert.Single(installedServices);
        Assert.Equal("nginx", installedServices[0].Name);
        Assert.True(installedServices[0].IsManageable);
        Assert.Contains(result.Services, s => !s.IsInstalled);
        Assert.Single(result.RecentTasks);
        Assert.NotNull(result.Docker);
        Assert.Single(result.Docker.Containers);
        Assert.Equal("web", result.Docker.Containers[0].Name);
        Assert.Single(result.Docker.Images);
        Assert.Single(result.Docker.ComposeStacks);
        Assert.Single(result.Docker.Networks);
        Assert.Single(result.Docker.Volumes);
        Assert.NotNull(result.Apache);
        Assert.True(result.Apache.IsRunning);
        Assert.Equal("2.4.54", result.Apache.Version);
        Assert.Single(result.Apache.Modules);
        Assert.Single(result.Apache.VirtualHosts);
    }

    [Fact]
    public async Task GetServerDetailAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetServerDetailAsync(99, Arg.Any<CancellationToken>()).Returns((Server?)null);

        var result = await _sut.GetServerDetailAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetServerDetailAsync_NoMetrics_DefaultsToZero()
    {
        var server = new Server
        {
            Id = 1,
            Name = "empty",
            Hostname = "h",
            OsDescription = "os",
            IpAddress = "0.0.0.0",
            AgentVersion = "1.0",
            Status = ServerStatus.Offline,
            Tags = "[]",
            Metrics = [],
            Services = [],
            Tasks = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        _repoMock.GetServerDetailAsync(1, Arg.Any<CancellationToken>()).Returns(server);

        var result = await _sut.GetServerDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.CpuPercent);
        Assert.Equal(0, result.MemoryUsedMb);
        Assert.Equal(0, result.DiskUsedGb);
    }

    // --- UpdateServerAsync ---

    [Fact]
    public async Task UpdateServerAsync_Found_UpdatesAndReturnsDto()
    {
        var server = new Server { Id = 1, Name = "old", Hostname = "h", Tags = "[]", Status = ServerStatus.Online };
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UpdateServerAsync(1, new UpdateServerRequest
        {
            Name = "new-name",
            Tags = ["staging"],
            Status = ServerStatus.Disabled,
            Type = ServerType.Build
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new-name", result.Name);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Updated", "Server", 1, "new-name", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateServerAsync_PartialUpdate_OnlyChangesProvided()
    {
        var server = new Server { Id = 1, Name = "keep", Hostname = "h", Tags = "[\"old\"]", Status = ServerStatus.Online, Type = ServerType.Normal };
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.UpdateServerAsync(1, new UpdateServerRequest { Name = "renamed" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("renamed", server.Name);
        Assert.Equal(ServerType.Normal, server.Type);
    }

    [Fact]
    public async Task UpdateServerAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindServerAsync(99, Arg.Any<CancellationToken>()).Returns((Server?)null);

        var result = await _sut.UpdateServerAsync(99, new UpdateServerRequest { Name = "x" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _auditMock.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // --- ProcessHeartbeatAsync ---

    [Fact]
    public async Task ProcessHeartbeatAsync_ServerFound_UpdatesStatusAndStoresData_AndDeduplicatesDockerImages()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            Status = ServerStatus.Offline,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        _heartbeatRepoMock.AddMetricAsync(Arg.Any<ServerMetric>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceServicesAsync(Arg.Any<int>(), Arg.Any<List<ServiceInfo>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerContainersAsync(Arg.Any<int>(), Arg.Any<List<DockerContainer>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerImagesAsync(Arg.Any<int>(), Arg.Any<List<DockerImage>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerComposeStacksAsync(Arg.Any<int>(), Arg.Any<List<DockerComposeStack>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerNetworksAsync(Arg.Any<int>(), Arg.Any<List<DockerNetwork>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerVolumesAsync(Arg.Any<int>(), Arg.Any<List<DockerVolume>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var heartbeat = new ServerHeartbeatDto
        {
            CpuPercent = 55,
            MemoryUsedMb = 2048,
            MemoryTotalMb = 4096,
            StorageDiagnostics = new StorageDiagnosticsDto
            {
                BuildCacheBytes = 1234,
                BuildCacheReclaimableBytes = 567,
                DeploymentOnly = true,
                BuildActive = false,
                LastBuildAttemptAtUtc = DateTime.UtcNow,
                DryRun = true
            },
            Disks = [new DiskInfoDto { TotalGb = 100, UsedGb = 50 }],
            Services = [new ServiceInfoDto { Name = "nginx", Type = ServiceType.Systemd, Status = "active", IsRunning = true }],
            Docker = new DockerDataDto
            {
                Containers = [new DockerContainerDto { ContainerId = "c1", Name = "web", Image = "nginx", State = "running", Status = "Up" }],
                Images =
                [
                    new DockerImageDto { ImageId = "i1", Repository = "<none>", Tag = "<none>", Size = "142MB" },
                    new DockerImageDto { ImageId = "i1", Repository = "nginx", Tag = "latest", Size = "142MB" }
                ],
                ComposeStacks = [new DockerComposeStackDto { Name = "stack1", Status = "running", ConfigFile = "/c.yml" }],
                Networks = [new DockerNetworkDto { NetworkId = "n1", Name = "bridge", Driver = "bridge", Scope = "local" }],
                Volumes = [new DockerVolumeDto { Name = "vol1", Driver = "local", Mountpoint = "/data" }]
            }
        };

        await _sut.ProcessHeartbeatAsync(1, heartbeat, ct: TestContext.Current.CancellationToken);

        Assert.Equal(ServerStatus.Online, server.Status);
        await _heartbeatRepoMock.Received(1).AddMetricAsync(Arg.Is<ServerMetric>(m =>
            m.CpuPercent == 55 && m.DiskUsedGb == 50 && m.DiskTotalGb == 100
            && m.BuildCacheBytes == 1234 && m.BuildCacheReclaimableBytes == 567
            && m.DeploymentOnly && !m.BuildActive && m.LastBuildAttemptAtUtc.HasValue
            && m.StorageMaintenanceDryRun && m.Timestamp > default(DateTime)),
            Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceServicesAsync(server.Id, Arg.Is<List<ServiceInfo>>(l => l.Count == 1), Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceDockerContainersAsync(server.Id, Arg.Is<List<DockerContainer>>(l => l.Count == 1), Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceDockerImagesAsync(server.Id,
            Arg.Is<List<DockerImage>>(l => l.Count == 1 && l[0].Repository == "nginx"), Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceDockerComposeStacksAsync(server.Id, Arg.Is<List<DockerComposeStack>>(l => l.Count == 1), Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceDockerNetworksAsync(server.Id, Arg.Is<List<DockerNetwork>>(l => l.Count == 1), Arg.Any<CancellationToken>());
        await _heartbeatRepoMock.Received(1).ReplaceDockerVolumesAsync(server.Id, Arg.Is<List<DockerVolume>>(l => l.Count == 1), Arg.Any<CancellationToken>());
        await _repoMock.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        var alertClient = _alertHubMock.Clients.Group(HubGroups.Alerts);
        await alertClient.Received(1).SendCoreAsync(
            "AlertTriggered",
            Arg.Is<object?[]>(arguments => IsStoragePolicyAlert(arguments)),
            Arg.Any<CancellationToken>());
    }

    private static bool IsStoragePolicyAlert(object?[] arguments)
    {
        if (arguments.Length != 1 || arguments[0] is not AlertTriggeredDto alert) return false;
        return alert.RuleName == "StoragePolicy"
            && alert.Metric == "BuildOnDeploymentTarget"
            && alert.Severity == "Critical";
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_InventoryPersistenceFails_PresenceWasPersistedFirst()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            Status = ServerStatus.Offline,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(
            Task.CompletedTask,
            Task.FromException(new InvalidOperationException("inventory persistence failed")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ProcessHeartbeatAsync(1, new ServerHeartbeatDto(), ct: TestContext.Current.CancellationToken));

        Assert.Equal("inventory persistence failed", error.Message);
        Assert.Equal(ServerStatus.Online, server.Status);
        Assert.True(server.LastHeartbeat > default(DateTime));
        await _repoMock.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ServerNotFound_DoesNothing()
    {
        _repoMock.FindServerAsync(99, Arg.Any<CancellationToken>()).Returns((Server?)null);

        await _sut.ProcessHeartbeatAsync(99, new ServerHeartbeatDto
        {
            Disks = [],
            Services = [],
            Docker = new DockerDataDto()
        }, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_MultipleDisks_SumsDiskValues()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);
        _heartbeatRepoMock.AddMetricAsync(Arg.Any<ServerMetric>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceServicesAsync(Arg.Any<int>(), Arg.Any<List<ServiceInfo>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerContainersAsync(Arg.Any<int>(), Arg.Any<List<DockerContainer>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerImagesAsync(Arg.Any<int>(), Arg.Any<List<DockerImage>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerComposeStacksAsync(Arg.Any<int>(), Arg.Any<List<DockerComposeStack>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerNetworksAsync(Arg.Any<int>(), Arg.Any<List<DockerNetwork>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerVolumesAsync(Arg.Any<int>(), Arg.Any<List<DockerVolume>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.ProcessHeartbeatAsync(1, new ServerHeartbeatDto
        {
            Disks = [
                new DiskInfoDto { TotalGb = 100, UsedGb = 40 },
                new DiskInfoDto { TotalGb = 200, UsedGb = 80 }
            ],
            Services = [],
            Docker = new DockerDataDto()
        }, ct: TestContext.Current.CancellationToken);

        await _heartbeatRepoMock.Received(1).AddMetricAsync(
            Arg.Is<ServerMetric>(m => m.DiskTotalGb == 300 && m.DiskUsedGb == 120),
            Arg.Any<CancellationToken>());
    }

    // --- GetServerNamesAsync ---

    [Fact]
    public async Task GetServerNamesAsync_ReturnsList()
    {
        _repoMock.GetServerNamesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["web-01", "db-01"]);

        var result = await _sut.GetServerNamesAsync(accessibleIds: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains("web-01", result);
    }

    // --- GetServerProjectsAsync ---

    [Fact]
    public async Task GetServerProjectsAsync_ReturnsMappedList()
    {
        _repoMock.GetProjectsForServerPagedAsync(
                1, null, 1, PaginationRequest.DefaultPageSize, null, false, Arg.Any<CancellationToken>())
            .Returns((new List<Project>
            {
                new() { Id = 10, Name = "MyProject", Description = "desc", Status = ProjectStatus.Active }
            }, 1));

        var result = await _sut.GetServerProjectsAsync(1, new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("MyProject", result.Items[0].Name);
        Assert.Equal(10, result.Items[0].Id);
    }

    // --- GetServerPipelinesAsync ---

    [Fact]
    public async Task GetServerPipelinesAsync_ReturnsMappedList()
    {
        _repoMock.GetPipelinesForServerAsync(1, Arg.Any<CancellationToken>())
            .Returns([new Pipeline { Id = 5, Name = "Build", TriggerType = PipelineTriggerType.Manual, ProjectId = 1, Project = new Project { Name = "P1" } }]);

        var result = await _sut.GetServerPipelinesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Build", result[0].Name);
        Assert.Equal("P1", result[0].ProjectName);
    }

    // --- GetServerVariableLibrariesAsync ---

    [Fact]
    public async Task GetServerVariableLibrariesAsync_ReturnsMappedList()
    {
        _repoMock.GetVariableLibrariesForServerAsync(1, Arg.Any<CancellationToken>())
            .Returns([new VariableLibrary { Id = 3, Name = "Vars", Description = "d", ProjectId = 1, Project = new Project { Name = "P1" }, Entries = [new VariableLibraryEntry()] }]);

        var result = await _sut.GetServerVariableLibrariesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Vars", result[0].Name);
        Assert.Equal(1, result[0].EntryCount);
    }

    // --- GetServerVaultsAsync ---

    [Fact]
    public async Task GetServerVaultsAsync_ReturnsMappedList()
    {
        _repoMock.GetVaultsForServerAsync(1, Arg.Any<CancellationToken>())
            .Returns([new Vault { Id = 7, Name = "Secrets", Description = "d", ProjectId = 1, Project = new Project { Name = "P1" }, Secrets = [new VaultSecret()] }]);

        var result = await _sut.GetServerVaultsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Secrets", result[0].Name);
        Assert.Equal(1, result[0].SecretCount);
    }


    // --- GetServerTasksAsync ---

    [Fact]
    public async Task GetServerTasksAsync_ReturnsPaginatedResult()
    {
        var tasks = new List<ServerTask>
        {
            new()
            {
                Id = 1,
                ServerId = 1,
                Name = "deploy",
                Command = "echo hi",
                Status = TaskExecutionStatus.Success,
                PipelineRunId = 42,
                PipelineStepRunId = 84
            }
        };
        _repoMock.GetTasksPagedAsync(1, 1, 10, Arg.Any<CancellationToken>())
            .Returns((tasks, 1));

        var result = await _sut.GetServerTasksAsync(1, new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("deploy", result.Items[0].Name);
        Assert.Equal(42, result.Items[0].PipelineRunId);
        Assert.Equal(84, result.Items[0].PipelineStepRunId);
    }

    // --- GetServerLogsAsync ---

    [Fact]
    public async Task GetServerLogsAsync_ReturnsPaginatedResult()
    {
        var logs = new List<TaskLog>
        {
            new() { Id = 1, TaskId = 1, Level = TaskLogLevel.Info, Message = "Started" }
        };
        _repoMock.GetLogsPagedAsync(1, 1, 10, Arg.Any<IReadOnlyList<GridFilter>?>(), Arg.Any<IReadOnlyList<GridSort>?>(), Arg.Any<CancellationToken>())
            .Returns((logs, 1));

        var result = await _sut.GetServerLogsAsync(1, new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Started", result.Items[0].Message);
    }

    /// <summary>The grid's column filters and its single sort key reach the repository.</summary>
    [Fact]
    public async Task GetServerLogsAsync_PassesFiltersAndSortToTheRepository()
    {
        var filters = new List<GridFilter> { new() { Field = "Level", Operator = GridFilterOperator.In, Value = "Error" } };
        _repoMock.GetLogsPagedAsync(1, 1, 50, Arg.Any<IReadOnlyList<GridFilter>?>(), Arg.Any<IReadOnlyList<GridSort>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<TaskLog>(), 0));

        await _sut.GetServerLogsAsync(1, new PaginationRequest
        {
            Page = 1,
            PageSize = 50,
            Filters = filters,
            SortBy = "Level",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).GetLogsPagedAsync(
            1, 1, 50,
            Arg.Is<IReadOnlyList<GridFilter>?>(sent => sent != null && sent.Single().Field == "Level"),
            Arg.Is<IReadOnlyList<GridSort>?>(sent => sent != null && sent.Single().Field == "Level" && sent.Single().Descending),
            Arg.Any<CancellationToken>());
    }

    // --- ContactAgentAsync ---

    [Fact]
    public async Task ContactAgentAsync_UnknownServer_ReturnsNull()
    {
        _repoMock.FindServerAsync(42, Arg.Any<CancellationToken>())
            .Returns((Server?)null);

        var result = await _sut.ContactAgentAsync(42, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ContactAgentAsync_FreshHeartbeat_IsReachable()
    {
        _backgroundOptions.ServerHeartbeatTimeout = TimeSpan.FromMinutes(2);
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server
            {
                Id = 1,
                Name = "web-01",
                AgentVersion = "1.4.2",
                LastHeartbeat = DateTime.UtcNow.AddSeconds(-10)
            });

        var result = await _sut.ContactAgentAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.Reachable);
        Assert.Null(result.Error);
        Assert.Equal("1.4.2", result.AgentVersion);
        Assert.NotNull(result.SecondsSinceLastHeartbeat);
    }

    [Fact]
    public async Task ContactAgentAsync_StaleHeartbeat_IsNotReachableWithError()
    {
        _backgroundOptions.ServerHeartbeatTimeout = TimeSpan.FromMinutes(2);
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server
            {
                Id = 1,
                Name = "web-01",
                LastHeartbeat = DateTime.UtcNow.AddMinutes(-30)
            });

        var result = await _sut.ContactAgentAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.Reachable);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.NotNull(result.LastHeartbeat);
    }

    [Fact]
    public async Task ContactAgentAsync_NeverReported_ReturnsNeverReportedError()
    {
        _repoMock.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Name = "web-01" });

        var result = await _sut.ContactAgentAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.Reachable);
        Assert.Null(result.LastHeartbeat);
        Assert.Null(result.SecondsSinceLastHeartbeat);
        Assert.Contains("never reported", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // --- F-023: OsType self-heal at heartbeat ---

    [Fact]
    public async Task ProcessHeartbeatAsync_UnknownOsType_SelfHealsFromOsDescription()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            OsType = OsType.Unknown,
            OsDescription = "Ubuntu 22.04 LTS",
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(OsType.Linux, server.OsType);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_KnownOsType_IsNotOverwritten()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            OsType = OsType.Windows,
            OsDescription = "Ubuntu 22.04 LTS",
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(OsType.Windows, server.OsType);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_MissingContract_DoesNotInferPreviousProtocolOrCapabilities()
    {
        var server = new Server
        {
            Id = 1,
            Name = "srv",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"agent.self-update\"]",
            PipelineRunnerEnabled = true,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat(), ct: TestContext.Current.CancellationToken);

        Assert.Null(server.AgentProtocolVersion);
        Assert.Equal("[]", server.AgentCapabilitiesJson);
        Assert.False(server.PipelineRunnerEnabled);
    }

    // --- Pipeline-runner gate self-heal at heartbeat (secure-by-default) ---

    [Fact]
    public async Task ProcessHeartbeatAsync_LegacyEmptySudoersInventory_PreservesKnownCapabilities()
    {
        var server = CapabilityServer(enabled: true);
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat(), ct: TestContext.Current.CancellationToken);

        Assert.True(server.PackageManagementAvailable);
        Assert.True(server.DeploymentTargetAvailable);
        Assert.True(server.MailSetupAvailable);
        Assert.True(server.TeamspeakSetupAvailable);
        Assert.True(server.PatchManagementAvailable);
        Assert.True(server.FirewallManagementAvailable);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_AuthoritativeEmptySudoersInventory_RevokesRemovedCapabilities()
    {
        var server = CapabilityServer(enabled: true);
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with { SudoersInventoryAvailable = true },
            ct: TestContext.Current.CancellationToken);

        Assert.False(server.PackageManagementAvailable);
        Assert.False(server.DeploymentTargetAvailable);
        Assert.False(server.MailSetupAvailable);
        Assert.False(server.TeamspeakSetupAvailable);
        Assert.False(server.PatchManagementAvailable);
        Assert.False(server.FirewallManagementAvailable);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_MissingCapabilityContract_DoesNotEnableDeployment()
    {
        var server = CapabilityServer(enabled: false);
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with
        {
            SudoersHashes = new Dictionary<string, string> { ["aetheus-deploy"] = new('A', 64) }
        }, ct: TestContext.Current.CancellationToken);

        Assert.False(server.DeploymentTargetAvailable);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ModernAgent_DisablesDeploymentWhenFunctionalProbeFailed()
    {
        var server = CapabilityServer(enabled: true);
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with
        {
            AgentCapabilities = [AgentCapabilities.PipelineBuild],
            SudoersInventoryAvailable = true,
            SudoersHashes = new Dictionary<string, string> { ["aetheus-deploy"] = new('A', 64) }
        }, ct: TestContext.Current.CancellationToken);

        Assert.False(server.DeploymentTargetAvailable);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ModernAgent_EnablesDeploymentOnlyAfterFunctionalProbe()
    {
        var server = CapabilityServer(enabled: false);
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with
        {
            AgentCapabilities = [AgentCapabilities.PipelineBuild, AgentCapabilities.Deployment],
            SudoersInventoryAvailable = true,
            SudoersHashes = new Dictionary<string, string> { ["aetheus-deploy"] = new('A', 64) }
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(server.DeploymentTargetAvailable);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ToolchainUnavailable_DisablesEnabledRunner()
    {
        var server = new Server
        {
            Id = 1,
            Name = "web-01",
            Hostname = "h",
            OsDescription = "Linux",
            OsType = OsType.Linux,
            PipelineRunnerEnabled = true,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with { PipelineRunnerAvailable = false }, ct: TestContext.Current.CancellationToken);

        Assert.False(server.PipelineRunnerEnabled);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ToolchainAvailable_DoesNotReEnableDisabledRunner()
    {
        // Self-heals DOWN only: a heartbeat reporting the toolchain present must NOT override a
        // deliberate admin disable.
        var server = new Server
        {
            Id = 1,
            Name = "web-01",
            Hostname = "h",
            OsDescription = "Linux",
            OsType = OsType.Linux,
            PipelineRunnerEnabled = false,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with
        {
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = [AgentCapabilities.PipelineBuild],
            PipelineRunnerAvailable = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.False(server.PipelineRunnerEnabled);
    }

    [Fact]
    public async Task ProcessHeartbeatAsync_ToolchainAvailable_PreservesEnabledRunner()
    {
        var server = new Server
        {
            Id = 1,
            Name = "web-01",
            Hostname = "h",
            OsDescription = "Linux",
            OsType = OsType.Linux,
            PipelineRunnerEnabled = true,
            Services = [],
            DockerContainers = [],
            DockerImages = [],
            DockerComposeStacks = [],
            DockerNetworks = [],
            DockerVolumes = []
        };
        SetupHeartbeatMocks(server);

        await _sut.ProcessHeartbeatAsync(1, MinimalHeartbeat() with
        {
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = [AgentCapabilities.PipelineBuild],
            PipelineRunnerAvailable = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(server.PipelineRunnerEnabled);
    }

    private void SetupHeartbeatMocks(Server server)
    {
        _repoMock.FindServerAsync(server.Id, Arg.Any<CancellationToken>()).Returns(server);
        _heartbeatRepoMock.AddMetricAsync(Arg.Any<ServerMetric>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceServicesAsync(Arg.Any<int>(), Arg.Any<List<ServiceInfo>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerContainersAsync(Arg.Any<int>(), Arg.Any<List<DockerContainer>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerImagesAsync(Arg.Any<int>(), Arg.Any<List<DockerImage>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerComposeStacksAsync(Arg.Any<int>(), Arg.Any<List<DockerComposeStack>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerNetworksAsync(Arg.Any<int>(), Arg.Any<List<DockerNetwork>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _heartbeatRepoMock.ReplaceDockerVolumesAsync(Arg.Any<int>(), Arg.Any<List<DockerVolume>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private static ServerHeartbeatDto MinimalHeartbeat() => new()
    {
        CpuPercent = 10,
        MemoryUsedMb = 512,
        MemoryTotalMb = 4096,
        Disks = [new DiskInfoDto { TotalGb = 100, UsedGb = 25 }],
        Services = [],
        Docker = new DockerDataDto()
    };

    private static Server CapabilityServer(bool enabled) => new()
    {
        Id = 1,
        Name = "web-01",
        Hostname = "h",
        OsDescription = "Linux",
        OsType = OsType.Linux,
        PackageManagementAvailable = enabled,
        DeploymentTargetAvailable = enabled,
        MailSetupAvailable = enabled,
        TeamspeakSetupAvailable = enabled,
        PatchManagementAvailable = enabled,
        FirewallManagementAvailable = enabled,
        Services = [],
        DockerContainers = [],
        DockerImages = [],
        DockerComposeStacks = [],
        DockerNetworks = [],
        DockerVolumes = []
    };
}
