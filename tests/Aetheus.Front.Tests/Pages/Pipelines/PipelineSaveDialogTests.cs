// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R-95: the review before a save compares against the repository's own file, so Aetheus never
/// shows two identical texts and then overwrites lines pushed to the repository that it has not seen.
/// </summary>
public class PipelineSaveDialogTests : BunitContext
{
    private const string AetheusYaml = "name: deploy\ntrigger: manual\n";
    private const string RepositoryYaml = "name: deploy\ntrigger: manual\nvariables:\n  COMPOSE_FILE: compose.yml\n";
    private const string RepositoryHash = "feedbeef";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSaveDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<PipelineSaveDialog> RenderDialog(string draftYaml)
    {
        var cut = Render<PipelineSaveDialog>(p => p
            .Add(x => x.PipelineId, 5)
            .Add(x => x.PipelineName, "deploy")
            .Add(x => x.SavedYaml, AetheusYaml)
            .Add(x => x.DraftYaml, draftYaml)
            .Add(x => x.SourceBranch, "develop"));
        cut.WaitForState(() => !cut.Markup.Contains("PipelineSaveRepositoryLoading", StringComparison.Ordinal));
        return cut;
    }

    private static void ClickSave(IRenderedComponent<PipelineSaveDialog> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Contains("Save", StringComparison.Ordinal)).Click();

    [Fact]
    public void AnUneditedDefinition_ComparesAetheusCopyWithTheRepositoryFile_AndCommitsNothing()
    {
        _handler.SetJsonResponse("api/pipelines/5/repository-definition", new PipelineRepositoryDefinitionDto
        {
            Exists = true,
            Yaml = RepositoryYaml,
            Sha256 = RepositoryHash
        });

        var cut = RenderDialog(AetheusYaml);

        Assert.Contains("PipelineSaveRepositoryFileKept", cut.Markup);
        var diff = cut.FindComponent<OmniDiffViewer>().Instance;
        Assert.Equal(AetheusYaml, diff.Original);
        Assert.Equal(RepositoryYaml, diff.Modified);
        ClickSave(cut);
        Assert.Null(((PipelineSaveDecision)Dialog.LastCloseResult!).ConfirmedRepositoryYamlSha256);
    }

    [Fact]
    public void AnEditOverAChangedRepositoryFile_ShowsTheRealDiff_AndQuotesTheReviewedFile()
    {
        _handler.SetJsonResponse("api/pipelines/5/repository-definition", new PipelineRepositoryDefinitionDto
        {
            Exists = true,
            Yaml = RepositoryYaml,
            Sha256 = RepositoryHash
        });
        const string draft = AetheusYaml + "stages: []\n";

        var cut = RenderDialog(draft);

        Assert.Contains("PipelineSaveRepositoryChanged", cut.Markup);
        var diff = cut.FindComponent<OmniDiffViewer>().Instance;
        Assert.Equal(RepositoryYaml, diff.Original);
        Assert.Equal(draft, diff.Modified);
        ClickSave(cut);
        var decision = (PipelineSaveDecision)Dialog.LastCloseResult!;
        Assert.Equal("develop", decision.SourceBranch);
        Assert.Equal(RepositoryHash, decision.ConfirmedRepositoryYamlSha256);
    }

    [Fact]
    public void AnEditWhenTheRepositoryHoldsWhatAetheusSynced_IsTheOrdinaryReview()
    {
        _handler.SetJsonResponse("api/pipelines/5/repository-definition", new PipelineRepositoryDefinitionDto
        {
            Exists = true,
            Yaml = AetheusYaml,
            Sha256 = RepositoryHash
        });
        const string draft = AetheusYaml + "stages: []\n";

        var cut = RenderDialog(draft);

        Assert.Contains("PipelineSaveCommitPreview", cut.Markup);
        var diff = cut.FindComponent<OmniDiffViewer>().Instance;
        Assert.Equal(AetheusYaml, diff.Original);
        Assert.Equal(draft, diff.Modified);
        ClickSave(cut);
        Assert.Null(((PipelineSaveDecision)Dialog.LastCloseResult!).ConfirmedRepositoryYamlSha256);
    }

    [Fact]
    public void ARepositoryFileThatCannotBeRead_IsSaidSo_NotGuessed()
    {
        _handler.SetResponse("api/pipelines/5/repository-definition", System.Net.HttpStatusCode.InternalServerError);

        var cut = RenderDialog(AetheusYaml + "stages: []\n");

        Assert.Contains("PipelineSaveRepositoryUnreadable", cut.Markup);
    }
}
