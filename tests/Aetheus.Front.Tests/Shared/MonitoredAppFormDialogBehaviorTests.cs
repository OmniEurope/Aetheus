// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

public sealed class MonitoredAppFormDialogBehaviorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public MonitoredAppFormDialogBehaviorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    [Fact]
    public async Task Create_LoadsLookups_PostsApp_AndClosesWithSuccess()
    {
        ArrangeLookups();
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/projects/7/apps",
            new MonitoredAppDto { Id = 11, ProjectId = 7, Name = "Public API" });
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7));
        var model = Model(cut.Instance);
        model.Name = "Public API";
        model.ProbeUrl = "https://example.test/health";

        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/servers", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/environments", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/appmonitoring/projects/7/apps", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
        // R-10: the dialog hands the saved application to the list, which draws that row at once.
        var saved = Assert.IsType<MonitoredAppDto>(Spy.LastResult);
        Assert.Equal(11, saved.Id);
    }

    [Fact]
    public void InputEvents_AreSentByTheCreateForm()
    {
        ArrangeLookups();
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/projects/7/apps",
            new MonitoredAppDto { Id = 11, ProjectId = 7, Name = "PortfolioTest public" });
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7));

        cut.Find("input#Name").Input("PortfolioTest public");
        cut.Find("input#ProbeUrl").Input("https://sonytumen.com/");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST"
                       && request.Url.EndsWith("api/appmonitoring/projects/7/apps", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST"
            && request.Url.EndsWith("api/appmonitoring/projects/7/apps", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateMonitoredAppRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("PortfolioTest public", request!.Name);
        Assert.Equal("https://sonytumen.com/", request.ProbeUrl);
        Assert.Equal(7, request.ProjectId);
    }

    [Fact]
    public async Task Edit_CopiesProbeConfiguration_AndUsesAppEndpoint()
    {
        ArrangeLookups();
        var app = new MonitoredAppDto
        {
            Id = 12,
            ProjectId = 7,
            Name = "Frontend",
            ProbeUrl = "https://front.test/health",
            ProbeIntervalSeconds = 45,
            ProbeTimeoutSeconds = 7,
            ExpectedStatusCode = 204,
            FailureThreshold = 4,
            RecoveryThreshold = 3,
            Enabled = true
        };
        _handler.SetJsonResponse(HttpMethod.Put, "api/appmonitoring/apps/12", app);
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7).Add(x => x.App, app));

        Assert.Equal("Frontend", Model(cut.Instance).Name);
        Assert.Equal(45, Model(cut.Instance).ProbeIntervalSeconds);
        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.EndsWith("api/appmonitoring/apps/12", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
    }

    [Fact]
    public async Task EmptyName_ShowsValidationWarningWithoutCallingMutationEndpoint()
    {
        ArrangeLookups();
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7));

        await InvokeSubmitAsync(cut);

        Assert.DoesNotContain(_handler.Requests, r => r.Method is "POST" or "PUT");
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Warning);
    }

    [Fact]
    public void LookupFailure_IsBestEffortAndFormStillRenders()
    {
        _handler.SetResponse(HttpMethod.Get, "api/servers", HttpStatusCode.ServiceUnavailable);
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7));

        Assert.Contains("MonitoredAppName", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Create", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Lookups_LoadEveryServerAndEnvironmentPage()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers?page=1&pageSize=100", new PaginatedResult<ServerDto>
        {
            Items = Enumerable.Range(1, 100).Select(id => new ServerDto { Id = id, Name = $"server-{id}" }).ToList(),
            TotalCount = 201,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers?page=2&pageSize=100", new PaginatedResult<ServerDto>
        {
            Items = Enumerable.Range(101, 100).Select(id => new ServerDto { Id = id, Name = $"server-{id}" }).ToList(),
            TotalCount = 201,
            Page = 2,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers?page=3&pageSize=100", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 201, Name = "server-201" }],
            TotalCount = 201,
            Page = 3,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/environments?page=1&pageSize=100&projectId=7", new PaginatedResult<EnvironmentDto>
        {
            Items = Enumerable.Range(1, 100).Select(id => new EnvironmentDto { Id = id, Name = $"env-{id}" }).ToList(),
            TotalCount = 201,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/environments?page=2&pageSize=100&projectId=7", new PaginatedResult<EnvironmentDto>
        {
            Items = Enumerable.Range(101, 100).Select(id => new EnvironmentDto { Id = id, Name = $"env-{id}" }).ToList(),
            TotalCount = 201,
            Page = 2,
            PageSize = 100
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/environments?page=3&pageSize=100&projectId=7", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 201, Name = "env-201" }],
            TotalCount = 201,
            Page = 3,
            PageSize = 100
        });

        var cut = Render<MonitoredAppFormDialog>(parameters => parameters.Add(component => component.ProjectId, 7));

        var servers = (List<ServerDto>)typeof(MonitoredAppFormDialog)
            .GetField("_servers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var environments = (List<EnvironmentDto>)typeof(MonitoredAppFormDialog)
            .GetField("_environments", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(201, servers.Count);
        Assert.Equal("server-201", servers.Single(server => server.Id == 201).Name);
        Assert.Equal(201, environments.Count);
        Assert.Equal("env-201", environments.Single(environment => environment.Id == 201).Name);
        Assert.Equal(3, _handler.Requests.Count(request =>
            request.Method == HttpMethod.Get.Method && request.Url.Contains("api/servers?page=", StringComparison.Ordinal)));
        Assert.Equal(3, _handler.Requests.Count(request =>
            request.Method == HttpMethod.Get.Method && request.Url.Contains("api/environments?page=", StringComparison.Ordinal)));
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("api/servers?page=3&pageSize=100", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith(
            "api/environments?page=3&pageSize=100&projectId=7", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancel_ClosesWithFalse()
    {
        ArrangeLookups();
        var cut = Render<MonitoredAppFormDialog>(p => p.Add(x => x.ProjectId, 7));

        cut.InvokeAsync(() => typeof(MonitoredAppFormDialog).GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, []));

        Assert.True(Spy.Closed);
        Assert.Equal(false, Spy.LastResult);
    }

    private void ArrangeLookups()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 2, Name = "prod" }],
            Page = 1,
            PageSize = 200,
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 3, Name = "Production" }],
            Page = 1,
            PageSize = 200,
            TotalCount = 1
        });
    }

    private SpyDialogService Spy => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
    private static UpdateMonitoredAppRequest Model(MonitoredAppFormDialog instance) => (UpdateMonitoredAppRequest)
        typeof(MonitoredAppFormDialog).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static Task InvokeSubmitAsync(IRenderedComponent<MonitoredAppFormDialog> cut) => cut.InvokeAsync(() =>
        (Task)typeof(MonitoredAppFormDialog).GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
}
