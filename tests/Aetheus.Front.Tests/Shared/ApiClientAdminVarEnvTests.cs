// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Tests;

/// <summary>Covers ApiClient.Admin, .Variables, .Environments, .ModuleLinks, .Services, .SystemLogs partials.</summary>
public class ApiClientAdminVarEnvTests
{
    private readonly BunitTestHelper.TestHandler _handler = new();
    private readonly ApiClient _api;

    public ApiClientAdminVarEnvTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://test/") };
        _api = new ApiClient(http);
    }

    // --- Admin ---
    [Fact]
    public async Task GetUsersAsync_Returns()
    { _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto> { Items = [new UserDto { Id = 7, Username = "alice" }], TotalCount = 1 }); var r = await _api.Auth.GetUsersAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Single(r.Items); Assert.Equal("alice", r.Items[0].Username); AssertRequest(HttpMethod.Get, "api/users?page=1&pageSize=25"); }

    [Fact]
    public async Task GetCurrentUserAsync_Returns()
    { _handler.SetJsonResponse("api/users/me", new UserDto { Id = 7, Username = "me" }); var r = await _api.Auth.GetCurrentUserAsync(Xunit.TestContext.Current.CancellationToken); Assert.Equal("me", r!.Username); AssertRequest(HttpMethod.Get, "api/users/me"); }

    [Fact]
    public async Task GetRolesAsync_Returns()
    { _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "Operator" }); var r = await _api.Auth.GetRolesAsync(); Assert.Equal(2, r.Count); Assert.Contains("Admin", r); AssertRequest(HttpMethod.Get, "api/users/roles"); }

    [Fact]
    public async Task GetDashboardsAsync_Returns()
    { _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>()); var r = await _api.Monitoring.GetDashboardsAsync(); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/dashboards"); }

    [Fact]
    public async Task GetPluginsAsync_Returns()
    { _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>()); var r = await _api.Settings.GetPluginsAsync(Xunit.TestContext.Current.CancellationToken); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/plugins?page=1&pageSize=100&sortDescending=false"); }

    [Fact]
    public async Task GetAlertRulesAsync_Returns()
    { _handler.SetJsonResponse("api/alerts", new List<AlertRuleDto> { new() { Id = 5, Name = "cpu-high" } }); var r = await _api.Monitoring.GetAlertRulesAsync(); Assert.Single(r); Assert.Equal("cpu-high", r[0].Name); AssertRequest(HttpMethod.Get, "api/alerts"); }

    [Fact]
    public async Task GetNotificationChannelsAsync_Returns()
    { _handler.SetJsonResponse("api/notifications/channels", new PaginatedResult<NotificationChannelDto> { Items = [new() { Id = 8, Name = "ops-email" }], TotalCount = 1 }); var r = await _api.Monitoring.GetNotificationChannelsAsync(); Assert.Single(r); Assert.Equal("ops-email", r[0].Name); AssertRequest(HttpMethod.Get, "api/notifications/channels?page=1&pageSize=100"); }

    [Fact]
    public async Task DeleteUserAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/users/1", HttpStatusCode.OK); var r = await _api.Auth.DeleteUserAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/users/1"); }

    [Fact]
    public async Task DeleteDashboardAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/dashboards/1", HttpStatusCode.OK); var r = await _api.Monitoring.DeleteDashboardAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/dashboards/1"); }

    [Fact]
    public async Task DeleteAlertRuleAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/alerts/1", HttpStatusCode.OK); var r = await _api.Monitoring.DeleteAlertRuleAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/alerts/1"); }

    [Fact]
    public async Task DeleteNotificationChannelAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/notifications/channels/1", HttpStatusCode.OK); var r = await _api.Monitoring.DeleteNotificationChannelAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/notifications/channels/1"); }

    // --- Variables / Vaults ---
    [Fact]
    public async Task GetVariableLibrariesAsync_Returns()
    { _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [new VariableLibraryDto { Id = 3, Name = "Lib" }], TotalCount = 1 }); var r = await _api.Variables.GetVariableLibrariesAsync(); Assert.Single(r.Items); Assert.Equal("Lib", r.Items[0].Name); AssertRequest(HttpMethod.Get, "api/variable-libraries?page=1&pageSize=25"); }

    [Fact]
    public async Task GetVariableLibraryNamesAsync_Returns()
    { _handler.SetJsonResponse("api/variable-libraries/names", new List<string>()); var r = await _api.Variables.GetVariableLibraryNamesAsync(); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/variable-libraries/names"); }

    [Fact]
    public async Task DeleteVariableLibraryAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/variable-libraries/1", HttpStatusCode.OK); var r = await _api.Variables.DeleteVariableLibraryAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/variable-libraries/1"); }

    [Fact]
    public async Task GetVaultsAsync_Returns()
    { _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto> { Items = [new VaultDto { Id = 4, Name = "Vault1" }], TotalCount = 1 }); var r = await _api.Variables.GetVaultsAsync(); Assert.Single(r.Items); Assert.Equal("Vault1", r.Items[0].Name); AssertRequest(HttpMethod.Get, "api/vaults?page=1&pageSize=25"); }

    [Fact]
    public async Task GetVaultNamesAsync_Returns()
    { _handler.SetJsonResponse("api/vaults/names", new List<string>()); var r = await _api.Variables.GetVaultNamesAsync(); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/vaults/names"); }

    [Fact]
    public async Task DeleteVaultAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/vaults/1", HttpStatusCode.OK); var r = await _api.Variables.DeleteVaultAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/vaults/1"); }

    [Fact]
    public async Task DeleteVaultSecretAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/vaults/1/secrets/2", HttpStatusCode.OK); var r = await _api.Variables.DeleteVaultSecretAsync(1, 2, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/vaults/1/secrets/2"); }

    [Fact]
    public async Task ExportVaultSecretKeysAsync_Returns()
    { _handler.SetJsonResponse("api/vaults/1/export-keys", new List<string>()); var r = await _api.Variables.ExportVaultSecretKeysAsync(1); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/vaults/1/export-keys"); }

    // --- Environments ---
    [Fact]
    public async Task GetEnvironmentsAsync_Returns()
    { _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [new EnvironmentDto { Id = 2, Name = "Staging" }], TotalCount = 1 }); var r = await _api.Servers.GetEnvironmentsAsync(); Assert.Single(r.Items); Assert.Equal("Staging", r.Items[0].Name); AssertRequest(HttpMethod.Get, "api/environments?page=1&pageSize=25"); }

    [Fact]
    public async Task GetEnvironmentAsync_Returns()
    { _handler.SetJsonResponse("api/environments/1", new EnvironmentDto { Id = 1, Name = "Prod" }); var r = await _api.Servers.GetEnvironmentAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.Equal("Prod", r!.Name); AssertRequest(HttpMethod.Get, "api/environments/1"); }

    [Fact]
    public async Task DeleteEnvironmentAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/environments/1", HttpStatusCode.OK); var r = await _api.Servers.DeleteEnvironmentAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/environments/1"); }

    // --- ModuleLinks ---
    [Fact]
    public async Task GetModuleLinksAsync_Returns()
    { _handler.SetJsonResponse("api/servers/1/module-links", new List<ModuleLinkDto>()); var r = await _api.Servers.GetModuleLinksAsync(1); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/servers/1/module-links"); }

    [Fact]
    public async Task DeleteModuleLinkAsync_Returns()
    { _handler.SetResponse(HttpMethod.Delete, "api/servers/1/module-links/2", HttpStatusCode.OK); var r = await _api.Servers.DeleteModuleLinkAsync(1, 2, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Delete, "api/servers/1/module-links/2"); }

    [Fact]
    public async Task AutoDetectModuleLinksAsync_Returns()
    { _handler.SetResponse(HttpMethod.Post, "api/servers/1/module-links/auto-detect", HttpStatusCode.OK); var r = await _api.Servers.AutoDetectModuleLinksAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); AssertRequest(HttpMethod.Post, "api/servers/1/module-links/auto-detect"); }

    // --- Services ---
    [Fact]
    public async Task ExecuteServiceActionAsync_Returns()
    { _handler.SetJsonResponse(HttpMethod.Post, "api/servers/1/services/action", new { taskId = 7 }); var r = await _api.Auth.ExecuteServiceActionAsync(1, new ServiceActionRequest(), Xunit.TestContext.Current.CancellationToken); Assert.Equal(7, r); AssertRequest(HttpMethod.Post, "api/servers/1/services/action"); }

    [Fact]
    public async Task GetAgentServerUrlAsync_Returns()
    { _handler.SetJsonResponse("api/servers/agent-server-url", new { url = "https://agent.example.com" }); var r = await _api.Security.GetAgentServerUrlAsync(Xunit.TestContext.Current.CancellationToken); Assert.Equal("https://agent.example.com", r); AssertRequest(HttpMethod.Get, "api/servers/agent-server-url"); }

    // --- SystemLogs ---
    [Fact]
    public async Task GetSystemLogFilesAsync_Returns()
    { _handler.SetJsonResponse("api/system-logs/files", new List<SystemLogFileDto>()); var r = await _api.Security.GetSystemLogFilesAsync(); Assert.Empty(r); AssertRequest(HttpMethod.Get, "api/system-logs/files"); }

    private void AssertRequest(HttpMethod method, string relativeUrl)
    {
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(method.Method, request.Method);
        Assert.Equal(new Uri(new Uri("http://test/"), relativeUrl).AbsoluteUri, request.Url);
    }
}
