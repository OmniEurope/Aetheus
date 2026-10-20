// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerPortsentrySectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags StaticPriv = BindingFlags.NonPublic | BindingFlags.Static;

    public ServerPortsentrySectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The section also reads the protocols its Protocol header filter offers.
        _handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private void StubPortsentryApi(
        int serverId = 50,
        List<PortsentryWhitelistIpDto>? whitelist = null)
    {
        var items = whitelist ?? [];
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>
        {
            Items = items,
            TotalCount = items.Count
        });
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/action", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/setup", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/logs", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/unblock", true);
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/whitelist/add", new PortsentryWhitelistIpDto { Id = 99, IpAddress = "1.2.3.4" });
    }

    private IRenderedComponent<ServerPortsentrySection> RenderInstalled(
        PortsentryDataDto? ps = null,
        int serverId = 50)
    {
        StubPortsentryApi(serverId);
        var data = ps ?? new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Version = "2.0",
            Mode = "atcp",
            BlockedCount = 0
        };
        return Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, serverId)
            .Add(x => x.Ps, data));
    }

    private IRenderedComponent<ServerPortsentrySection> RenderNotInstalled(int serverId = 50)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        return Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, serverId)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = false }));
    }

    // ── Render ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_InstalledState_ShowsPortSentryHeading()
    {
        var cut = RenderInstalled();
        Assert.Contains("Restart", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_InstalledState_ShowsVersion()
    {
        var cut = RenderInstalled();
        Assert.Contains("2.0", cut.Markup);
    }

    [Fact]
    public void Renders_NotInstalled_ShowsSetupButton_WithoutTitleHeading()
    {
        var cut = RenderNotInstalled();
        Assert.Contains("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PortsentrySetup", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PortSentry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_BlockedCount_AppearsInMarkup()
    {
        var cut = RenderInstalled(new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Version = "2.0",
            BlockedCount = 7
        });
        Assert.Contains("7", cut.Markup);
    }

    [Fact]
    public async Task Renders_WithWhitelistEntry_LoadsWhitelist()
    {
        StubPortsentryApi(50, whitelist:
        [
            new PortsentryWhitelistIpDto { Id = 1, IpAddress = "192.168.1.100", Description = "Office" }
        ]);
        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0", BlockedCount = 0 };
        var cut = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));

        // Force whitelist load via reflection to avoid timing issues
        var method = typeof(ServerPortsentrySection).GetMethod("LoadWhitelistAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);

        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Single(whitelist);
        Assert.Equal("192.168.1.100", whitelist[0].IpAddress);
    }

    // ── ExecuteActionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteActionAsync_Start_CompletesAndClearsRunning()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [PortsentryAction.Start])!);

        var running = (bool)typeof(ServerPortsentrySection)
            .GetField("_actionRunning", Priv)!
            .GetValue(cut.Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task ExecuteActionAsync_Stop_CompletesAndClearsRunning()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [PortsentryAction.Stop])!);

        Assert.False((bool)typeof(ServerPortsentrySection)
            .GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task ExecuteActionAsync_FailedResponse_StillClearsRunning()
    {
        var cut = RenderInstalled();
        _handler.SetResponse(HttpMethod.Post, "api/servers/50/portsentry/action",
            System.Net.HttpStatusCode.InternalServerError);
        var method = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [PortsentryAction.Restart])!);

        Assert.False((bool)typeof(ServerPortsentrySection)
            .GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task ServerChange_ClearsWhitelistFromPreviousServer()
    {
        StubPortsentryApi(50,
        [
            new PortsentryWhitelistIpDto { Id = 1, IpAddress = "10.0.0.1" }
        ]);
        var cut = Render<ServerPortsentrySection>(parameters => parameters
            .Add(component => component.ServerId, 50)
            .Add(component => component.Ps, new PortsentryDataDto { IsInstalled = true }));
        var load = typeof(ServerPortsentrySection).GetMethod("LoadWhitelistAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)load.Invoke(
            cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);

        StubPortsentryApi(51);
        cut.Render(parameters => parameters
            .Add(component => component.ServerId, 51)
            .Add(component => component.Ps, new PortsentryDataDto { IsInstalled = true }));

        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(whitelist);
    }

    // ── GetLogsAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogsAsync_CompletesWithoutThrowing()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerPortsentrySection).GetMethod("GetLogsAsync", Priv)!;

        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.Instance, [])!));

        Assert.Null(ex);
    }

    // ── LoadWhitelistAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadWhitelistAsync_PopulatesWhitelist()
    {
        StubPortsentryApi(50, whitelist:
        [
            new PortsentryWhitelistIpDto { Id = 1, IpAddress = "10.0.0.1", Description = "LAN" },
            new PortsentryWhitelistIpDto { Id = 2, IpAddress = "10.0.0.2", Description = "VPN" }
        ]);
        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0" };
        var cut = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));

        var method = typeof(ServerPortsentrySection).GetMethod("LoadWhitelistAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);

        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, whitelist.Count);
    }

    // ── AddWhitelistIpAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task AddWhitelistIpAsync_EmptyIp_DoesNotAdd()
    {
        var cut = RenderInstalled();
        typeof(ServerPortsentrySection).GetField("_whitelistIp", Priv)!.SetValue(cut.Instance, "");

        var method = typeof(ServerPortsentrySection).GetMethod("AddWhitelistIpAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(whitelist);
    }

    [Fact]
    public async Task AddWhitelistIpAsync_ValidIp_CompletesWithoutThrowing()
    {
        // Use a not-installed state so OnParametersSetAsync skips LoadWhitelistAsync,
        // avoiding the GET/POST URL collision (both hit api/.../whitelist).
        // The POST fallback returns {} which deserializes as a default DTO (not null),
        // so the component adds it to the list gracefully.
        _handler.SetJsonResponse("api/servers/50/portsentry/action", true);
        _handler.SetJsonResponse("api/servers/50/portsentry/setup", true);
        _handler.SetJsonResponse("api/servers/50/portsentry/logs", true);
        _handler.SetJsonResponse(
            HttpMethod.Post,
            "api/servers/50/portsentry/whitelist",
            new PortsentryWhitelistIpDto { Id = 99, IpAddress = "1.2.3.4", Description = "Test" });

        var ps = new PortsentryDataDto { IsInstalled = false };
        var cut = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));

        typeof(ServerPortsentrySection).GetField("_whitelistIp", Priv)!.SetValue(cut.Instance, "1.2.3.4");
        typeof(ServerPortsentrySection).GetField("_whitelistDesc", Priv)!.SetValue(cut.Instance, "Test");

        var method = typeof(ServerPortsentrySection).GetMethod("AddWhitelistIpAsync", Priv)!;
        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.Instance, [])!));

        Assert.Null(ex);
    }

    // ── RemoveWhitelistIpAsync ────────────────────────────────────────────────

    [Fact]
    public async Task RemoveWhitelistIpAsync_ExistingEntry_RemovesFromList()
    {
        var entry = new PortsentryWhitelistIpDto { Id = 5, IpAddress = "9.9.9.9", Description = "DNS" };
        StubPortsentryApi(50);
        // Use numeric id in the URL pattern to match DELETE .../whitelist/5
        _handler.SetJsonResponse("api/servers/50/portsentry/whitelist/5", true);

        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0" };
        var cut = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));

        // Manually populate the whitelist field so the test doesn't rely on OnParametersSetAsync timing
        // Override the whitelist with our entry directly
        var whitelistField = typeof(ServerPortsentrySection).GetField("_whitelist", Priv)!;
        whitelistField.SetValue(cut.Instance, new List<PortsentryWhitelistIpDto> { entry });

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;

        var method = typeof(ServerPortsentrySection).GetMethod("RemoveWhitelistIpAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [entry])!);

        var whitelist = (List<PortsentryWhitelistIpDto>)whitelistField.GetValue(cut.Instance)!;
        Assert.Empty(whitelist);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, OmniTone.Success)]
    [InlineData(3, OmniTone.Warning)]
    [InlineData(5, OmniTone.Warning)]
    [InlineData(10, OmniTone.Danger)]
    public void GetBlockedCountBadgeStyle_ReturnsExpected(int count, OmniTone expected)
    {
        var method = typeof(ServerPortsentrySection).GetMethod("GetBlockedCountBadgeStyle", StaticPriv)!;
        var result = (OmniTone)method.Invoke(null, [count])!;
        Assert.Equal(expected, result);
    }

    // ── SetupAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupAsync_PostsSetupRequest()
    {
        var cut = RenderInstalled();
        var method = typeof(ServerPortsentrySection).GetMethod("SetupAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("portsentry/setup"));
    }

    // ── UnblockIpAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnblockIpAsync_Confirmed_PostsUnblockRequest()
    {
        var cut = RenderInstalled();
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        var method = typeof(ServerPortsentrySection).GetMethod("UnblockIpAsync", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["203.0.113.8"])!);

        Assert.Equal("Unblock", dialog.LastTitle);
        Assert.Equal("UnblockConfirm", dialog.LastConfirmMessage);
        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.Contains("portsentry/unblock"));
    }
}
