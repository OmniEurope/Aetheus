// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using TaskExecutionStatus = Aetheus.Shared.Components.Tasks.TaskExecutionStatus;

namespace Aetheus.Back.Tests.Repositories;

public class ServerRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ServerRepository _repo;
    private readonly ServerHeartbeatRepository _heartbeatRepo;

    public ServerRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new ServerRepository(_db, TimeProvider.System);
        // Same AppDbContext instance, deliberately: that shared context is what keeps one heartbeat
        // one transaction after the split, so the tests must exercise the pair the same way.
        _heartbeatRepo = new ServerHeartbeatRepository(_db);
    }

    private Server CreateServer(string name = "srv", ServerType type = ServerType.Normal, ServerStatus status = ServerStatus.Online)
    {
        var s = new Server { Name = name, Hostname = name + ".local", Status = status, Type = type };
        _db.Servers.Add(s);
        return s;
    }

    [Fact]
    public async Task GetServersPagedAsync_ReturnsPagedResults()
    {
        for (var i = 0; i < 5; i++) CreateServer($"srv{i}");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetServersPagedAsync(null, null, false, null, null, 1, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetServersPagedAsync_FiltersByType()
    {
        CreateServer("a", ServerType.Build);
        CreateServer("b", ServerType.Normal);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetServersPagedAsync(null, null, false, ServerType.Build, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("a", items[0].Name);
    }

    [Fact]
    public async Task GetServersPagedAsync_FiltersByStatus()
    {
        CreateServer("a", status: ServerStatus.Online);
        CreateServer("b", status: ServerStatus.Offline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetServersPagedAsync(null, null, false, null, ServerStatus.Offline, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("b", items[0].Name);
    }

    [Fact]
    public async Task GetServersPagedAsync_FiltersBySearch()
    {
        CreateServer("alpha");
        CreateServer("beta");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync("alpha", null, false, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Single(items);
    }

    [Fact]
    public async Task GetServersPagedAsync_SortsByName()
    {
        CreateServer("zeta");
        CreateServer("alpha");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync(null, "name", false, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("alpha", items[0].Name);
    }

    [Fact]
    public async Task GetServersPagedAsync_SortsByNameDescending()
    {
        CreateServer("alpha");
        CreateServer("zeta");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync(null, "name", true, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("zeta", items[0].Name);
    }

    [Fact]
    public async Task GetServersPagedAsync_SortsByStatus()
    {
        CreateServer("a", status: ServerStatus.Online);
        CreateServer("b", status: ServerStatus.Offline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync(null, "status", false, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetServersPagedAsync_SortsByLastHeartbeat()
    {
        var s1 = CreateServer("old");
        s1.LastHeartbeat = DateTime.UtcNow.AddHours(-2);
        var s2 = CreateServer("new");
        s2.LastHeartbeat = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync(null, "lastheartbeat", false, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal("old", items[0].Name);
    }

    [Fact]
    public async Task GetServersPagedAsync_FiltersAccessibleIds()
    {
        CreateServer("a");
        CreateServer("b");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var all = await _db.Servers.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, _) = await _repo.GetServersPagedAsync(null, null, false, null, null, 1, 10, [all[0].Id], ct: TestContext.Current.CancellationToken);
        Assert.Single(items);
    }

    [Fact]
    public async Task GetServerDetailAsync_ReturnsWithIncludes()
    {
        var s = CreateServer("detail");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServiceInfos.Add(new ServiceInfo { ServerId = s.Id, Name = "svc", Status = "running" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerDetailAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.Services);
    }

    [Fact]
    public async Task GetServerDetailAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetServerDetailAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindServerAsync_Found()
    {
        var s = CreateServer("find");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetServerWithCollectionsAsync_ReturnsWithIncludes()
    {
        var s = CreateServer("col");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetServerWithCollectionsAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("col", result.Name);
    }

    [Fact]
    public async Task AddMetricAsync_AddsToContext()
    {
        var s = CreateServer("met");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var metric = new ServerMetric { ServerId = s.Id, CpuPercent = 50, Timestamp = DateTime.UtcNow };
        await _heartbeatRepo.AddMetricAsync(metric, ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.ServerMetrics.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceServicesAsync_ReplacesAll()
    {
        var s = CreateServer("svc");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServiceInfos.Add(new ServiceInfo { ServerId = s.Id, Name = "old", Status = "running" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.Services).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        var newSvcs = new List<ServiceInfo> { new() { ServerId = s.Id, Name = "new", Status = "active" } };
        await _heartbeatRepo.ReplaceServicesAsync(s.Id, newSvcs, ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var svcs = await _db.ServiceInfos.Where(x => x.ServerId == s.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(svcs);
        Assert.Equal("new", svcs[0].Name);
    }

    [Fact]
    public async Task ReplaceDockerContainersAsync_ReplacesAll()
    {
        var s = CreateServer("dc");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.DockerContainers.Add(new DockerContainer { ServerId = s.Id, ContainerId = "old", Name = "old", Image = "img", Status = "up" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.DockerContainers).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceDockerContainersAsync(s.Id, [new() { ServerId = s.Id, ContainerId = "new", Name = "new", Image = "img", Status = "up" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.DockerContainers.CountAsync(c => c.ServerId == s.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceDockerImagesAsync_ReplacesAll()
    {
        var s = CreateServer("di");
        var foreign = CreateServer("di-foreign");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.DockerImages.Add(new DockerImage { ServerId = s.Id, ImageId = "old", Repository = "r", Tag = "1", Size = "1" });
        _db.DockerImages.Add(new DockerImage { ServerId = foreign.Id, ImageId = "kept", Repository = "r", Tag = "1", Size = "1" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.DockerImages).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceDockerImagesAsync(s.Id, [new() { ServerId = s.Id, ImageId = "new", Repository = "r", Tag = "2", Size = "2" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A no-op keeps "old", an append keeps both: only a real replacement leaves exactly "new",
        // and another server's inventory is left untouched.
        Assert.Equal(["new"], await _db.DockerImages.Where(i => i.ServerId == s.Id).Select(i => i.ImageId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["kept"], await _db.DockerImages.Where(i => i.ServerId == foreign.Id).Select(i => i.ImageId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceDockerComposeStacksAsync_ReplacesAll()
    {
        var s = CreateServer("dcs");
        var foreign = CreateServer("dcs-foreign");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.DockerComposeStacks.Add(new DockerComposeStack { ServerId = s.Id, Name = "old", Status = "up", ConfigFile = "/" });
        _db.DockerComposeStacks.Add(new DockerComposeStack { ServerId = foreign.Id, Name = "kept", Status = "up", ConfigFile = "/" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.DockerComposeStacks).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceDockerComposeStacksAsync(s.Id, [new() { ServerId = s.Id, Name = "new", Status = "up", ConfigFile = "/" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A no-op keeps "old", an append keeps both: only a real replacement leaves exactly "new",
        // and another server's inventory is left untouched.
        Assert.Equal(["new"], await _db.DockerComposeStacks.Where(c => c.ServerId == s.Id).Select(c => c.Name).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["kept"], await _db.DockerComposeStacks.Where(c => c.ServerId == foreign.Id).Select(c => c.Name).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceDockerNetworksAsync_ReplacesAll()
    {
        var s = CreateServer("dn");
        var foreign = CreateServer("dn-foreign");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.DockerNetworks.Add(new DockerNetwork { ServerId = s.Id, NetworkId = "old", Name = "old", Driver = "bridge" });
        _db.DockerNetworks.Add(new DockerNetwork { ServerId = foreign.Id, NetworkId = "kept", Name = "kept", Driver = "bridge" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.DockerNetworks).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceDockerNetworksAsync(s.Id, [new() { ServerId = s.Id, NetworkId = "new", Name = "new", Driver = "bridge" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A no-op keeps "old", an append keeps both: only a real replacement leaves exactly "new",
        // and another server's inventory is left untouched.
        Assert.Equal(["new"], await _db.DockerNetworks.Where(n => n.ServerId == s.Id).Select(n => n.NetworkId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["kept"], await _db.DockerNetworks.Where(n => n.ServerId == foreign.Id).Select(n => n.NetworkId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceDockerVolumesAsync_ReplacesAll()
    {
        var s = CreateServer("dv");
        var foreign = CreateServer("dv-foreign");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.DockerVolumes.Add(new DockerVolume { ServerId = s.Id, Name = "old", Driver = "local" });
        _db.DockerVolumes.Add(new DockerVolume { ServerId = foreign.Id, Name = "kept", Driver = "local" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.DockerVolumes).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceDockerVolumesAsync(s.Id, [new() { ServerId = s.Id, Name = "new", Driver = "local" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A no-op keeps "old", an append keeps both: only a real replacement leaves exactly "new",
        // and another server's inventory is left untouched.
        Assert.Equal(["new"], await _db.DockerVolumes.Where(v => v.ServerId == s.Id).Select(v => v.Name).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["kept"], await _db.DockerVolumes.Where(v => v.ServerId == foreign.Id).Select(v => v.Name).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceApacheDataAsync_ReplacesAll()
    {
        var s = CreateServer("apache");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ApacheStates.Add(new ApacheState { ServerId = s.Id, IsRunning = true, Version = "2.4" });
        _db.ApacheModules.Add(new ApacheModule { ServerId = s.Id, Name = "old" });
        _db.ApacheVirtualHosts.Add(new ApacheVirtualHost { ServerId = s.Id, ServerName = "old", DocumentRoot = "/" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.ApacheState).Include(x => x.ApacheModules).Include(x => x.ApacheVirtualHosts).FirstAsync(x => x.Id == s.Id, TestContext.Current.CancellationToken))!;

        var newState = new ApacheState { ServerId = s.Id, IsRunning = false, Version = "2.5" };
        await _heartbeatRepo.ReplaceApacheDataAsync(s.Id, newState, [new() { ServerId = s.Id, Name = "new" }], [new() { ServerId = s.Id, ServerName = "new", DocumentRoot = "/" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.ApacheModules.CountAsync(m => m.ServerId == s.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceApacheDataAsync_NullState_RemovesOldState()
    {
        var s = CreateServer("apache2");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ApacheStates.Add(new ApacheState { ServerId = s.Id, IsRunning = true, Version = "2.4" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.ApacheState).Include(x => x.ApacheModules).Include(x => x.ApacheVirtualHosts).FirstAsync(x => x.Id == s.Id, TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceApacheDataAsync(s.Id, null, [], [], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await _db.ApacheStates.CountAsync(a => a.ServerId == s.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceTeamspeakDataAsync_ReplacesStateAndAllInventories()
    {
        var server = CreateServer("teamspeak");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.TeamspeakStates.Add(new TeamspeakState { ServerId = server.Id, ServerName = "Old" });
        _db.TeamspeakChannels.Add(new TeamspeakChannel { ServerId = server.Id, ChannelId = 1, Name = "Old" });
        _db.TeamspeakClients.Add(new TeamspeakClient { ServerId = server.Id, ClientId = 1, Nickname = "Old" });
        _db.TeamspeakBans.Add(new TeamspeakBan { ServerId = server.Id, BanId = 1, Nickname = "Old" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _heartbeatRepo.ReplaceTeamspeakDataAsync(
            server.Id,
            new TeamspeakState { ServerId = server.Id, ServerName = "New", Platform = "Linux" },
            [new TeamspeakChannel { ServerId = server.Id, ChannelId = 2, Name = "New" }],
            [new TeamspeakClient { ServerId = server.Id, ClientId = 2, Nickname = "New" }],
            [new TeamspeakBan { ServerId = server.Id, BanId = 2, Nickname = "New" }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("New", (await _db.TeamspeakStates.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ServerName);
        Assert.Equal(2, (await _db.TeamspeakChannels.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ChannelId);
        Assert.Equal(2, (await _db.TeamspeakClients.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ClientId);
        Assert.Equal(2, (await _db.TeamspeakBans.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).BanId);
    }

    [Fact]
    public async Task ReplaceCertbotCertificatesAsync_ReplacesAll()
    {
        var s = CreateServer("cert");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.CertbotCertificates.Add(new CertbotCertificate { ServerId = s.Id, Name = "old", Domains = "old.com", ExpiryDate = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        s = (await _db.Servers.Include(x => x.CertbotCertificates).FirstAsync(x => x.Id == s.Id, cancellationToken: TestContext.Current.CancellationToken))!;

        await _heartbeatRepo.ReplaceCertbotCertificatesAsync(s.Id, [new() { ServerId = s.Id, Name = "new", Domains = "new", ExpiryDate = DateTime.UtcNow }], ct: TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var certs = await _db.CertbotCertificates.Where(c => c.ServerId == s.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(certs);
        Assert.Equal("new", certs[0].Name);
    }

    [Fact]
    public async Task GetStaleOnlineServersAsync_ReturnsStalOnly()
    {
        var s1 = CreateServer("stale");
        s1.Status = ServerStatus.Online;
        s1.LastHeartbeat = DateTime.UtcNow.AddMinutes(-10);
        var s2 = CreateServer("fresh");
        s2.Status = ServerStatus.Online;
        s2.LastHeartbeat = DateTime.UtcNow;
        var s3 = CreateServer("offline");
        s3.Status = ServerStatus.Offline;
        s3.LastHeartbeat = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetStaleOnlineServersAsync(TimeSpan.FromMinutes(5), ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("stale", result[0].Name);
    }

    [Fact]
    public async Task GetServerNamesAsync_ReturnsOrdered()
    {
        CreateServer("zeta");
        CreateServer("alpha");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var names = await _repo.GetServerNamesAsync((List<int>?)null, TestContext.Current.CancellationToken);
        Assert.Equal(2, names.Count);
        Assert.Equal("alpha", names[0]);
    }

    [Fact]
    public async Task GetTasksPagedAsync_ReturnsPaged()
    {
        var s = CreateServer("tasks");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
            _db.Tasks.Add(new ServerTask { ServerId = s.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetTasksPagedAsync(s.Id, 1, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetLogsPagedAsync_ReturnsPaged()
    {
        var s = CreateServer("logs");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var task = new ServerTask { ServerId = s.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
            _db.TaskLogs.Add(new TaskLog { TaskId = task.Id, Message = $"log{i}", Timestamp = DateTime.UtcNow.AddMinutes(-i) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetLogsPagedAsync(s.Id, 1, 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetLogsPagedAsync_LeavesOutTheAgentDirectiveLines()
    {
        var s = CreateServer("logs-directives");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var task = new ServerTask { ServerId = s.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.TaskLogs.AddRange(
            new TaskLog { TaskId = task.Id, Message = "##aetheus[pipelinemetric key=cpu.percent]12", Timestamp = DateTime.UtcNow },
            new TaskLog { TaskId = task.Id, Message = "##aetheus[setvariable name=X]1", Timestamp = DateTime.UtcNow },
            new TaskLog { TaskId = task.Id, Message = "done: ##aetheus[setvariable name=X]1", Timestamp = DateTime.UtcNow },
            new TaskLog { TaskId = task.Id, Message = "build succeeded", Timestamp = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetLogsPagedAsync(s.Id, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.DoesNotContain(items, log => log.Message.StartsWith("##aetheus[", StringComparison.Ordinal));
        Assert.Contains(items, log => log.Message == "build succeeded");
    }

    /// <summary> The level list and the time range narrow the whole log of the server before the
    /// count, and a requested sort replaces the newest-first order. </summary>
    [Fact]
    public async Task GetLogsPagedAsync_AppliesColumnFiltersAndSort()
    {
        var s = CreateServer("logs-filtered");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var task = new ServerTask { ServerId = s.Id, Name = "t", Command = "echo", Executor = ExecutorType.Shell, Status = TaskExecutionStatus.Pending };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var day = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var logs = new[]
        {
            (Log: new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Error, Message = "old error" }, At: day.AddDays(-5)),
            (Log: new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Error, Message = "error a" }, At: day),
            (Log: new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Warning, Message = "warning" }, At: day.AddHours(1)),
            (Log: new TaskLog { TaskId = task.Id, Level = TaskLogLevel.Info, Message = "info" }, At: day.AddHours(2))
        };
        _db.TaskLogs.AddRange(logs.Select(entry => entry.Log));
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        // The context stamps an added log with the current time; the test's own times are set afterwards.
        foreach (var (log, at) in logs) log.Timestamp = at;
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<GridFilter> filters =
        [
            new() { Field = "Level", Operator = GridFilterOperator.In, Value = $"Error{GridFilter.ListSeparator}Warning" },
            new() { Field = "Timestamp", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-09-10T00:00:00" }
        ];

        var (items, total) = await _repo.GetLogsPagedAsync(
            s.Id, 1, 10, filters, [new GridSort { Field = "Message", Descending = false }], TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(["error a", "warning"], items.Select(log => log.Message));
    }

    [Fact]
    public async Task GetLogsPagedAsync_UnknownColumn_IsABadRequest()
    {
        var s = CreateServer("logs-unknown");
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BadRequestException>(() => _repo.GetLogsPagedAsync(
            s.Id, 1, 10, [new GridFilter { Field = "OriginalMessage", Operator = GridFilterOperator.Contains, Value = "x" }],
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectIdsForServerAsync_ReturnsDistinctIds()
    {
        var s = CreateServer("proj");
        var project = new Project { Name = "P1" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s2", Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.GetProjectIdsForServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(ids);
        Assert.Equal(project.Id, ids[0]);
    }

    [Fact]
    public async Task GetPipelinesForServerAsync_ReturnsPipelines()
    {
        var s = CreateServer("pipe");
        var project = new Project { Name = "P1" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline { Name = "pipe1", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPipelinesForServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("pipe1", result[0].Name);
    }

    [Fact]
    public async Task GetVariableLibrariesForServerAsync_ReturnsLibraries()
    {
        var s = CreateServer("vars");
        var project = new Project { Name = "P1" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        _db.VariableLibraries.Add(new VariableLibrary { Name = "VL1", ProjectId = project.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetVariableLibrariesForServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("VL1", result[0].Name);
    }

    [Fact]
    public async Task GetVaultsForServerAsync_ReturnsVaults()
    {
        var s = CreateServer("vaults");
        var project = new Project { Name = "P1" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        _db.Vaults.Add(new Vault { Name = "V1", ProjectId = project.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetVaultsForServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("V1", result[0].Name);
    }

    [Fact]
    public async Task ServerOwnedResources_AppearWithoutPipelineHistory()
    {
        var server = CreateServer("owned-resources");
        var project = new Project { Name = "Direct" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var projectServer = new ProjectServer
        {
            ProjectId = project.Id,
            ServerId = server.Id,
            DisplayName = "Production",
            Host = server.Hostname
        };
        _db.ProjectServers.Add(projectServer);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.VariableLibraries.Add(new VariableLibrary { Name = "Direct library", ProjectServerId = projectServer.Id });
        _db.Vaults.Add(new Vault { Name = "Direct vault", ProjectServerId = projectServer.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var projects = await _repo.GetProjectsForServerAsync(server.Id, ct: TestContext.Current.CancellationToken);
        var libraries = await _repo.GetVariableLibrariesForServerAsync(server.Id, ct: TestContext.Current.CancellationToken);
        var vaults = await _repo.GetVaultsForServerAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Direct", Assert.Single(projects).Name);
        Assert.Equal("Production", Assert.Single(libraries).ProjectServer!.DisplayName);
        Assert.Equal("Production", Assert.Single(vaults).ProjectServer!.DisplayName);
    }

    [Fact]
    public async Task GetProjectsForServerAsync_ReturnsProjects()
    {
        var s = CreateServer("projs");
        var project = new Project { Name = "P1" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline { Name = "pipe", ProjectId = project.Id, YamlDefinition = "" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, ServerId = s.Id, StepName = "s1", Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetProjectsForServerAsync(s.Id, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("P1", result[0].Name);
    }

    [Fact]
    public async Task GetProjectsForServerPagedAsync_ReturnsRequestedPageAndTotal()
    {
        var server = CreateServer("paged-projs");
        var projects = new[] { new Project { Name = "Alpha" }, new Project { Name = "Zulu" } };
        _db.Projects.AddRange(projects);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var project in projects)
        {
            var pipeline = new Pipeline { Name = $"pipe-{project.Name}", ProjectId = project.Id, YamlDefinition = "" };
            _db.Pipelines.Add(pipeline);
            await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running };
            _db.PipelineRuns.Add(run);
            await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
            _db.PipelineStepRuns.Add(new PipelineStepRun
            {
                PipelineRunId = run.Id,
                ServerId = server.Id,
                StepName = "step",
                Status = TaskExecutionStatus.Pending
            });
        }
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetProjectsForServerPagedAsync(
            server.Id, null, 2, 1, "Name", false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Single(items);
        Assert.Equal("Zulu", items[0].Name);
    }

    public void Dispose() => _db.Dispose();
}

