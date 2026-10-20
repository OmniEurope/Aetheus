// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Exercises the copy wizard through project selection, backend planning, copy request and
/// the resulting empty-value checklist.</summary>
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

    private async Task<(IRenderedComponent<PipelineSetupWizard> Cut, IRenderedComponent<PipelineProjectCopyWizard> Copy)> CopyAsync()
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
        await cut.InvokeAsync(() => cut.FindComponent<OmniRadioButtonList<bool>>().Instance.ValueChanged.InvokeAsync(true));
        var copy = cut.FindComponent<PipelineProjectCopyWizard>();
        await cut.InvokeAsync(() => copy.Instance.SetSourceProject(1));
        var wizard = copy.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='pipeline-copy-item']").Count));
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='setup-readiness']")));
        return (cut, copy);
    }

    private int ReadinessReads() =>
        _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/setup/project-readiness?projectId=2", StringComparison.Ordinal));

    // A library, vault or repository created from the result changes the verdict: it is read again for
    // the new project and replaces the one shown.
    [Fact]
    public async Task AChangeFromTheResult_ReadsTheVerdictAgain_AndShowsTheNewOne()
    {
        var (cut, copy) = await CopyAsync();
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/setup/project-readiness", new PipelineSetupReadinessDto
        {
            RepositoryInspected = true,
            Checks = [new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingEnvironments, Severity = PipelineSetupReadinessSeverity.Blocking, Items = ["staging"] }]
        });

        await cut.InvokeAsync(() => cut.FindComponent<PipelineSetupReadinessPanel>().Instance.OnChanged.InvokeAsync());

        Assert.Equal(1, ReadinessReads());
        Assert.False(copy.Instance.ReadinessLoading);
        Assert.Equal("staging", Assert.Single(Assert.Single(copy.Instance.Result!.Readiness!.Checks).Items));
        cut.WaitForAssertion(() =>
            Assert.Contains("staging", cut.Find("[data-testid='setup-readiness']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVerdictThatCannotBeReadAgain_KeepsThePreviousOne()
    {
        var (cut, copy) = await CopyAsync();
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/setup/project-readiness", System.Net.HttpStatusCode.ServiceUnavailable);

        await cut.InvokeAsync(() => cut.FindComponent<PipelineSetupReadinessPanel>().Instance.OnChanged.InvokeAsync());

        Assert.Equal(1, ReadinessReads());
        Assert.False(copy.Instance.ReadinessLoading);
        Assert.Equal("prod", Assert.Single(Assert.Single(copy.Instance.Result!.Readiness!.Checks).Items));
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
