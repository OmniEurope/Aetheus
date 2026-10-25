// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Services;

public class PipelineRunGateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunGateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private PipelineRunGate Sut => Services.GetRequiredService<PipelineRunGate>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();
    private ImmediateDialogService Dialog =>
        (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    // === Happy path - all stages resolved, no dialog ===

    [Fact]
    public async Task ConfirmPreflightAsync_AllResolved_NoFallbacks_ReturnsTrue()
    {
        // 200 → PipelinePreflightDto (no unresolved stages, no server names)
        _handler.SetJsonResponse("api/pipelines/1/preflight", new PipelinePreflightDto
        {
            Stages =
            [
                new PreflightStageDto { StageName = "Build", Resolved = true, ServerName = null, Target = "build-pool" }
            ]
        });

        var result = await Sut.ConfirmPreflightAsync(1);

        Assert.True(result);
        // No resolved stage carries a ServerName, so no fallback-transparency toast is shown.
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task ConfirmPreflightAsync_AllResolved_WithFallbacks_ReturnsTrueWithoutAToast()
    {
        // 200 → resolved stages with server names
        _handler.SetJsonResponse("api/pipelines/2/preflight", new PipelinePreflightDto
        {
            Stages =
            [
                new PreflightStageDto { StageName = "Deploy", Resolved = true, ServerName = "web-01", Reason = "tag match", Target = "prod" }
            ]
        });

        var result = await Sut.ConfirmPreflightAsync(2);

        Assert.True(result);
        // R-26: the "runners found" information toast is gone; a resolved launch says nothing.
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task ConfirmPreflightAsync_EmptyStages_ReturnsTrue()
    {
        _handler.SetJsonResponse("api/pipelines/3/preflight", new PipelinePreflightDto { Stages = [] });

        var result = await Sut.ConfirmPreflightAsync(3);

        Assert.True(result);
    }

    [Fact]
    public async Task ConfirmPreflightAsync_SendsTheChosenParametersToThePreview()
    {
        _handler.SetJsonResponse("api/pipelines/14/preflight", new PipelinePreflightDto { Stages = [] });

        var result = await Sut.ConfirmPreflightAsync(14, "main", new Dictionary<string, string> { ["deployServer"] = "web-01" });

        Assert.True(result);
        // The readiness call that follows has no body: read the preview request itself.
        var preview = _handler.RequestDetails.Single(request => request.Url.Contains("api/pipelines/14/preflight", StringComparison.Ordinal));
        Assert.Contains("web-01", preview.Body, StringComparison.Ordinal);
        Assert.Contains("deployServer", preview.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmPreflightAsync_WithWarnings_ShowsWarningsAndContinues()
    {
        _handler.SetJsonResponse("api/pipelines/13/preflight", new PipelinePreflightDto
        {
            Warnings = ["Variable library 'optional' not found."],
            Stages =
            [
                new PreflightStageDto { StageName = "Build", Resolved = true, Target = "ci" }
            ]
        });

        var result = await Sut.ConfirmPreflightAsync(13);

        Assert.True(result);
        var warning = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Warning, warning.Severity);
        Assert.Contains("optional", warning.Detail?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmPreflightAsync_UnresolvedTarget_UsesExplicitConfirmation(bool confirm)
    {
        _handler.SetJsonResponse("api/pipelines/9/preflight", new PipelinePreflightDto
        {
            Stages =
            [
                new PreflightStageDto
                {
                    StageName = "Deploy",
                    Resolved = false,
                    Target = "prod-web",
                    Reason = "no online matching agent"
                }
            ]
        });
        Dialog.OpenResult = confirm;

        var result = await Sut.ConfirmPreflightAsync(9);

        Assert.Equal(confirm, result);
        Assert.Equal(1, Dialog.OpenCount);
        Assert.Equal("PreflightNoAgentTitle", Dialog.LastTitle);

        // Assert unresolved-stage data passed to the grouped gate dialog rather than repeated
        // formatted reason strings.
        Assert.Equal(typeof(PipelineRunPreflightDialog), Dialog.LastComponent);
        var unresolved = Assert.IsAssignableFrom<IReadOnlyList<PreflightStageDto>>(
            Dialog.LastParameters![nameof(PipelineRunPreflightDialog.Unresolved)]);
        var stage = Assert.Single(unresolved);
        Assert.Equal("Deploy", stage.StageName);
        Assert.Equal("prod-web", stage.Target);
        Assert.Equal("no online matching agent", stage.Reason);
    }

    // === Error path - 400 with YamlValidationResultDto body, no dialog ===

    [Fact]
    public async Task ConfirmPreflightAsync_WithError_ReturnsFalse()
    {
        // 400 → YamlValidationResultDto parsed as error
        _handler.SetJsonResponse("api/pipelines/4/preflight",
            new YamlValidationResultDto { IsValid = false, Errors = ["Invalid YAML: line 5"] });
        // Override with 400 status - we need to re-register as 400
        // The TestHandler uses SetResponse for a specific status:
        // We can't set 400 with typed body via the helper, so use a workaround:
        // Return a 400 with the JSON body by registering a custom response
        RegisterBadRequest("api/pipelines/4/preflight",
            new YamlValidationResultDto { IsValid = false, Errors = ["Invalid YAML: line 5"] });

        var result = await Sut.ConfirmPreflightAsync(4);

        Assert.False(result);
    }

    [Fact]
    public async Task ConfirmPreflightAsync_WithMultipleErrors_ReturnsFalse()
    {
        RegisterBadRequest("api/pipelines/5/preflight",
            new YamlValidationResultDto { IsValid = false, Errors = ["Error 1", "Error 2"] });

        var result = await Sut.ConfirmPreflightAsync(5);

        Assert.False(result);
    }

    // === Null value path - 200 with empty body ===

    [Fact]
    public async Task ConfirmPreflightAsync_NullPreflightDto_ReturnsTrue()
    {
        // 200 with no stages means proceed (no unresolved)
        _handler.SetJsonResponse("api/pipelines/6/preflight", new PipelinePreflightDto { Stages = [] });

        var result = await Sut.ConfirmPreflightAsync(6);

        Assert.True(result);
    }

    // === Multiple fallbacks ===

    [Fact]
    public async Task ConfirmPreflightAsync_MultipleFallbacks_ReturnsTrue()
    {
        _handler.SetJsonResponse("api/pipelines/7/preflight", new PipelinePreflightDto
        {
            Stages =
            [
                new PreflightStageDto { StageName = "Build", Resolved = true, ServerName = "build-01", Reason = "pool", Target = "build-pool" },
                new PreflightStageDto { StageName = "Deploy", Resolved = true, ServerName = "web-01", Reason = null, Target = "prod" }
            ]
        });

        var result = await Sut.ConfirmPreflightAsync(7);

        Assert.True(result);
        // Two resolved stages with a server name raise no toast (R-26).
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task ConfirmPreflightAsync_FallbackWithNoReason_RaisesNoToast()
    {
        _handler.SetJsonResponse("api/pipelines/8/preflight", new PipelinePreflightDto
        {
            Stages =
            [
                new PreflightStageDto { StageName = "Test", Resolved = true, ServerName = "ci-01", Reason = null, Target = "ci" }
            ]
        });

        var result = await Sut.ConfirmPreflightAsync(8);

        Assert.True(result);
        Assert.Empty(Toasts.Toasts());
    }

    [Fact]
    public async Task ConfirmPreflightAsync_MiddlewareErrorBody_ToastsItsMessageInsteadOfThrowing()
    {
        // An HTTP 400 middleware error object with errors:null must not become a successful
        // validation response.
        RegisterBadRequest("api/pipelines/9/preflight", new ApiError
        {
            Message = "The authoritative pipeline YAML is invalid and cannot be run.",
            CorrelationId = "0HN7:00000002"
        });

        var result = await Sut.ConfirmPreflightAsync(9);

        Assert.False(result);
        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("cannot be run", toast.Detail?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "PreflightSessionExpired")]
    [InlineData(HttpStatusCode.Forbidden, "PreflightForbidden")]
    [InlineData(HttpStatusCode.InternalServerError, "PreflightServerError")]
    public async Task ConfirmPreflightAsync_TransportFailure_ToastsItsCauseAndAborts(HttpStatusCode status, string reasonKey)
    {
        _handler.SetResponse("api/pipelines/10/preflight", status);

        var result = await Sut.ConfirmPreflightAsync(10);

        Assert.False(result);
        var toast = Assert.Single(Toasts.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Equal("PipelineRunFailed", toast.Summary?.ToString());
        Assert.Equal(Services.GetRequiredService<IStringLocalizer<AppStrings>>()[reasonKey].Value, toast.Detail?.ToString());
    }

    /// <summary>
    /// Helper: register a response that returns 400 Bad Request with a JSON body.
    /// Used to exercise the error-path in PipelineRunGate without triggering Dialog.Confirm.
    /// </summary>
    private void RegisterBadRequest<T>(string urlContains, T body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        // We reach into the handler using the same mechanism as SetJsonResponse but with status 400.
        // TestHandler doesn't expose this directly, so we use SetResponse then rely on the
        // overload to return a pre-built response (workaround: override with a custom content).
        // Instead, simply call the internal dictionary via reflection.
        var responses = (System.Collections.Generic.Dictionary<string, Func<System.Net.Http.HttpResponseMessage>>)
            typeof(BunitTestHelper.TestHandler)
                .GetField("_responses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_handler)!;

        responses[urlContains] = () => new System.Net.Http.HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
