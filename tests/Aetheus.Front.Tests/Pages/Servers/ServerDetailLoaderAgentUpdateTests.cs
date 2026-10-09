// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R2-111: an agent-update event reloads the loaded server's update request, agent version and
/// compatibility, so the Overview's update state ends on the new version or on the failure.
/// </summary>
public sealed class ServerDetailLoaderAgentUpdateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDetailLoaderAgentUpdateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/monitoring/servers/7/metrics", new List<ServerMetricDto>());
    }

    private static ServerDetailDto Detail(string version, AgentUpdateRequestSummaryDto? request) => new()
    {
        Id = 7,
        Name = "web-prod",
        Hostname = "10.0.0.7",
        IpAddress = "10.0.0.7",
        Status = ServerStatus.Online,
        AgentVersion = version,
        AgentUpdateRequest = request,
        CpuPercent = 42
    };

    private ServerDetailLoader NewLoader() => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<NavigationManager>(),
        NullLogger<ServerDetailLoader>.Instance);

    [Fact]
    public async Task AReload_TakesTheUpdateRequestAndVersion_AndKeepsTheLiveFields()
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Queued
        }));
        var loader = NewLoader();
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        var changes = 0;
        loader.OnChanged += () => changes++;

        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", new AgentUpdateRequestSummaryDto
        {
            RequestId = 4,
            TargetVersion = "1.0.1552",
            Status = AgentUpdateRequestStatus.Failed,
            FailureDiagnostic = "download refused"
        }) with
        { CpuPercent = 3 });
        await loader.ReloadAgentUpdateAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(1, changes);
        Assert.Equal(AgentUpdateRequestStatus.Failed, loader.Server!.AgentUpdateRequest!.Status);
        Assert.Equal("download refused", loader.Server.AgentUpdateRequest.FailureDiagnostic);
        // The heartbeat-driven fields are the live ones and stay as they were.
        Assert.Equal(42, loader.Server.CpuPercent);
    }

    [Fact]
    public async Task AReloadForAnotherServer_ChangesNothing()
    {
        _handler.SetJsonResponse("api/servers/7", Detail("1.0.1547", null));
        var loader = NewLoader();
        await loader.EnsureLoadedAsync(7, Xunit.TestContext.Current.CancellationToken);
        var changes = 0;
        loader.OnChanged += () => changes++;

        _handler.SetJsonResponse("api/servers/8", Detail("1.0.1552", null) with { Id = 8 });
        await loader.ReloadAgentUpdateAsync(8, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, changes);
        Assert.Equal("1.0.1547", loader.Server!.AgentVersion);
    }
}
