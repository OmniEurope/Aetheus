// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerAppsSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerAppsSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyList()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        // After the fetch resolves, the empty-grid text renders; the title heading is gone (page header shows it).
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(">Applications<", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("NoRecords", cut.Markup);
        });
    }

    [Fact]
    public void Renders_WithApps()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>
        {
            new() { Id = 1, Name = "nginx", Version = "1.25", Port = 80, Source = "docker" },
            new() { Id = 2, Name = "api", Version = "3.0", Port = 5000, Source = "systemd" }
        });
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        // After the fetch resolves, the seeded app names render in the grid.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("nginx", cut.Markup);
            Assert.Contains("api", cut.Markup);
        });
    }

    [Fact]
    public void ServerIdChange_ReloadsAppsOnSameInstance()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new[]
        {
            new ServerAppDto { Id = 1, Name = "first-app" }
        });
        _handler.SetPaginatedJsonResponse("api/servers/2/apps", new[]
        {
            new ServerAppDto { Id = 2, Name = "second-app" }
        });
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));

        cut.Render(p => p.Add(x => x.ServerId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-app", cut.Markup));
        Assert.DoesNotContain("first-app", cut.Markup);
    }

    [Theory]
    [InlineData(ServerAppStatus.Running, OmniTone.Success)]
    [InlineData(ServerAppStatus.Stopped, OmniTone.Neutral)]
    [InlineData(ServerAppStatus.Error, OmniTone.Danger)]
    public void GetAppStatusBadge_ReturnsExpected(ServerAppStatus status, OmniTone expected)
    {
        var method = typeof(ServerAppsSection).GetMethod("GetAppStatusBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AddAppAsync_PostsCreateRequest()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        // Method-aware POST stub returns the created app so AddAppAsync's success path runs.
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/1/apps",
            new ServerAppDto { Id = 7, Name = "myapp" });
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance) != null);

        typeof(ServerAppsSection).GetField("_addName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "myapp");
        typeof(ServerAppsSection).GetField("_addVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "1.0");
        typeof(ServerAppsSection).GetField("_addPort", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, (int?)8080);

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/1/apps"));
        Assert.True(_handler.Requests.Count(r =>
            r.Method == "GET" && r.Url.Contains("api/servers/1/apps")) >= 2);
    }

    [Fact]
    public void Renders_ManyApps()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>
        {
            new() { Id = 1, Name = "nginx", Version = "1.25", Port = 80, Source = "docker", Status = ServerAppStatus.Running },
            new() { Id = 2, Name = "api", Version = "3.0", Port = 5000, Source = "systemd", Status = ServerAppStatus.Running },
            new() { Id = 3, Name = "worker", Version = "1.0", Port = null, Source = "manual", Status = ServerAppStatus.Stopped },
            new() { Id = 4, Name = "broken", Version = "0.1", Port = 9999, Source = "docker", Status = ServerAppStatus.Error }
        });
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        // After the fetch resolves, every seeded app name renders in the grid.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("nginx", cut.Markup);
            Assert.Contains("worker", cut.Markup);
            Assert.Contains("broken", cut.Markup);
        });
    }
}
