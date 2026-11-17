// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Notifications;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Notifications;

/// <summary>The pipeline page's per-user switch "e-mail me when this pipeline fails", and its list in the settings.</summary>
public sealed class PipelineFailureMailSwitchTests : BunitContext
{
    private const string Url = "api/notifications/me/pipelines/7/failure-mail";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineFailureMailSwitchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void ShowsTheUsersChoice_AndSavingItSendsOnlyTheNewValue()
    {
        _handler.SetJsonResponse(HttpMethod.Get, Url, new PipelineFailureMailPreferenceDto { PipelineId = 7, IsEnabled = true, DefaultEnabled = true });
        _handler.SetJsonResponse(HttpMethod.Put, Url, new PipelineFailureMailPreferenceDto { PipelineId = 7, IsEnabled = false, DefaultEnabled = true, IsSaved = true });

        var cut = Render<PipelineFailureMailSwitch>(parameters => parameters.Add(p => p.PipelineId, 7));

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[role='switch']").GetAttribute("aria-checked")));
        Assert.Contains("PipelineFailureMailSwitch", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".pipeline-failure-mail-scheduled"));

        cut.Find("button[role='switch']").Click();

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("button[role='switch']").GetAttribute("aria-checked")));
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        Assert.EndsWith(Url, put.Url, StringComparison.Ordinal);
        Assert.Contains("\"isEnabled\":false", put.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AScheduledPipeline_IsOffByDefault_AndSaysWhy()
    {
        _handler.SetJsonResponse(HttpMethod.Get, Url, new PipelineFailureMailPreferenceDto { PipelineId = 7, IsEnabled = false, DefaultEnabled = false, IsScheduled = true });

        var cut = Render<PipelineFailureMailSwitch>(parameters => parameters.Add(p => p.PipelineId, 7));

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("button[role='switch']").GetAttribute("aria-checked")));
        Assert.Contains("PipelineFailureMailScheduledHint", cut.Find(".pipeline-failure-mail-scheduled").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheRightToReadThePipeline_NothingIsDrawn()
    {
        _handler.SetResponse(HttpMethod.Get, Url, System.Net.HttpStatusCode.Forbidden);

        var cut = Render<PipelineFailureMailSwitch>(parameters => parameters.Add(p => p.PipelineId, 7));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Method == "GET" && request.Url.EndsWith(Url, StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("button[role='switch']"));
    }

    [Fact]
    public void TheSettingsListsTheChoicesTheUserSaved_AndTurnsOneBackOn()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/me/pipeline-failure-mails", new List<PipelineFailureMailChoiceDto>
        {
            new() { PipelineId = 7, PipelineName = "nightly", IsEnabled = false, DefaultEnabled = false, IsScheduled = true }
        });
        _handler.SetJsonResponse(HttpMethod.Put, Url, new PipelineFailureMailPreferenceDto { PipelineId = 7, IsEnabled = true, IsSaved = true, IsScheduled = true });

        var cut = Render<PipelineFailureMailChoices>();

        cut.WaitForAssertion(() => Assert.Contains("nightly", cut.Find(".pipeline-failure-mail-choice").TextContent, StringComparison.Ordinal));
        Assert.Equal("false", cut.Find(".pipeline-failure-mail-choice button[role='switch']").GetAttribute("aria-checked"));

        cut.Find(".pipeline-failure-mail-choice button[role='switch']").Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".pipeline-failure-mail-choice button[role='switch']").GetAttribute("aria-checked")));
    }

    [Fact]
    public void TheSettingsListIsEmpty_WhenTheUserSavedNoChoice()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/me/pipeline-failure-mails", new List<PipelineFailureMailChoiceDto>());

        var cut = Render<PipelineFailureMailChoices>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.EndsWith("api/notifications/me/pipeline-failure-mails", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll(".pipeline-failure-mail-choice"));
    }
}
