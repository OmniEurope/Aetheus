// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineRunRerunCoordinatorTests : BunitContext
{
    [Fact]
    public async Task ResumeCheckpoints_UsesExplicitRerunModeWithoutParameterDialog()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/42/checkpoint-resume-preview",
            new PipelineCheckpointResumePreviewDto
            {
                SourceRunId = 42,
                Items =
                [
                    new PipelineCheckpointResumeItemDto
                    {
                        PipelineName = "aetheus-ci",
                        RunId = 40,
                        ReuseCandidate = true
                    }
                ]
            });
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/runs/42/rerun",
            new PipelineRunDto { Id = 99 });
        var coordinator = new PipelineRunRerunCoordinator(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<OmniDialogService>(),
            new BunitTestHelper.StubLocalizer());
        ((Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services
            .GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        var run = new PipelineRunDto { Id = 42, PipelineId = 5, Status = PipelineStatus.Failed };

        await coordinator.RerunAsync(run, "resumeCheckpoints");

        Assert.Contains(handler.Requests, request =>
            request.Url.Contains("api/pipelines/runs/42/rerun?mode=ResumeCheckpoints", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Url.Contains("api/pipelines/5/parameters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParameterLoadFailure_DoesNotFallBackToDirectRerun()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse("api/pipelines/5/parameters",
            System.Net.HttpStatusCode.InternalServerError);
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/runs/42/rerun",
            new PipelineRunDto { Id = 99 });
        var coordinator = new PipelineRunRerunCoordinator(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<OmniDialogService>(),
            new BunitTestHelper.StubLocalizer());
        var run = new PipelineRunDto
        {
            Id = 42,
            PipelineId = 5,
            Status = PipelineStatus.Failed
        };

        await coordinator.RerunAsync(run, null);

        Assert.Contains(handler.Requests, request =>
            request.Url.Contains("api/pipelines/5/parameters", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Url.Contains("api/pipelines/runs/42/rerun", StringComparison.Ordinal));
    }

    /// <summary>R-47 on the rerun path: the API refuses to read the pipeline's parameters (invalid YAML, 400
    /// with its reason). The rerun stops and the user reads that reason, not a generic "load failed".</summary>
    [Fact]
    public async Task ParameterLoadRefusedWithAReason_ShowsTheReasonOnce_AndStartsNothing()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetRawResponse(
            HttpMethod.Get,
            "api/pipelines/5/parameters",
            """{"message":"The pipeline YAML is invalid: While scanning a plain scalar value, found invalid mapping.","correlationId":"0HN7:00000001"}""",
            "application/json",
            System.Net.HttpStatusCode.BadRequest);
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/runs/42/rerun", new PipelineRunDto { Id = 99 });
        var coordinator = new PipelineRunRerunCoordinator(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<OmniDialogService>(),
            new BunitTestHelper.StubLocalizer());

        await coordinator.RerunAsync(FailedRun(), null);

        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniEurope.Blazor.Components.OmniSeverity.Danger, toast.Severity);
        Assert.Contains("The pipeline YAML is invalid: While scanning", toast.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("api/pipelines/runs/42/rerun", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "PreflightForbidden")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "RunParametersReadFailed")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "RunParametersReadFailed")]
    public async Task ParameterLoadRefusedThroughTheGlobalNotifier_ShowsExactlyOneToast(
        System.Net.HttpStatusCode status, string expectedKey)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse("api/pipelines/5/parameters", status);
        var toast = Services.GetRequiredService<NotifyHelper>();
        var localizer = new BunitTestHelper.StubLocalizer();
        using var notifier = new ErrorNotificationHandler(toast, localizer) { InnerHandler = handler };
        using var http = new HttpClient(notifier, disposeHandler: false) { BaseAddress = new Uri("http://test/") };
        var coordinator = new PipelineRunRerunCoordinator(
            new ApiClient(http),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            toast,
            Services.GetRequiredService<OmniDialogService>(),
            localizer);

        await coordinator.RerunAsync(FailedRun(), null);

        var raised = Assert.Single(Services.Toasts());
        Assert.Contains(expectedKey, raised.Detail, StringComparison.Ordinal);
    }

    private (PipelineRunRerunCoordinator Coordinator, BunitTestHelper.TestHandler Handler, Aetheus.Front.Tests.TestDoubles.ImmediateDialogService Dialog) WithParameters(
        params PipelineRunParameterDto[] declared)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/5/parameters", declared.ToList());
        var coordinator = new PipelineRunRerunCoordinator(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<OmniDialogService>(),
            new BunitTestHelper.StubLocalizer());
        return (coordinator, handler, (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>());
    }

    private static PipelineRunDto FailedRun() => new()
    {
        Id = 42,
        PipelineId = 5,
        Status = PipelineStatus.Failed,
        Parameters = new Dictionary<string, string> { ["TARGET"] = "staging" }
    };

    private string CurrentUri => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

    [Fact]
    public async Task ARunWithParameters_AsksThemPrefilled_ThenStartsANewRunWithTheAnswers()
    {
        var (coordinator, handler, dialog) = WithParameters(new PipelineRunParameterDto { Name = "TARGET" });
        dialog.OpenResult = new Dictionary<string, string> { ["TARGET"] = "production" };
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/5/run", new PipelineRunDto { Id = 77 });

        await coordinator.RerunAsync(FailedRun(), null);

        Assert.Equal(typeof(RunParametersDialog), dialog.LastComponent);
        Assert.Equal(new Dictionary<string, string> { ["TARGET"] = "staging" }, dialog.LastParameters!["Prefill"]);
        var post = Assert.Single(handler.RequestDetails, request => request.Method == "POST");
        Assert.EndsWith("api/pipelines/5/run", post.Url, StringComparison.Ordinal);
        Assert.Contains("\"TARGET\":\"production\"", post.Body, StringComparison.Ordinal);
        Assert.EndsWith("/pipelines/runs/77", CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DismissingTheParameters_StartsNothing()
    {
        var (coordinator, handler, dialog) = WithParameters(new PipelineRunParameterDto { Name = "TARGET" });
        dialog.OpenResult = null;
        var before = CurrentUri;

        await coordinator.RerunAsync(FailedRun(), null);

        Assert.DoesNotContain(handler.Requests, request => request.Method == "POST");
        Assert.Equal(before, CurrentUri);
    }

    [Fact]
    public async Task ARefusedRun_StaysOnThePage_AndSaysWhy()
    {
        var (coordinator, handler, dialog) = WithParameters(new PipelineRunParameterDto { Name = "TARGET" });
        dialog.OpenResult = new Dictionary<string, string> { ["TARGET"] = "production" };
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/5/run", new { }, System.Net.HttpStatusCode.Forbidden);
        var before = CurrentUri;

        await coordinator.RerunAsync(FailedRun(), null);

        Assert.Single(handler.Requests, request => request.Method == "POST" && request.Url.EndsWith("api/pipelines/5/run", StringComparison.Ordinal));
        Assert.Equal(before, CurrentUri);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger);
    }

    [Fact]
    public async Task ARunWithoutParameters_IsRerunDirectly_WithTheCurrentDefinition()
    {
        var (coordinator, handler, dialog) = WithParameters();
        handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/runs/42/rerun", new PipelineRunDto { Id = 78 });

        await coordinator.RerunAsync(FailedRun(), null);

        Assert.Equal(0, dialog.OpenCount);
        Assert.Contains(handler.Requests, request => request.Url.Contains("api/pipelines/runs/42/rerun?mode=Current", StringComparison.Ordinal));
        Assert.EndsWith("/pipelines/runs/78", CurrentUri, StringComparison.Ordinal);
    }
}
