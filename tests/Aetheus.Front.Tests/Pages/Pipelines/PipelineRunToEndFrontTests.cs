// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// "Run to end" in the front: the launch option exists only when the platform allows it, the run page
/// says the run went on past its failures and names them, and the administrator turns it on with a
/// switch. The test localizer renders keys, so the assertions read resource keys.
/// </summary>
public sealed class PipelineRunToEndFrontTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunToEndFrontTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void LaunchDialog_SettingOff_OffersNoRunToEndOption()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.RunToEndAllowed, false));

        Assert.Empty(cut.FindComponents<OmniSwitch<bool>>());
        Assert.DoesNotContain("RunToEndHelp", cut.Markup, StringComparison.Ordinal);
        // Nothing to choose at all: the dialog still says so.
        Assert.Contains("NoRunOptions", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchDialog_SettingOn_OffersTheSwitchWithItsHelp_OffByDefault()
    {
        var cut = Render<PipelineLaunchDialog>(parameters => parameters
            .Add(dialog => dialog.RunToEndAllowed, true));

        var toggle = Assert.Single(cut.FindComponents<OmniSwitch<bool>>());
        Assert.False(toggle.Instance.Value);
        Assert.Contains("RunToEnd", toggle.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindComponents<OmniFormField>(), field => field.Instance.Help == "RunToEndHelp");
        Assert.DoesNotContain("NoRunOptions", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_SendsTheModeOnlyWhenChosen()
    {
        _handler.SetJsonResponse("api/pipelines/3/run", new PipelineRunDto { Id = 9 });
        var api = Services.GetRequiredService<ApiClient>();

        await api.Pipelines.TriggerPipelineRunAsync(3, ct: Xunit.TestContext.Current.CancellationToken, runToEnd: true);
        await api.Pipelines.TriggerPipelineRunAsync(3, ct: Xunit.TestContext.Current.CancellationToken);

        var bodies = _handler.RequestDetails.Where(request => request.Url.Contains("api/pipelines/3/run", StringComparison.Ordinal))
            .Select(request => request.Body ?? string.Empty).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"AETHEUS_RUN_TO_END\":\"true\"", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("AETHEUS_RUN_TO_END", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RunPage_NormalRun_ShowsNoRunToEndBanner()
    {
        var cut = Render<PipelineRunToEndAlert>(parameters => parameters
            .Add(alert => alert.Run, new PipelineRunDto { Id = 1, Status = PipelineStatus.Failed }));

        Assert.Empty(cut.FindComponents<OmniAlert>());
    }

    [Fact]
    public void RunPage_RunToEndRun_SaysSoAndNamesEveryFailedStep()
    {
        var run = new PipelineRunDto
        {
            Id = 1,
            Status = PipelineStatus.Failed,
            RunToEnd = true,
            Steps =
            [
                new PipelineStepRunDto { StepName = "integration", Status = TaskExecutionStatus.Failed },
                new PipelineStepRunDto { StepName = "security-scan", Status = TaskExecutionStatus.Success },
                new PipelineStepRunDto { StepName = "e2e", Status = TaskExecutionStatus.Timeout },
                new PipelineStepRunDto { StepName = "Cleanup", Status = TaskExecutionStatus.Failed, IsSystem = true }
            ]
        };

        var cut = Render<PipelineRunToEndAlert>(parameters => parameters.Add(alert => alert.Run, run));

        var alert = Assert.Single(cut.FindComponents<OmniAlert>());
        Assert.Equal("RunToEndRunTitle", alert.Instance.Title);
        Assert.Equal(OmniSeverity.Warning, alert.Instance.Severity);
        Assert.Contains("RunToEndFailedSteps", alert.Markup, StringComparison.Ordinal);
        Assert.Equal(["integration", "e2e"], cut.Instance.FailedSteps);
    }

    [Fact]
    public void RunPage_RunToEndRunWithoutFailure_SaysNoStepFailed()
    {
        var cut = Render<PipelineRunToEndAlert>(parameters => parameters.Add(alert => alert.Run, new PipelineRunDto
        {
            Id = 1,
            Status = PipelineStatus.Success,
            RunToEnd = true,
            Steps = [new PipelineStepRunDto { StepName = "integration", Status = TaskExecutionStatus.Success }]
        }));

        var alert = Assert.Single(cut.FindComponents<OmniAlert>());
        Assert.Equal(OmniSeverity.Info, alert.Instance.Severity);
        Assert.Contains("RunToEndNoFailure", alert.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSettings_ShowTheSwitchApart_AndSaveItAsTrueOrFalse()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new() { Key = "SiteName", Value = "Aetheus" },
            new() { Key = PipelineRunToEndSetting.Key, Value = "false" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());
        _handler.SetResponse("api/settings/Pipelines%3AAllowRunToEnd", System.Net.HttpStatusCode.OK);

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.FindComponents<OmniSettingsTile>().Any(tile => tile.Instance.Title == "AllowRunToEnd"));

        // Not a free-text row: the key has no text box of its own.
        Assert.DoesNotContain(cut.FindComponents<OmniTextBox>(),
            box => box.Instance.Id == $"platform-setting-{PipelineRunToEndSetting.Key}");
        var tile = cut.FindComponents<OmniSettingsTile>().Single(tile => tile.Instance.Title == "AllowRunToEnd");
        var toggle = tile.FindComponent<OmniSwitch<bool>>();
        Assert.False(toggle.Instance.Value);

        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(true));

        var put = Assert.Single(_handler.RequestDetails, request =>
            request.Method == "PUT" && request.Url.Contains("Pipelines%3AAllowRunToEnd", StringComparison.Ordinal));
        Assert.Contains("\"value\":\"true\"", put.Body ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        cut.WaitForAssertion(() => Assert.True(
            cut.FindComponents<OmniSettingsTile>().Single(tile => tile.Instance.Title == "AllowRunToEnd")
                .FindComponent<OmniSwitch<bool>>().Instance.Value));
    }
}
