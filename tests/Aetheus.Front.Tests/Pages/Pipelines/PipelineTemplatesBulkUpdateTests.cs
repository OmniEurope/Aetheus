// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-8: "Update all pipelines" asks once, sends one request for the scope and shows what stayed as it was.</summary>
public sealed class PipelineTemplatesBulkUpdateTests : BunitContext
{
    private const string Url = "api/pipelines/fleet-update/generic-templates";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineTemplatesBulkUpdateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Fact]
    public void Hidden_RendersNoButton()
    {
        var cut = Render<PipelineTemplatesBulkUpdate>(parameters => parameters.Add(p => p.Visible, false));

        Assert.Empty(cut.FindAll("[data-testid=update-all-pipelines]"));
    }

    [Fact]
    public void AConfirmedClick_UpdatesTheProjectAndListsWhatStayedAsItWas()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineFleetBulkUpdateResultDto
        {
            Items =
            [
                Item(1, "w-ci", PipelineFleetBulkUpdateOutcome.Updated),
                Item(2, "w-deploy-prod", PipelineFleetBulkUpdateOutcome.Refused, "changed in the repository"),
                Item(3, "w-qa", PipelineFleetBulkUpdateOutcome.Forbidden)
            ]
        });
        Dialog.ConfirmResult = true;
        var updated = 0;
        var cut = Render<PipelineTemplatesBulkUpdate>(parameters => parameters
            .Add(p => p.ProjectId, 12)
            .Add(p => p.Visible, true)
            .Add(p => p.Updated, () => updated++));

        cut.Find("[data-testid=update-all-pipelines]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=update-all-pipelines-result]")));
        var request = Assert.Single(_handler.RequestDetails, item => item.Method == "POST");
        Assert.Contains("\"projectId\":12", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UpdateAllPipelinesConfirm", Dialog.LastConfirmMessage);
        Assert.Equal(1, updated);
        var result = cut.Find("[data-testid=update-all-pipelines-result]");
        // The test localizer returns the key: the title is the result line, the body names the two left untouched.
        Assert.Contains("UpdateAllPipelinesResult", result.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, result.TextContent.Split("UpdateAllPipelinesItem").Length - 1);
        Assert.Equal(OmniSeverity.Warning, cut.FindComponent<OmniAlert>().Instance.Severity);
    }

    [Fact]
    public void ADeclinedConfirmation_SendsNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = Render<PipelineTemplatesBulkUpdate>(parameters => parameters.Add(p => p.Visible, true));

        cut.Find("[data-testid=update-all-pipelines]").Click();

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "POST");
        Assert.Empty(cut.FindAll("[data-testid=update-all-pipelines-result]"));
    }

    [Fact]
    public void NothingToUpdate_SaysSoWithoutRaisingUpdated()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineFleetBulkUpdateResultDto());
        Dialog.ConfirmResult = true;
        var updated = 0;
        var cut = Render<PipelineTemplatesBulkUpdate>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Updated, () => updated++));

        cut.Find("[data-testid=update-all-pipelines]").Click();

        cut.WaitForAssertion(() => Assert.Contains("UpdateAllPipelinesNothing",
            cut.Find("[data-testid=update-all-pipelines-result]").TextContent, StringComparison.Ordinal));
        Assert.Equal(0, updated);
    }

    private static PipelineFleetBulkUpdateItemDto Item(
        int id, string name, PipelineFleetBulkUpdateOutcome outcome, string? reason = null) => new()
        {
            PipelineId = id,
            PipelineName = name,
            TemplateName = "application-ci",
            FromVersion = 1,
            ToVersion = 3,
            Outcome = outcome,
            Reason = reason
        };
}
