// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineTemplatePagesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineTemplatePagesTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void ExtractPage_LoadsPipelineAndBuildsPinnedDefinition()
    {
        _handler.SetJsonResponse("api/pipelines/3", new PipelineDto
        {
            Id = 3,
            Name = "ci",
            YamlDefinition = "name: ci\nstages: []"
        });

        var cut = Render<PipelineTemplateExtract>(parameters => parameters.Add(component => component.PipelineId, 3));
        cut.WaitForState(() => cut.FindComponents<MonacoEditor>().Count == 2);

        Assert.Equal("name: ci\nstages: []", cut.FindComponents<MonacoEditor>()[0].Instance.Value);
        Assert.Contains("extends: 'ci@1'", cut.FindComponents<MonacoEditor>()[1].Instance.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("rz-dialog", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotePage_ShowsResolvedDiffWithoutDialog()
    {
        MockPipeline(4);
        _handler.SetJsonResponse("promote-template/preview", new PipelinePromotePreviewDto
        {
            PipelineId = 4,
            TemplateId = 7,
            TemplateName = "ci",
            LatestVersion = 2,
            LatestTemplateYaml = "name: old",
            EffectivePipelineYaml = "name: new"
        });

        var cut = Render<PipelineTemplatePromote>(parameters => parameters.Add(component => component.PipelineId, 4));
        cut.WaitForState(() => cut.FindComponents<OmniDiffViewer>().Count == 1);

        Assert.Equal("name: old", cut.FindComponent<OmniDiffViewer>().Instance.Original);
        Assert.Equal("name: new", cut.FindComponent<OmniDiffViewer>().Instance.Modified);
        Assert.DoesNotContain("rz-dialog", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatePage_RequiresOrphanAcknowledgement()
    {
        MockPipeline(5);
        _handler.SetJsonResponse("fleet-update/preview", new PipelineFleetUpdatePreviewDto
        {
            PipelineId = 5,
            TemplateName = "ci",
            CurrentVersion = 1,
            TargetVersion = 2,
            CurrentResolvedYaml = "name: old",
            TargetResolvedYaml = "name: new",
            OrphanOverrides = ["stage:removed"]
        });

        var cut = Render<PipelineTemplateUpdate>(parameters => parameters
            .Add(component => component.PipelineId, 5)
            .Add(component => component.TargetVersion, 2));
        cut.WaitForState(() => cut.FindComponents<OmniDiffViewer>().Count == 1);

        Assert.Contains("OrphanOverridesWarning", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button[disabled]"), button => button.TextContent.Contains("ConfirmUpdate", StringComparison.Ordinal));
        Assert.DoesNotContain("rz-dialog", cut.Markup, StringComparison.Ordinal);
    }

    // Recette R-100, R-101, R-102: the comparison takes the height the page leaves it, steps from one change to the
    // next with OE's own navigation bar, and runs through the frame module, as Monaco cannot run under the strict CSP.
    [Fact]
    public void UpdatePage_DiffFillsThePage_NavigatesBetweenChanges_AndRunsInTheFrame()
    {
        MockPipeline(5);
        _handler.SetJsonResponse("fleet-update/preview", new PipelineFleetUpdatePreviewDto
        {
            PipelineId = 5,
            TemplateName = "ci",
            CurrentVersion = 1,
            TargetVersion = 2,
            CurrentResolvedYaml = "name: old",
            TargetResolvedYaml = "name: new"
        });

        var cut = Render<PipelineTemplateUpdate>(parameters => parameters
            .Add(component => component.PipelineId, 5)
            .Add(component => component.TargetVersion, 2));
        cut.WaitForState(() => cut.FindComponents<OmniDiffViewer>().Count == 1);

        var diff = cut.FindComponent<OmniDiffViewer>().Instance;
        Assert.True(diff.FillAvailableHeight);
        Assert.True(diff.ShowNavigation);
        Assert.Equal(EditorModules.Frame, diff.InteropModulePath);
        Assert.Equal("yaml", diff.Language);
        Assert.NotNull(cut.Find(".page-fill-body .omni-diff-viewer--fill"));
        Assert.NotNull(cut.Find(".omni-diff-viewer__previous"));
        Assert.NotNull(cut.Find(".omni-diff-viewer__next"));
    }

    [Fact]
    public void HistoryPage_RendersVersionNumberInsteadOfLiteralExpression()
    {
        // SITE-FUNC-TEMPLATE-001: `v@version.Version` was parsed as an e-mail address.
        _handler.SetJsonResponse("api/pipelines/templates/7", new PipelineTemplateDto { Id = 7, Name = "ci", Version = 1 });
        _handler.SetJsonResponse("api/pipelines/templates/7/versions", new PaginatedResult<PipelineTemplateVersionSummaryDto>
        {
            Items = [new PipelineTemplateVersionSummaryDto { Version = 4, ChangelogEntry = "fourth" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 10
        });
        var cut = Render<PipelineTemplateHistory>(parameters => parameters.Add(component => component.TemplateId, 7));
        cut.WaitForAssertion(() => Assert.Contains("fourth", cut.Markup, StringComparison.Ordinal));

        Assert.Contains(">v4<", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v@version", cut.Markup, StringComparison.Ordinal);
    }

    private void MockPipeline(int id) => _handler.SetJsonResponse($"api/pipelines/{id}", new PipelineDto
    {
        Id = id,
        Name = "ci",
        ProjectId = 9,
        ProjectName = "Aetheus"
    });
}
