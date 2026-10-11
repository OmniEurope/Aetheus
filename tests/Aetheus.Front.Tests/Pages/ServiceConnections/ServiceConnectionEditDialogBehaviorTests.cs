// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.ServiceConnections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.ServiceConnections;

public sealed class ServiceConnectionEditDialogBehaviorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServiceConnectionEditDialogBehaviorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    [Fact]
    public async Task Create_PostsConnectionAndNormalizesBlankConfiguration()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/service-connections",
            new ServiceConnectionDto { Id = 5, Name = "GitHub", Type = ServiceConnectionType.GitHub });
        var cut = Render<ServiceConnectionEditDialog>();
        var model = Model(cut.Instance);
        Set(model, "Name", "GitHub");
        Set(model, "ConfigurationJson", "   ");

        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/service-connections", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
        Assert.Equal(true, Spy.LastResult);
    }

    [Fact]
    public async Task Edit_LoadsDetail_AndUsesConnectionEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/service-connections/8", new ServiceConnectionDetailDto
        {
            Id = 8,
            Name = "GitLab prod",
            Description = "Deploy token",
            Type = ServiceConnectionType.GitLab,
            Url = "https://gitlab.example.test",
            ConfigurationJson = "{\"token\":\"masked\"}"
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/service-connections/8",
            new ServiceConnectionDto { Id = 8, Name = "GitLab prod", Type = ServiceConnectionType.GitLab });
        var cut = Render<ServiceConnectionEditDialog>(p => p.Add(x => x.ConnectionId, 8));

        Assert.Contains("GitLab prod", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Deploy token", cut.Markup, StringComparison.Ordinal);
        await InvokeSubmitAsync(cut);

        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.EndsWith("api/service-connections/8", StringComparison.Ordinal));
        Assert.True(Spy.Closed);
    }

    [Fact]
    public void Edit_LoadFailure_StopsLoadingAndLeavesEditableForm()
    {
        _handler.SetResponse(HttpMethod.Get, "api/service-connections/8", HttpStatusCode.ServiceUnavailable);

        var cut = Render<ServiceConnectionEditDialog>(p => p.Add(x => x.ConnectionId, 8));

        Assert.Contains("Credentials", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<AetheusLoader>());
    }

    [Fact]
    public void Cancel_ClosesWithFalse()
    {
        var cut = Render<ServiceConnectionEditDialog>();

        cut.InvokeAsync(() => typeof(ServiceConnectionEditDialog).GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, []));

        Assert.True(Spy.Closed);
        Assert.Equal(false, Spy.LastResult);
    }

    private SpyDialogService Spy => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
    private static object Model(ServiceConnectionEditDialog instance) => typeof(ServiceConnectionEditDialog)
        .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object model, string property, object? value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static Task InvokeSubmitAsync(IRenderedComponent<ServiceConnectionEditDialog> cut) => cut.InvokeAsync(() =>
        (Task)typeof(ServiceConnectionEditDialog).GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
}
