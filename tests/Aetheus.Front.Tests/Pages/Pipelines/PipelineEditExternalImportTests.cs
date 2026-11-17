// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R-11: the page of a pipeline imported from an external repository. A read-only badge names the commit, there is no
/// Edit entry and no edit form (the stored definition is shown as it is), and the menu offers Synchronize.
/// </summary>
public class PipelineEditExternalImportTests : BunitContext
{
    private const string Url = "api/projects/7/external-pipelines";
    private const string Imported = "0123456789abcdef0123456789abcdef01234567";
    private const string Head = "89abcdef0123456789abcdef0123456789abcdef";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineEditExternalImportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/variable-libraries/names", new List<string>());
        _handler.SetJsonResponse("api/vaults/names", new List<string>());
        _handler.SetJsonResponse("api/variable-libraries/suggestion-keys", new PaginatedResult<string>());
        _handler.SetJsonResponse("api/servers/names", new List<string>());
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
    }

    private void SetupPipeline(string? importedCommit)
    {
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "oe1-ci",
            ProjectId = 7,
            YamlDefinition = "name: oe1-ci\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual,
            ExternalImportPath = importedCommit is null ? null : ".pipeline/oe1-ci.yaml",
            ExternalImportCommit = importedCommit
        });
    }

    private IRenderedComponent<PipelineEdit> RenderPage(string tab = "runs")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://test/pipelines/5?tab={tab}");
        var cut = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("oe1-ci", StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void ImportedPipeline_ShowsTheReadOnlyBadgeAndNoEditEntry_ButSynchronize()
    {
        SetupPipeline(Imported);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll(".omni-badge"), badge => badge.TextContent.Contains("ReadOnly", StringComparison.Ordinal));
            Assert.Contains("ExternalPipelineImportedTooltip", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Null(cut.FindMenuItem("Edit"));
        Assert.NotNull(cut.FindMenuItem("ExternalPipelineSynchronize"));
    }

    [Fact]
    public void ImportedPipeline_ShowsItsStoredDefinitionInsteadOfTheEditForm()
    {
        SetupPipeline(Imported);

        var cut = RenderPage("edit");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ExternalPipelineReadOnlyNotice", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("stages: []", cut.Find("[data-testid='external-pipeline-definition']").TextContent, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll(".pipeline-edit-form"));
        Assert.Empty(cut.FindAll("input#Name"));
        Assert.Empty(cut.FindAll(".pipeline-edit-toolbar"));
    }

    [Fact]
    public void OwnPipeline_KeepsItsEditFormAndHasNoBadgeNorSynchronize()
    {
        SetupPipeline(importedCommit: null);

        var cut = RenderPage("edit");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pipeline-edit-form")));
        Assert.DoesNotContain(cut.FindAll(".omni-badge"), badge => badge.TextContent.Contains("ReadOnly", StringComparison.Ordinal));
        Assert.NotNull(cut.FindMenuItem("Edit"));
        Assert.Null(cut.FindMenuItem("ExternalPipelineSynchronize"));
    }

    [Fact]
    public void Synchronize_ReimportsFromTheMirrorHead_ThenShowsTheNewCommit()
    {
        SetupPipeline(Imported);
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items =
            [
                new ExternalPipelineCandidateDto
                {
                    RepositoryId = 3,
                    RepositoryName = "oe1",
                    FilePath = ".pipeline/oe1-ci.yaml",
                    Name = "oe1-ci",
                    CommitSha = Head,
                    State = ExternalPipelineImportState.ChangedSinceImport,
                    PipelineId = 5,
                    ImportedCommitSha = Imported
                }
            ]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineDto { Id = 5, Name = "oe1-ci", ExternalImportCommit = Head });
        var cut = RenderPage("edit");
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindMenuItem("ExternalPipelineSynchronize")));

        // The mirror's head is what the page shows after the import: the pipeline is read again.
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "oe1-ci",
            ProjectId = 7,
            YamlDefinition = "name: oe1-ci\ntrigger: manual\nstages: []",
            ExternalImportPath = ".pipeline/oe1-ci.yaml",
            ExternalImportCommit = Head
        });
        cut.FindMenuItem("ExternalPipelineSynchronize")!.Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains(Url, StringComparison.Ordinal));
            Assert.Contains($"\"commitSha\":\"{Head}\"", post.Body, StringComparison.Ordinal);
            Assert.Contains("ExternalPipelineReadOnlyNotice", cut.Markup, StringComparison.Ordinal);
        });
    }
}
