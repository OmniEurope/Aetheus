// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineRunLauncherTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler handler;

    public PipelineRunLauncherTests()
        => handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public async Task LaunchAsync_Triggered_GoesStraightToTheNewRunWithoutReloadingThePipeline()
    {
        handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/5/run", new PipelineRunDto { Id = 2460, PipelineId = 5 });
        var navigation = Services.GetRequiredService<NavigationManager>();
        var launcher = new PipelineRunLauncher(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            navigation,
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<IStringLocalizer<AppStrings>>());

        var result = await launcher.LaunchAsync(5, sourceBranch: null);

        Assert.Equal(2460, result!.TriggeredRun.Id);
        Assert.EndsWith("/pipelines/runs/2460", navigation.Uri, StringComparison.Ordinal);
        // Neither the run list nor the definition is fetched again behind the page being left.
        Assert.DoesNotContain(handler.Requests, request => request.Method == "GET" && request.Url.EndsWith("api/pipelines/5", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchAsync_TriggerRefusal_SurfacesServerReasonInsteadOfGenericMessage()
    {
        handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        handler.SetJsonResponse(
            HttpMethod.Post,
            "api/pipelines/5/run",
            new YamlValidationResultDto { Errors = ["Matrix axis 'bad-axis' is invalid."] },
            HttpStatusCode.BadRequest);
        var launcher = new PipelineRunLauncher(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<IStringLocalizer<AppStrings>>());

        var result = await launcher.LaunchAsync(5, sourceBranch: null);

        Assert.Null(result);
        var notification = Assert.Single(
            Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
        Assert.Contains("bad-axis", notification.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("PipelineRunFailedNoReason", notification.Detail, StringComparison.Ordinal);
    }

    /// <summary>Middleware error payloads may contain a null <c>errors</c> field; the launcher must
    /// extract the refusal without joining a null list.</summary>
    private static string MiddlewareRefusalBody() => System.Text.Json.JsonSerializer.Serialize(
        new ApiError
        {
            Message = "This run cannot complete with the current configuration: Port 8080 is taken. Runner lacks docker.",
            CorrelationId = "0HN7:00000001"
        },
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    public static TheoryData<string, string, string> RefusalBodies => new()
    {
        { MiddlewareRefusalBody(), "application/json", "Port 8080 is taken." },
        { """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Parameters":["Parameter 'env' is required."]}}""", "application/problem+json", "Parameter 'env' is required." },
        { """{"title":"Bad Request","status":400,"detail":"The selected source branch is invalid."}""", "application/problem+json", "The selected source branch is invalid." },
        { "\"The authoritative pipeline YAML is invalid and cannot be run.\"", "application/json", "The authoritative pipeline YAML is invalid and cannot be run." },
        { "The selected source branch is invalid.", "text/plain", "The selected source branch is invalid." },
        { """{"errors":null}""", "application/json", "PipelineRunFailedNoReason" },
        { "", "application/json", "PipelineRunFailedNoReason" },
    };

    [Fact]
    public async Task TriggerAsync_RefusedThroughTheGlobalErrorNotifier_ShowsExactlyOneToastWithTheReason()
    {
        // The global notifier precedes transport; launch refusal must produce one notification
        // through this chain.
        handler.SetRawResponse(HttpMethod.Post, "api/pipelines/5/run", MiddlewareRefusalBody(), "application/json", HttpStatusCode.BadRequest);
        var toast = Services.GetRequiredService<NotifyHelper>();
        var localizer = Services.GetRequiredService<IStringLocalizer<AppStrings>>();
        using var notifier = new ErrorNotificationHandler(toast, localizer) { InnerHandler = handler };
        using var http = new HttpClient(notifier, disposeHandler: false) { BaseAddress = new Uri("http://test/") };
        var launcher = new PipelineRunLauncher(
            new ApiClient(http),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<NavigationManager>(),
            toast,
            localizer);

        var result = await launcher.TriggerAsync(5, sourceBranch: null, parameters: null);

        Assert.Null(result);
        var notification = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, notification.Severity);
        Assert.Contains("Port 8080 is taken.", notification.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RefusalBodies))]
    public async Task LaunchAsync_AnyRefusalBodyShape_ShowsItsReasonInsteadOfThrowing(
        string body, string contentType, string expected)
    {
        handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
        handler.SetRawResponse(HttpMethod.Post, "api/pipelines/5/run", body, contentType, HttpStatusCode.BadRequest);
        var launcher = new PipelineRunLauncher(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<IStringLocalizer<AppStrings>>());

        var result = await launcher.LaunchAsync(5, sourceBranch: null);

        Assert.Null(result);
        var notification = Assert.Single(
            Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
        Assert.Contains(expected, notification.Detail, StringComparison.Ordinal);
    }
}
