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
