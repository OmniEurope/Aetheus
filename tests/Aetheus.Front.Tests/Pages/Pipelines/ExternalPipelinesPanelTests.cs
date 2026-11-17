// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-11: the external repository pipelines of a project, with a read-only badge for the imported ones.</summary>
public class ExternalPipelinesPanelTests : BunitContext
{
    private const string Url = "api/projects/7/external-pipelines";
    private readonly BunitTestHelper.TestHandler _handler;

    public ExternalPipelinesPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ExternalPipelineCandidateDto Item(string name, ExternalPipelineImportState state, int? pipelineId = null) => new()
    {
        RepositoryId = 3,
        RepositoryName = "oe1",
        FilePath = $".pipeline/{name}.yaml",
        Name = name,
        CommitSha = "0123456789abcdef0123456789abcdef01234567",
        State = state,
        PipelineId = pipelineId
    };

    [Fact]
    public void ProjectWithoutExternalRepository_RendersNothing()
    {
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto());

        var cut = Render<ExternalPipelinesPanel>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".external-pipelines-panel")));
    }

    [Fact]
    public void ImportedPipeline_ShowsTheReadOnlyBadge_AndNoImportButton()
    {
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items = [Item("oe1-ci", ExternalPipelineImportState.Imported, pipelineId: 12)]
        });

        var cut = Render<ExternalPipelinesPanel>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".external-pipelines-row");
            Assert.Contains("ExternalPipelineStateImported", row.QuerySelector(".external-pipelines-state")!.TextContent, StringComparison.Ordinal);
            Assert.Null(row.QuerySelector(".external-pipelines-import"));
            Assert.Equal("pipelines/12", row.QuerySelector("a.external-pipelines-name")!.GetAttribute("href"));
            Assert.Contains("ExternalPipelinesHint", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AvailableAndChangedFiles_OfferAnImport_RefusedOnesListTheirReasons()
    {
        var refused = Item("oe1-deploy", ExternalPipelineImportState.Refused) with { Problems = ["vaults refused"] };
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items = [Item("oe1-ci", ExternalPipelineImportState.Available), Item("oe1-qa", ExternalPipelineImportState.ChangedSinceImport, 13), refused]
        });

        var cut = Render<ExternalPipelinesPanel>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".external-pipelines-row");
            Assert.Equal(3, rows.Count);
            Assert.Contains("ExternalPipelinesImport", rows[0].QuerySelector(".external-pipelines-import")!.TextContent, StringComparison.Ordinal);
            Assert.Contains("ExternalPipelinesReimport", rows[1].QuerySelector(".external-pipelines-import")!.TextContent, StringComparison.Ordinal);
            Assert.Null(rows[2].QuerySelector(".external-pipelines-import"));
            Assert.Contains("vaults refused", rows[2].TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ImportButton_PostsTheFileAtTheListedCommit()
    {
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items = [Item("oe1-ci", ExternalPipelineImportState.Available)]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineDto { Id = 12, Name = "oe1-ci" });
        var cut = Render<ExternalPipelinesPanel>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForElement(".external-pipelines-import").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains(Url, StringComparison.Ordinal));
            Assert.Contains("\"filePath\":\".pipeline/oe1-ci.yaml\"", post.Body, StringComparison.Ordinal);
            Assert.Contains("\"commitSha\":\"0123456789abcdef0123456789abcdef01234567\"", post.Body, StringComparison.Ordinal);
        });
    }
}
