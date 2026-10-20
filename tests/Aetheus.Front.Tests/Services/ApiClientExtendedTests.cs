// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// Targets uncovered ApiClient methods:
/// - GetServerDiagnosticAsync (catch path + success path)
/// - GetServerHeartbeatsAsync (catch path + success path)
/// - UpdateAgentAsync / UpdateAllAgentsAsync
/// - GetProjectServersAsync / CreateProjectServerAsync / UpdateProjectServerAsync / DeleteProjectServerAsync
/// - GetTaskLogsUnmaskedAsync
/// </summary>
public class ApiClientExtendedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ApiClient _api;

    public ApiClientExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _api = Services.GetRequiredService<ApiClient>();
    }

    // ── GetServerDiagnosticAsync - success ────────────────────────────────────

    [Fact]
    public async Task GetServerDiagnosticAsync_Success_ReturnsDto()
    {
        _handler.SetJsonResponse("api/servers/1/diagnostic", new ServerDiagnosticDto
        {
            AgentVersion = "2.0.0",
            Summary = "Agent offline for 5 min",
            TokenValid = true
        });

        var result = await _api.Servers.GetServerDiagnosticAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("2.0.0", result.AgentVersion);
    }

    // ── GetServerDiagnosticAsync - HTTP error returns null ────────────────────

    [Fact]
    public async Task GetServerDiagnosticAsync_HttpError_ReturnsNull()
    {
        _handler.SetResponse("api/servers/1/diagnostic", System.Net.HttpStatusCode.InternalServerError);

        var result = await _api.Servers.GetServerDiagnosticAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Null(result);
    }


    // ── UpdateAgentAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgentAsync_Success()
    {
        _handler.SetJsonResponse("api/servers/1/agent/update", new AgentUpdateResponse { TaskId = 42 });
        var result = await _api.Servers.UpdateAgentAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(42, result.TaskId);
    }

    // ── UpdateAllAgentsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAllAgentsAsync_Success()
    {
        _handler.SetJsonResponse("api/servers/agent/update-all", new AgentUpdateAllResponse { QueuedCount = 5 });
        var result = await _api.Servers.UpdateAllAgentsAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(5, result.QueuedCount);
    }

    // ── GetProjectServersAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectServersAsync_ReturnsItems()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 1, ServerId = 10, ProjectId = 1 }
        });

        var result = await _api.Projects.GetProjectServersAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
    }

    // ── CreateProjectServerAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CreateProjectServerAsync_Success()
    {
        _handler.SetJsonResponse("api/projects/1/servers", new ProjectServerDto { Id = 5, ProjectId = 1, ServerId = 10 });

        var result = await _api.Projects.CreateProjectServerAsync(1, new CreateProjectServerRequest
        {
            ServerId = 10,
            DisplayName = "web",
            Host = "web01.example.com",
            Type = Aetheus.Shared.Components.Projects.ProjectServerType.AgentServer
        }, Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        var request = Assert.Single(_handler.Requests, r => r.Method == "POST");
        Assert.EndsWith("api/projects/1/servers", request.Url);
        Assert.Contains("\"serverId\":10", _handler.LastRequestBody);
        Assert.Contains("\"displayName\":\"web\"", _handler.LastRequestBody);
    }

    // ── UpdateProjectServerAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateProjectServerAsync_Success()
    {
        _handler.SetJsonResponse("api/projects/1/servers/5", new ProjectServerDto { Id = 5, ProjectId = 1, ServerId = 10 });

        var result = await _api.Projects.UpdateProjectServerAsync(1, 5, new UpdateProjectServerRequest
        {
            DisplayName = "web-updated",
            Host = "web01-new.example.com"
        }, Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        var request = Assert.Single(_handler.Requests, r => r.Method == "PUT");
        Assert.EndsWith("api/projects/1/servers/5", request.Url);
        Assert.Contains("\"displayName\":\"web-updated\"", _handler.LastRequestBody);
        Assert.Contains("\"host\":\"web01-new.example.com\"", _handler.LastRequestBody);
    }

    // ── DeleteProjectServerAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteProjectServerAsync_Success()
    {
        _handler.SetJsonResponse("api/projects/1/servers/5", new { });

        var result = await _api.Projects.DeleteProjectServerAsync(1, 5, Xunit.TestContext.Current.CancellationToken);
        Assert.True(result.Success);
    }

    // ── GetTaskLogsUnmaskedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetTaskLogsUnmaskedAsync_ReturnsLogs()
    {
        _handler.SetJsonResponse("api/logs/task/7/unmasked", new List<TaskLogDto>
        {
            new() { Id = 1, Message = "secret=abc123", Level = Aetheus.Shared.Components.Logs.TaskLogLevel.Info }
        });

        var result = await _api.Monitoring.GetTaskLogsUnmaskedAsync(7, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Contains("abc123", result[0].Message);
    }

    [Fact]
    public async Task GetTaskLogsUnmaskedAsync_WithCt_ReturnsLogs()
    {
        _handler.SetJsonResponse("api/logs/task/8/unmasked", new List<TaskLogDto>
        {
            new() { Id = 2, Message = "log line", Level = Aetheus.Shared.Components.Logs.TaskLogLevel.Info }
        });

        var result = await _api.Monitoring.GetTaskLogsUnmaskedAsync(8, CancellationToken.None);
        Assert.Single(result);
    }
}

