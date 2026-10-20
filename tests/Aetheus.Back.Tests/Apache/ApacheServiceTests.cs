// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ApacheServiceTests
{
    private readonly IApacheRepository _repoMock = Substitute.For<IApacheRepository>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly ApacheService _sut;

    public ApacheServiceTests()
    {
        _sut = new ApacheService(_repoMock, _auditMock, _taskService);
    }

    [Fact]
    public async Task GetStateAsync_StateIsNull_ReturnsNotInstalled()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns((ApacheState?)null);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
    }

    [Fact]
    public async Task GetStateAsync_StateExists_ReturnsFullData()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ApacheState { ServerId = 1, IsRunning = true, Version = "2.4", Pid = 1234, ConfigRoot = "/etc/apache2" });
        _repoMock.GetModulesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ApacheModule { Name = "mod_ssl", Type = "shared", IsEnabled = true }]);
        _repoMock.GetVirtualHostsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ApacheVirtualHost { ServerName = "example.com", Port = 80, DocumentRoot = "/var/www", ConfigFile = "example.conf", IsEnabled = true }]);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsRunning);
        Assert.Equal("2.4", result.Version);
        Assert.Equal(1234, result.Pid);
        Assert.Single(result.Modules);
        Assert.Single(result.VirtualHosts);
    }

    [Fact]
    public async Task GetModulesAsync_ReturnsMappedModules()
    {
        _repoMock.GetModulesAsync(1, Arg.Any<CancellationToken>())
            .Returns([
                new ApacheModule { Name = "mod_ssl", Type = "shared", IsEnabled = true },
                new ApacheModule { Name = "mod_rewrite", Type = "static", IsEnabled = false }
            ]);

        var result = await _sut.GetModulesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("mod_ssl", result[0].Name);
        Assert.True(result[0].IsEnabled);
        Assert.Equal("mod_rewrite", result[1].Name);
        Assert.False(result[1].IsEnabled);
    }

    [Fact]
    public async Task GetVirtualHostsAsync_ReturnsMappedVHosts()
    {
        _repoMock.GetVirtualHostsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ApacheVirtualHost
            {
                ServerName = "test.com", Port = 443, DocumentRoot = "/var/www/test",
                ConfigFile = "test.conf", IsEnabled = true
            }]);

        var result = await _sut.GetVirtualHostsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("test.com", result[0].ServerName);
        Assert.Equal(443, result[0].Port);
    }

    [Fact]
    public async Task ExecuteActionAsync_Start_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.Start };

        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);

        // Apache lifecycle and configuration actions use typed operations through controlled sudo
        // escalation (ADR-004).
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Executor == ExecutorType.Operation
            && t.Operation == OperationKind.ApacheStart && t.Status == TaskExecutionStatus.Pending),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("ApacheStart", "Apache", 1, "Start", Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Stop_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.Stop };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t => t.ServerId == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Restart_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.Restart };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_Reload_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.Reload };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_TestConfig_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.TestConfig };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableSite_WithValidName_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableSite, TargetName = "example.conf" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("ApacheEnableSite", "Apache", 1, "example.conf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_DisableSite_WithValidName_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.DisableSite, TargetName = "example.conf" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableSite_NullTargetName_ThrowsBadRequest()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableSite, TargetName = null };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_DisableSite_EmptyTargetName_ThrowsBadRequest()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.DisableSite, TargetName = "" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableSite_InvalidSiteName_ThrowsBadRequest()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableSite, TargetName = "../etc/passwd" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableModule_InvalidModuleName_ThrowsBadRequest()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableModule, TargetName = "../evil" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableModule_WithValidName_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableModule, TargetName = "ssl" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_DisableModule_WithValidName_CreatesTask()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.DisableModule, TargetName = "ssl" };
        await _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteActionAsync_EnableModule_NullTargetName_ThrowsBadRequest()
    {
        var request = new ApacheActionRequest { Action = ApacheAction.EnableModule, TargetName = null };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLogsAsync_ClampsThenCreatesTask()
    {
        var request = new ApacheLogRequest { LogType = "access", Lines = 50 };
        await _sut.GetLogsAsync(1, request, ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1 && t.Name.Contains("access")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetVHostConfigAsync_ValidSiteName_CreatesTask()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ApacheState { ConfigRoot = "/etc/apache2" });

        await _sut.GetVHostConfigAsync(1, "example.conf", ct: TestContext.Current.CancellationToken);

        // Typed read op (not the old dead shell): kind ApacheGetConfig, site in Command, config root in env.
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1
            && t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.ApacheGetConfig
            && t.Command == "example.conf"
            && t.EnvironmentVariables.Contains("/etc/apache2")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetVHostConfigAsync_NoState_UsesDefaultConfigRoot()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns((ApacheState?)null);

        await _sut.GetVHostConfigAsync(1, "example.conf", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetVHostConfigAsync_InvalidSiteName_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetVHostConfigAsync(1, "../../../etc/passwd", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveVHostConfigAsync_ValidRequest_CreatesTaskAndAudits()
    {
        _repoMock.GetStateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ApacheState { ConfigRoot = "/etc/apache2" });

        var request = new ApacheVHostSaveRequest { SiteName = "example.conf", Content = "<VirtualHost></VirtualHost>" };
        await _sut.SaveVHostConfigAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("ApacheSaveConfig", "Apache", 1, "example.conf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveVHostConfigAsync_InvalidSiteName_ThrowsBadRequest()
    {
        var request = new ApacheVHostSaveRequest { SiteName = "../../malicious", Content = "test" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SaveVHostConfigAsync(1, request, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHtaccessAsync_ValidDocRoot_CreatesTask()
    {
        await _sut.GetHtaccessAsync(1, "/var/www/html", ct: TestContext.Current.CancellationToken);
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.ServerId == 1
            && t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.ApacheGetHtaccess
            && t.Command == "/var/www/html"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHtaccessAsync_InvalidDocRoot_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetHtaccessAsync(1, "../../malicious", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveHtaccessAsync_ValidRequest_CreatesTaskAndAudits()
    {
        var request = new ApacheHtaccessSaveRequest { DocumentRoot = "/var/www/html", Content = "RewriteEngine On" };
        await _sut.SaveHtaccessAsync(1, request, ct: TestContext.Current.CancellationToken);

        // Typed write op: kind ApacheSaveHtaccess, doc root in Command, base64 content in env.
        var expectedB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("RewriteEngine On"));
        await _repoMock.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t =>
            t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
            && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.ApacheSaveHtaccess
            && t.Command == "/var/www/html"
            && t.EnvironmentVariables.Contains(expectedB64)), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("ApacheSaveHtaccess", "Apache", 1, "/var/www/html", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveHtaccessAsync_InvalidDocRoot_ThrowsBadRequest()
    {
        var request = new ApacheHtaccessSaveRequest { DocumentRoot = "../../hack", Content = "test" };
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SaveHtaccessAsync(1, request, ct: TestContext.Current.CancellationToken));
    }
}
