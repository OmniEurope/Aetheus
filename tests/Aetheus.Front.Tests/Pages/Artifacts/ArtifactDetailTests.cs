// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Artifacts;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Artifacts;

public class ArtifactDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ArtifactDetailTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineArtifactDto Sample(int id = 5) => new()
    {
        Id = id,
        Name = "build-output",
        ProjectId = 1,
        ProjectName = "Demo",
        PipelineId = 1,
        PipelineName = "CI",
        PipelineRunId = 42,
        FilePath = "/srv/aetheus/artifacts/build-output.zip",
        SizeBytes = 2_097_152,
        RetentionPolicy = ArtifactRetentionPolicy.Released,
        CreatedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        RetentionExpiresAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        CommitHash = "abcdef1234567890",
        BranchName = "main",
        RepositoryUrl = "https://github.com/acme/demo.git",
        Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
    };

    [Fact]
    public void Renders_Artifact_ShowsChecksum()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample());

        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 5));

        cut.WaitForState(() => cut.Markup.Contains("build-output"));
        Assert.Contains("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", cut.Markup);
    }

    [Fact]
    public void Renders_ArtifactWithoutChecksum_ShowsUnavailableNotFabricated()
    {
        // A metadata-only artifact (no stored bytes) must honestly show "not available",
        // never a placeholder or fabricated hash.
        _handler.SetJsonResponse("api/artifacts/6", Sample(6) with { Sha256 = null });

        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 6));

        cut.WaitForState(() => cut.Markup.Contains("build-output"));
        // Stub localizer echoes keys; the honest "unavailable" branch renders ChecksumUnavailable.
        Assert.Contains("ChecksumUnavailable", cut.Markup);
    }

    [Fact]
    public void Renders_LoadedArtifact_ShowsName()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample());

        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 5));

        cut.WaitForState(() => cut.Markup.Contains("build-output"));
        Assert.Contains("build-output", cut.Markup);
    }

    [Fact]
    public void Renders_NotFound_DoesNotShowArtifactName()
    {
        _handler.SetResponse("api/artifacts/9", HttpStatusCode.NotFound);

        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 9));

        Assert.DoesNotContain("build-output", cut.Markup);
    }

    [Fact]
    public void ArtifactIdChange_ReloadsSameComponentInstance()
    {
        _handler.SetJsonResponse("api/artifacts/1", Sample(1) with { Name = "first-artifact" });
        _handler.SetJsonResponse("api/artifacts/2", Sample(2) with { Name = "second-artifact" });
        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 1));

        cut.Render(p => p.Add(c => c.ArtifactId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-artifact", cut.Markup));
        Assert.DoesNotContain("first-artifact", cut.Markup);
    }

    [Fact]
    public void Panels_RenderProvenanceAndOnlyRenderDistributionWhenItHasContent()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample());
        _handler.SetJsonResponse("api/artifacts/6", Sample(6) with
        {
            ProjectId = null,
            ProjectName = null,
            EnvironmentName = null,
            Releases = []
        });

        var withDistribution = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 5));
        withDistribution.WaitForAssertion(() =>
        {
            Assert.Contains("Provenance", withDistribution.Markup);
            Assert.Contains("Distribution", withDistribution.Markup);
        });

        var withoutDistribution = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 6));
        withoutDistribution.WaitForAssertion(() =>
        {
            Assert.Contains("Provenance", withoutDistribution.Markup);
            Assert.DoesNotContain("Distribution", withoutDistribution.Markup);
        });
    }

    [Fact]
    public void GitProvenanceLinks_UseOnlyCanonicalGitSectionRoutes()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample() with
        {
            Branches = [new BranchLinkDto { Id = 11, Name = "main" }],
            Commits = [new CommitLinkDto { Id = 22, Sha = "abcdef1234567890" }]
        });

        var cut = Render<ArtifactDetail>(parameters => parameters.Add(component => component.ArtifactId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("href=\"/git-repositories/branches/11\"", cut.Markup);
            Assert.Contains("href=\"/git-repositories/commits/22\"", cut.Markup);
            Assert.DoesNotContain("href=\"/git/", cut.Markup);
        });
    }

    [Fact]
    public void GitProvenanceLinks_WithSourceRepository_GoDirectlyToBranchAndCommit()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample() with { SourceRepositoryId = 17 });

        var cut = Render<ArtifactDetail>(parameters => parameters.Add(component => component.ArtifactId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("href=\"/git-repositories/17?tab=branches&amp;branch=main\"", cut.Markup);
            Assert.Contains("href=\"/git-repositories/17?tab=commits&amp;search=abcdef1234567890\"", cut.Markup);
        });
    }

    [Fact]
    public void CopyPathButton_WritesExactArtifactPathToClipboard()
    {
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);
        _handler.SetJsonResponse("api/artifacts/5", Sample());
        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 5));
        cut.WaitForState(() => cut.Markup.Contains("build-output"));

        cut.FindAll("button[aria-label='Copy']")[0].Click();

        var invocation = Assert.Single(
            JSInterop.Invocations,
            call => call.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("/srv/aetheus/artifacts/build-output.zip", invocation.Arguments[0]);
    }

    private async Task<IRenderedComponent<ArtifactDetail>> DownloadAsync()
    {
        _handler.SetJsonResponse("api/artifacts/5", Sample());
        var cut = Render<ArtifactDetail>(p => p.Add(c => c.ArtifactId, 5));
        cut.WaitForState(() => cut.Markup.Contains("build-output"));
        await cut.FindAll("button").First(button => button.TextContent.Contains("Download", StringComparison.Ordinal)).ClickAsync(new());
        return cut;
    }

    [Fact]
    public async Task Download_StreamsTheArchiveToTheBrowserUnderTheArtifactsName()
    {
        _handler.SetRawResponse("api/artifacts/5/download", "zip-bytes");

        var cut = await DownloadAsync();

        var invocation = Assert.Single(JSInterop.Invocations, call => call.Identifier == "downloadFileFromStream");
        Assert.Equal("build-output.zip", invocation.Arguments[0]);
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("api/artifacts/5/download", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("button.btn-busy"));
    }

    [Fact]
    public async Task Download_AFailedResponse_SendsNothingToTheBrowser()
    {
        _handler.SetResponse("api/artifacts/5/download", HttpStatusCode.NotFound);

        var cut = await DownloadAsync();

        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "downloadFileFromStream");
        Assert.Empty(cut.FindAll("button.btn-busy"));
    }
}
