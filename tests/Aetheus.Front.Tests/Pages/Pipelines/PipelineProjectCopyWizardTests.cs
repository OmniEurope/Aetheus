// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R1-29, option 1: the setup wizard starts from an existing project. The path is driven
/// through the wizard page itself: the start choice, the projects and prefixes, the plan the backend
/// returns, the copy request, and the result that lists the empty values and what remains to set up.
/// </summary>
public sealed class PipelineProjectCopyWizardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineProjectCopyWizardTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProjectCopyPlanDto Plan(bool created = false) => new()
    {
        Pipelines =
        [
            new PipelineCopyItemDto
            {
                SourcePipelineId = 10, SourceName = "aetheus-candidate", NewName = "ats-candidate",
                Status = PipelineCopyStatus.ToReview, CreatedPipelineId = created ? 501 : null,
                Issues = [new PipelineCopyIssueDto { Kind = PipelineCopyIssueKind.PinnedAgent, Detail = "vps-aetheus-01" }]
            },
            new PipelineCopyItemDto
            {
                SourcePipelineId = 11, SourceName = "aetheus-deploy-prod", NewName = "ats-deploy-prod",
                Status = PipelineCopyStatus.NotCopied,
                Issues = [new PipelineCopyIssueDto { Kind = PipelineCopyIssueKind.NameTaken, Detail = "ats-deploy-prod" }]
            }
        ],
        Libraries = [new PipelineCopyResourceDto { SourceName = "aetheus-prod-host", NewName = "ats-prod-host", Keys = ["HOST", "PORT"], CreatedId = created ? 300 : null }],
        Vaults = [new PipelineCopyResourceDto { SourceName = "aetheus-deploy", NewName = "ats-deploy", Keys = ["DB_PASSWORD"], CreatedId = created ? 400 : null }]
    };

    [Fact]
    public async Task StartingFromAnExistingProject_CopiesTheChosenPipelines_AndListsWhatRemains()
    {
        _handler.SetPaginatedJsonResponse("api/projects",
        [
            new ProjectDto { Id = 1, Name = "Aetheus", DefaultBranch = "main" },
            new ProjectDto { Id = 2, Name = "ATS", DefaultBranch = "develop" }
        ]);
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/copy-plan", Plan());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/copy", new PipelineProjectCopyResultDto
        {
            Plan = Plan(created: true),
            Readiness = new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                Checks = [new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingEnvironments, Severity = PipelineSetupReadinessSeverity.Blocking, Items = ["prod"] }]
            }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=2");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => cut.FindComponent<OmniRadioButtonList<bool>>());

        var start = cut.FindComponent<OmniRadioButtonList<bool>>();
        await cut.InvokeAsync(() => start.Instance.ValueChanged.InvokeAsync(true));
        var copy = cut.FindComponent<PipelineProjectCopyWizard>();
        // The new project and its prefix come from the page's project; the source is chosen.
        Assert.Equal(2, copy.Instance.TargetProjectId);
        Assert.Equal("ats", copy.Instance.Prefix);
        await cut.InvokeAsync(() => copy.Instance.SetSourceProject(1));
        Assert.Equal("aetheus", copy.Instance.SourcePrefix);

        var wizard = copy.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll("[data-testid='pipeline-copy-item']").Count);
            Assert.Contains("vps-aetheus-01", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("HOST, PORT", cut.Markup, StringComparison.Ordinal);
        });
        // A copy the backend will not make is offered unchecked and disabled.
        Assert.Equal([10], copy.Instance.Selected);

        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.NotNull(copy.Instance.Result));
        var body = _handler.RequestDetails.Single(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines/setup/copy", StringComparison.OrdinalIgnoreCase)).Body!;
        var sent = JsonSerializer.Deserialize<PipelineProjectCopyRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((1, 2, "aetheus", "ats", "develop"), (sent.SourceProjectId, sent.TargetProjectId, sent.SourcePrefix, sent.Prefix, sent.TargetBranch));
        Assert.Equal([10], sent.PipelineIds);
        // Never a value travels: the request has no field for one, the result names keys only.
        Assert.DoesNotContain("Value", body, StringComparison.OrdinalIgnoreCase);

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid='pipeline-copy-result']"));
            Assert.Contains("DB_PASSWORD", cut.Markup, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid='setup-readiness']"));
            Assert.Contains("prod", cut.Find("[data-testid='setup-readiness']").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheTemplatesPathStaysTheDefault()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 2, Name = "ATS" }]);
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var cut = Render<PipelineSetupWizard>();

        cut.WaitForAssertion(() => cut.FindComponent<WizardDialog>());
        Assert.Empty(cut.FindComponents<PipelineProjectCopyWizard>());
    }

    [Theory]
    [InlineData("ats", true)]
    [InlineData("oe-performance", true)]
    [InlineData("", false)]
    [InlineData("ATS", false)]
    [InlineData("a--b", false)]
    public void PrefixShape_MatchesTheRequestValidation(string prefix, bool valid) =>
        Assert.Equal(valid, PipelineProjectCopyWizard.IsPrefix(prefix));
}
