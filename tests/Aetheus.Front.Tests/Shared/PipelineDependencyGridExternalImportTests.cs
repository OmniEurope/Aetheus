// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// R-11: a pipeline imported from an external repository is read-only in the list. It carries a read-only badge
/// whose tooltip names the commit, offers no Edit, and offers Synchronize (a re-import from the mirror's head).
/// </summary>
public class PipelineDependencyGridExternalImportTests : BunitContext
{
    private const string Url = "api/projects/7/external-pipelines";
    private const string Imported = "0123456789abcdef0123456789abcdef01234567";
    private const string Head = "89abcdef0123456789abcdef0123456789abcdef";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineDependencyGridExternalImportTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineDependencyDto Pipeline(int id, string? importedCommit) => new()
    {
        Id = id,
        Name = $"p{id}",
        ProjectId = 7,
        ExternalImportPath = importedCommit is null ? null : $".pipeline/p{id}.yaml",
        ExternalImportCommit = importedCommit
    };

    private static ExternalPipelineCandidateDto Candidate(ExternalPipelineImportState state) => new()
    {
        RepositoryId = 3,
        RepositoryName = "oe1",
        FilePath = ".pipeline/p1.yaml",
        Name = "p1",
        CommitSha = Head,
        State = state,
        PipelineId = 1,
        ImportedCommitSha = Imported,
        Problems = state == ExternalPipelineImportState.Refused ? ["vaults refused"] : []
    };

    private IRenderedComponent<PipelineDependencyGrid> RenderGrid(Action? synchronized = null, bool canWrite = true, params PipelineDependencyDto[] items) =>
        Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, items.ToList())
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.CanWrite, canWrite)
            .Add(component => component.ExternalSynchronized, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => synchronized?.Invoke())));

    [Fact]
    public void ImportedPipeline_ShowsTheReadOnlyBadgeWithItsCommitInTheTooltip_AndOwnRowsDoNot()
    {
        var cut = RenderGrid(items: [Pipeline(1, Imported), Pipeline(2, null)]);

        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll(".omni-badge").Where(badge => badge.TextContent.Contains("ReadOnly", StringComparison.Ordinal)).ToList();
            Assert.Single(badges);
            Assert.Contains("ExternalPipelineImportedTooltip", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ImportedPipeline_HasNoEditEntry_ButSynchronize_WhileAnOwnPipelineHasEditOnly()
    {
        var imported = RenderGrid(items: Pipeline(1, Imported));
        imported.WaitForAssertion(() => Assert.NotEmpty(imported.FindAll("tr")));
        Assert.Null(imported.FindMenuItem("Edit"));
        Assert.NotNull(imported.FindMenuItem("ExternalPipelineSynchronize"));

        var own = RenderGrid(items: Pipeline(2, null));
        own.WaitForAssertion(() => Assert.NotEmpty(own.FindAll("tr")));
        Assert.NotNull(own.FindMenuItem("Edit"));
        Assert.Null(own.FindMenuItem("ExternalPipelineSynchronize"));
    }

    [Fact]
    public void Synchronize_ReimportsTheFileAtTheMirrorHead_ThenAsksTheHostToReload()
    {
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items = [Candidate(ExternalPipelineImportState.ChangedSinceImport)]
        });
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineDto { Id = 1, Name = "p1", ExternalImportCommit = Head });
        var reloaded = 0;
        var cut = RenderGrid(() => reloaded++, items: Pipeline(1, Imported));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr")));

        cut.FindMenuItem("ExternalPipelineSynchronize")!.Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains(Url, StringComparison.Ordinal));
            Assert.Contains($"\"commitSha\":\"{Head}\"", post.Body, StringComparison.Ordinal);
            Assert.Contains("\"filePath\":\".pipeline/p1.yaml\"", post.Body, StringComparison.Ordinal);
            Assert.Contains("\"repositoryId\":3", post.Body, StringComparison.Ordinal);
            Assert.Equal(1, reloaded);
        });
    }

    [Theory]
    [InlineData(ExternalPipelineImportState.Imported)]
    [InlineData(ExternalPipelineImportState.Refused)]
    public void Synchronize_OfAnUnchangedOrRefusedFile_ImportsNothingAndDoesNotReload(ExternalPipelineImportState state)
    {
        _handler.SetJsonResponse(Url, new ExternalPipelineCandidatesDto
        {
            HasExternalRepository = true,
            Items = [Candidate(state)]
        });
        var reloaded = 0;
        var cut = RenderGrid(() => reloaded++, items: Pipeline(1, Imported));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr")));

        cut.FindMenuItem("ExternalPipelineSynchronize")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Method == "GET" && request.Url.Contains(Url, StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST" && request.Url.Contains(Url, StringComparison.Ordinal));
        Assert.Equal(0, reloaded);
    }

    [Fact]
    public void Synchronize_WithoutThePermission_IsDisabled()
    {
        var cut = RenderGrid(canWrite: false, items: Pipeline(1, Imported));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr")));

        var item = cut.FindMenuItem("ExternalPipelineSynchronize");

        Assert.NotNull(item);
        Assert.True(item!.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true");
    }
}
