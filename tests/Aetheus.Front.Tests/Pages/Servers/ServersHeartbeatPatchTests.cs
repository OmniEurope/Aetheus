// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using ServersPage = Aetheus.Front.Components.Servers.Servers;
namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// S-TECH-T3M8: a server heartbeat patches the matching grid row in place (via the record's
/// <c>with</c>) instead of triggering a full LoadData reload - no server round-trip, the list
/// reference is preserved. Off-page heartbeats are ignored; an active status filter falls back to
/// the coalesced reload because the new Online status could change page membership.
/// </summary>
public class ServersHeartbeatPatchTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServersHeartbeatPatchTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static List<ServerDto> Servers(IRenderedComponent<ServersPage> cut) =>
        (List<ServerDto>)typeof(ServersPage).GetField("_servers", Priv)!.GetValue(cut.Instance)!;

    private async Task InvokeHeartbeatAsync(IRenderedComponent<ServersPage> cut, int serverId, ServerHeartbeatDto hb)
    {
        var method = typeof(ServersPage).GetMethod("OnHeartbeatAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [serverId, hb])!);
    }

    private IRenderedComponent<ServersPage> RenderWith(params ServerDto[] servers)
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [.. servers],
            TotalCount = servers.Length
        });
        var cut = Render<ServersPage>();
        // The grid's LoadData populates _servers from the stub on first render.
        cut.WaitForState(() => Servers(cut).Count == servers.Length);
        return cut;
    }

    [Fact]
    public async Task Heartbeat_VisibleRow_NoFilter_PatchesRowInPlace()
    {
        var cut = RenderWith(new ServerDto
        {
            Id = 1,
            Name = "web-01",
            Hostname = "10.0.0.1",
            Type = ServerType.Normal,
            Status = ServerStatus.Offline,
            AgentVersion = "1.0.0",
            DockerAvailable = false,
            InsecureTls = false
        });
        var listBefore = Servers(cut);

        await InvokeHeartbeatAsync(cut, 1, new ServerHeartbeatDto
        {
            AgentVersion = "2.0.0",
            DockerAvailable = true,
            InsecureTls = true,
            AgentInstalledAt = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)
        });

        var server = Servers(cut)[0];
        Assert.Equal(ServerStatus.Online, server.Status);
        Assert.Equal("2.0.0", server.AgentVersion);
        Assert.True(server.DockerAvailable);
        Assert.True(server.InsecureTls);
        Assert.True(server.LastHeartbeat > DateTime.Now.AddMinutes(-1));
        // In-place patch: the backing list is the same instance, never re-fetched.
        Assert.Same(listBefore, Servers(cut));
    }

    [Fact]
    public async Task Heartbeat_NullAgentVersion_KeepsExistingVersion()
    {
        var cut = RenderWith(new ServerDto
        {
            Id = 1,
            Name = "web-01",
            Status = ServerStatus.Offline,
            AgentVersion = "1.5.0"
        });

        await InvokeHeartbeatAsync(cut, 1, new ServerHeartbeatDto { AgentVersion = null });

        Assert.Equal("1.5.0", Servers(cut)[0].AgentVersion);
        Assert.Equal(ServerStatus.Online, Servers(cut)[0].Status);
    }

    [Fact]
    public async Task Heartbeat_OffPageServer_IsNoOp()
    {
        var cut = RenderWith(new ServerDto { Id = 1, Name = "web-01", Status = ServerStatus.Online });
        var listBefore = Servers(cut);

        await InvokeHeartbeatAsync(cut, 99, new ServerHeartbeatDto { AgentVersion = "9.9.9" });

        // Unknown server id → nothing on the current page changes.
        Assert.Single(Servers(cut));
        Assert.Equal(1, Servers(cut)[0].Id);
        Assert.Same(listBefore, Servers(cut));
    }

    [Fact]
    public void Heartbeat_WithStatusFilter_DoesNotPatchInPlace()
    {
        var cut = RenderWith(new ServerDto
        {
            Id = 1,
            Name = "web-01",
            Status = ServerStatus.Offline,
            AgentVersion = "1.0.0"
        });

        // Swap in a coalescer whose debounce never elapses, so the deferred reload never runs in
        // the test (a real grid reload off the dispatcher would otherwise throw). We only need to
        // prove the handler takes the deferred path and does NOT patch the row synchronously.
        var neverFiring = new TrailingReloadCoalescer(5000, (_, _) => new TaskCompletionSource<bool>().Task);
        typeof(ServersPage).GetField("_heartbeatCoalescer", Priv)!.SetValue(cut.Instance, neverFiring);

        // A column filter is active → membership may change, so the handler defers to the
        // coalesced reload instead of patching the row synchronously.
        typeof(ServersPage).GetField("_columnFilters", Priv)!.SetValue(cut.Instance,
            new List<Aetheus.Shared.Components.Shared.GridFilter>
            {
                new() { Field = "Status", Operator = Aetheus.Shared.Components.Shared.GridFilterOperator.In, Value = "Online" }
            });

        var method = typeof(ServersPage).GetMethod("OnHeartbeatAsync", Priv)!;
        var pending = (Task)method.Invoke(cut.Instance, [1, new ServerHeartbeatDto { AgentVersion = "2.0.0" }])!;

        // Deferred (waiting on the debounce), not completed synchronously…
        Assert.False(pending.IsCompleted);
        // …and the row is NOT patched in place (still Offline / old version).
        Assert.Equal(ServerStatus.Offline, Servers(cut)[0].Status);
        Assert.Equal("1.0.0", Servers(cut)[0].AgentVersion);
    }
}
