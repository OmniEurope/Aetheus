// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.ServerConfigurations;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServerConfigurationServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ServerConfigurationService _sut;

    public ServerConfigurationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        var repo = new ServerConfigurationRepository(_db);
        _sut = new ServerConfigurationService(repo, TimeProvider.System, NSubstitute.Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Server> SeedServerAsync(Action<Server>? configure = null)
    {
        var server = new Server
        {
            Name = "test-server",
            Type = ServerType.Docker,
            Status = ServerStatus.Online,
            IpAddress = "10.0.0.1",
            AgentVersion = "1.0.0",
            OsDescription = "Linux",
            LastHeartbeat = DateTime.UtcNow,
            Tags = "[\"web\",\"prod\"]"
        };
        configure?.Invoke(server);
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();
        return server;
    }

    // --- ExportConfigurationAsync ---

    [Fact]
    public async Task ExportConfigurationAsync_ServerNotFound_ReturnsNull()
    {
        var result = await _sut.ExportConfigurationAsync(999, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExportConfigurationAsync_MinimalServer_ReturnsYaml()
    {
        var server = await SeedServerAsync();

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.Contains("name: test-server", yaml);
        Assert.Contains("type: Docker", yaml);
    }

    [Fact]
    public async Task ExportConfigurationAsync_WithTags_IncludesTags()
    {
        var server = await SeedServerAsync();

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.Contains("web", yaml);
        Assert.Contains("prod", yaml);
    }

    [Fact]
    public async Task ExportConfigurationAsync_WithDocker_IncludesDockerSection()
    {
        var server = await SeedServerAsync(s =>
        {
            s.DockerContainers.Add(new DockerContainer
            {
                Name = "nginx",
                Image = "nginx:latest",
                Ports = "80:80,443:443",
                State = "running",
                Status = "Up 2h"
            });
            s.DockerImages.Add(new DockerImage
            {
                Repository = "nginx",
                Tag = "latest",
                ImageId = "sha256:abc",
                Size = "142MB"
            });
        });

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.Contains("nginx:latest", yaml);
        Assert.Contains("80:80", yaml);
        Assert.Contains("443:443", yaml);
    }

    [Fact]
    public async Task ExportConfigurationAsync_WithServices_IncludesServicesSection()
    {
        var server = await SeedServerAsync(s =>
        {
            s.Services.Add(new ServiceInfo
            {
                Name = "nginx.service",
                Type = ServiceType.Systemd,
                IsRunning = true
            });
        });

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.Contains("nginx.service", yaml);
    }

    [Fact]
    public async Task ExportConfigurationAsync_WithComposeStacks_IncludesStacks()
    {
        var server = await SeedServerAsync(s =>
        {
            s.DockerComposeStacks.Add(new DockerComposeStack
            {
                Name = "monitoring",
                ConfigFile = "version: '3'\nservices:\n  grafana:\n    image: grafana/grafana",
                Status = "running(2)",
                RunningCount = 2,
                TotalCount = 2
            });
        });

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.Contains("monitoring", yaml);
    }

    [Fact]
    public async Task ExportConfigurationAsync_NoDocker_OmitsDockerSection()
    {
        var server = await SeedServerAsync();

        var yaml = await _sut.ExportConfigurationAsync(server.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(yaml);
        Assert.DoesNotContain("docker:", yaml);
    }

    // --- ValidateConfigurationAsync ---

    [Fact]
    public async Task ValidateConfigurationAsync_EmptyYaml_ReturnsInvalid()
    {
        var result = await _sut.ValidateConfigurationAsync("", ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_InvalidYamlSyntax_ReturnsInvalid()
    {
        var result = await _sut.ValidateConfigurationAsync("{{{{invalid yaml", ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("YAML syntax"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_MissingServerName_ReturnsInvalid()
    {
        const string yaml = """
            server:
              type: Docker
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_InvalidServerType_ReturnsInvalid()
    {
        const string yaml = """
            server:
              name: test
              type: InvalidType
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid server type"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_ValidMinimal_ReturnsValid()
    {
        const string yaml = """
            server:
              name: test-server
              type: Docker
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateConfigurationAsync_ContainerMissingImage_ReturnsInvalid()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              containers:
                - name: web
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_ContainerMissingName_ReturnsInvalid()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              containers:
                - image: nginx:latest
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Container name"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_ComposeStackMissingContent_ReturnsInvalid()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              compose_stacks:
                - name: monitoring
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing content"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_FullValid_ReturnsValid()
    {
        const string yaml = """
            server:
              name: prod-web
              type: Docker
              tags:
                - web
                - prod
            docker:
              containers:
                - name: web
                  image: nginx:latest
                  ports:
                    - "80:80"
              compose_stacks:
                - name: monitoring
                  content: "version: '3'"
              images:
                - nginx:latest
            services:
              systemd:
                - name: nginx.service
                  enabled: true
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // --- PreviewImportAsync ---

    [Fact]
    public async Task PreviewImportAsync_InvalidYaml_ReturnsNull()
    {
        await SeedServerAsync();

        var result = await _sut.PreviewImportAsync(1, "", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PreviewImportAsync_ServerNotFound_ReturnsNull()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            """;

        var result = await _sut.PreviewImportAsync(999, yaml, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task PreviewImportAsync_NewContainer_ShowsCreateAction()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              containers:
                - name: redis
                  image: redis:7
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "create" && c.Description.Contains("redis"));
        Assert.True(result.TaskCount > 0);
    }

    [Fact]
    public async Task PreviewImportAsync_ExistingContainer_ShowsUnchanged()
    {
        var server = await SeedServerAsync(s =>
        {
            s.DockerContainers.Add(new DockerContainer
            {
                Name = "nginx",
                Image = "nginx:latest",
                State = "running",
                Status = "Up"
            });
        });
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              containers:
                - name: nginx
                  image: nginx:latest
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "unchanged" && c.Description == "nginx");
    }

    [Fact]
    public async Task PreviewImportAsync_NameChange_ShowsUpdate()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: renamed-server
              type: Docker
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "update" && c.Description.Contains("renamed-server"));
    }

    [Fact]
    public async Task PreviewImportAsync_NewImage_ShowsPull()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              images:
                - redis:7
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "pull" && c.Description == "redis:7");
    }

    [Fact]
    public async Task PreviewImportAsync_NewComposeStack_ShowsDeploy()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              compose_stacks:
                - name: monitoring
                  content: "version: '3'"
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "deploy" && c.Description == "monitoring");
    }

    [Fact]
    public async Task PreviewImportAsync_ExistingComposeStack_ShowsUpdate()
    {
        var server = await SeedServerAsync(s =>
        {
            s.DockerComposeStacks.Add(new DockerComposeStack
            {
                Name = "monitoring",
                ConfigFile = "old content",
                Status = "running(1)"
            });
        });
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              compose_stacks:
                - name: monitoring
                  content: "version: '3'"
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "update" && c.Description == "monitoring");
    }

    [Fact]
    public async Task PreviewImportAsync_NewSystemdService_ShowsEnable()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            services:
              systemd:
                - name: nginx.service
                  enabled: true
            """;

        var result = await _sut.PreviewImportAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Changes, c => c.Action == "enable" && c.Description == "nginx.service");
    }

    // --- DeployConfigurationAsync ---

    [Fact]
    public async Task DeployConfigurationAsync_InvalidYaml_ReturnsNull()
    {
        await SeedServerAsync();

        var result = await _sut.DeployConfigurationAsync(1, "", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeployConfigurationAsync_ServerNotFound_ReturnsNull()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            """;

        var result = await _sut.DeployConfigurationAsync(999, yaml, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeployConfigurationAsync_NewImage_CreatesPullTask()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              images:
                - redis:7
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result.TasksCreated);
        Assert.Contains(result.TaskNames, n => n.Contains("Pull image"));

        var task = await _db.Tasks.FirstAsync(t => t.ServerId == server.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("docker pull redis:7", task.Command);
        Assert.Equal(TaskExecutionStatus.Pending, task.Status);
    }

    [Fact]
    public async Task DeployConfigurationAsync_OfflineAgent_IsRefused_AndQueuesNothing()
    {
        // SITE-OFFLINE-001: the deploy queues through TaskQueuePersistence, so the task service's
        // offline refusal stops it before a task is persisted or the server metadata is saved.
        var server = await SeedServerAsync(s => s.Status = ServerStatus.Offline);
        var taskService = NSubstitute.Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
        taskService.EnsureServerOnlineAsync(server.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Aetheus.Back.Exceptions.ConflictException("offline")));
        var sut = new ServerConfigurationService(new ServerConfigurationRepository(_db), TimeProvider.System, taskService);
        const string yaml = """
            server:
              name: renamed-server
              type: Docker
            docker:
              images:
                - redis:7
            """;

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() =>
            sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken));

        _db.ChangeTracker.Clear();
        Assert.False(await _db.Tasks.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("test-server", (await _db.Servers.SingleAsync(TestContext.Current.CancellationToken)).Name);
        await taskService.DidNotReceiveWithAnyArgs().NotifyTaskQueuedAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeployConfigurationAsync_NewContainer_CreatesRunTask()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              containers:
                - name: redis
                  image: redis:7
                  ports:
                    - "6379:6379"
                  restart: always
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.TaskNames, n => n.Contains("Create container"));

        var task = await _db.Tasks.FirstAsync(t => t.Command.Contains("docker run"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("--name redis", task.Command);
        Assert.Contains("-p 6379:6379", task.Command);
        Assert.Contains("redis:7", task.Command);
    }

    [Fact]
    public async Task DeployConfigurationAsync_ExistingContainer_SkipsCreation()
    {
        var server = await SeedServerAsync(s =>
        {
            s.DockerContainers.Add(new DockerContainer
            {
                Name = "nginx",
                Image = "nginx:latest",
                State = "running",
                Status = "Up"
            });
        });
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              containers:
                - name: nginx
                  image: nginx:latest
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.TasksCreated);
    }

    [Fact]
    public async Task DeployConfigurationAsync_ComposeStack_CreatesTask()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              compose_stacks:
                - name: monitoring
                  content: "version: '3'"
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.TaskNames, n => n.Contains("Deploy compose"));

        var task = await _db.Tasks.FirstAsync(t => t.Command.Contains("docker compose"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("monitoring", task.Command);
    }

    [Fact]
    public async Task DeployConfigurationAsync_SystemdService_CreatesEnableTask()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            services:
              systemd:
                - name: nginx.service
                  enabled: true
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.TaskNames, n => n.Contains("Enable service"));

        // Phase 3 (option B): the enable step is now a typed ServiceEnable operation (no free-form
        // shell). The agent runs `sudo -n systemctl enable --now <unit>` against the argv-exact
        // fixed-unit allow-list - the unit name travels in Command (the operation target).
        var task = await _db.Tasks.FirstAsync(t => t.Operation == OperationKind.ServiceEnable, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExecutorType.Operation, task.Executor);
        Assert.Equal("nginx.service", task.Command);
        Assert.DoesNotContain("sudo", task.Command);
    }

    [Fact]
    public async Task DeployConfigurationAsync_UpdatesServerMetadata()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: new-name
              type: Build
              tags:
                - staging
            """;

        await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        var updated = await _db.Servers.FindAsync([server.Id], TestContext.Current.CancellationToken);
        Assert.Equal("new-name", updated!.Name);
        Assert.Equal(ServerType.Build, updated.Type);
        Assert.Contains("staging", updated.Tags);
    }

    [Fact]
    public async Task DeployConfigurationAsync_DisabledService_SkipsTask()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            services:
              systemd:
                - name: nginx.service
                  enabled: false
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.TasksCreated);
        Assert.DoesNotContain(result.TaskNames, n => n.Contains("nginx.service"));
    }

    [Fact]
    public async Task DeployConfigurationAsync_MultipleTasks_CreatesAll()
    {
        var server = await SeedServerAsync();
        const string yaml = """
            server:
              name: test-server
              type: Docker
            docker:
              images:
                - redis:7
              containers:
                - name: redis
                  image: redis:7
              compose_stacks:
                - name: monitoring
                  content: "version: '3'"
            services:
              systemd:
                - name: nginx.service
                  enabled: true
            """;

        var result = await _sut.DeployConfigurationAsync(server.Id, yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(4, result.TasksCreated);
        Assert.Equal(4, await _db.Tasks.CountAsync(t => t.ServerId == server.Id, cancellationToken: TestContext.Current.CancellationToken));
    }
}
