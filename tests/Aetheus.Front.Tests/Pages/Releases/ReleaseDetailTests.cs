// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Releases;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Releases;

public class ReleaseDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ReleaseDetailTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The project has no repository unless a test says otherwise.
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    private static ReleaseDto Sample(int id = 7) => new()
    {
        Id = id,
        ProjectId = 1,
        ProjectName = "Demo",
        Version = "v2.3.0",
        BranchName = "main",
        DetectedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        PublishedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        BuildNumber = 128,
        CommitHash = "abcdef1234567890",
        TagName = "v2.3.0",
        RepositoryUrl = "https://github.com/acme/demo.git",
        Changelog = "- feat: add dashboard\n- fix: null crash\n- chore: bump deps"
    };

    [Fact]
    public void Renders_LoadedRelease_ShowsVersion()
    {
        _handler.SetJsonResponse("api/releases/7", Sample());

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForState(() => cut.Markup.Contains("v2.3.0"));
        Assert.Contains("v2.3.0", cut.Markup);
    }

    [Fact]
    public void Breadcrumb_ShowsTheShortVersion_LikeEveryOtherReleaseName()
    {
        // D39: a pipeline release is "c-" plus a 40-character SHA; written in full, the breadcrumb
        // pushes the rest of the header off a phone screen.
        var full = "c-0123456789abcdef0123456789abcdef01234567";
        _handler.SetJsonResponse("api/releases/7", Sample() with { Version = full, TagName = null });
        var breadcrumb = Services.GetRequiredService<Aetheus.Front.Layout.BreadcrumbService>();

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForAssertion(() => Assert.Equal(4, breadcrumb.Items.Count));
        Assert.Equal(Aetheus.Front.Components.Releases.ReleaseHelper.ShortVersion(full), breadcrumb.Items[^1].Text);
        Assert.NotEqual(full, breadcrumb.Items[^1].Text);
    }

    [Fact]
    public void Renders_NotFound_DoesNotShowVersion()
    {
        _handler.SetResponse("api/releases/9", HttpStatusCode.NotFound);

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 9));

        Assert.DoesNotContain("v2.3.0", cut.Markup);
    }

    [Fact]
    public void ReleaseIdChange_RequestsSecondReleaseOnSameInstance()
    {
        _handler.SetResponse("api/releases/1", HttpStatusCode.NotFound);
        _handler.SetResponse("api/releases/2", HttpStatusCode.NotFound);
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 1));

        cut.Render(p => p.Add(c => c.ReleaseId, 2));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/releases/1"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/releases/2"));
    }

    [Fact]
    public void Panels_AlwaysRenderSourceAndOnlyRenderDeliverablesWhenPresent()
    {
        _handler.SetJsonResponse("api/releases/7", Sample());
        _handler.SetJsonResponse("api/releases/8", Sample(8) with
        {
            EnvironmentName = "production",
            Artifacts =
            [
                new ArtifactLinkDto
                {
                    Id = 41,
                    Name = "release.zip",
                    SizeBytes = 2048
                }
            ]
        });

        var withoutDeliverables = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));
        withoutDeliverables.WaitForAssertion(() =>
        {
            Assert.Contains("Source", withoutDeliverables.Markup);
            Assert.DoesNotContain("Deliverables", withoutDeliverables.Markup);
        });

        var withDeliverables = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 8));
        withDeliverables.WaitForAssertion(() =>
        {
            Assert.Contains("Source", withDeliverables.Markup);
            Assert.Contains("Deliverables", withDeliverables.Markup);
            Assert.Contains("release.zip", withDeliverables.Markup);
            Assert.Contains("production", withDeliverables.Markup);
        });
    }

    [Fact]
    public void GitProvenanceLinks_UseOnlyCanonicalGitSectionRoutes()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            Branches = [new BranchLinkDto { Id = 11, Name = "main" }],
            Commits = [new CommitLinkDto { Id = 22, Sha = "abcdef1234567890" }]
        });

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("href=\"/git-repositories/branches/11\"", cut.Markup);
            Assert.Contains("href=\"/git-repositories/commits/22\"", cut.Markup);
            Assert.DoesNotContain("href=\"/git/", cut.Markup);
            // The tag leads to the commit it points at, the release's own commit.
            var tag = cut.Find(".short-id[title='v2.3.0'] a.short-id__text");
            Assert.Equal("/git-repositories/commits/22", tag.GetAttribute("href"));
        });
    }

    [Fact]
    public void ABareCommitHash_AndTheTag_StayText_WhenTheRepositoryCannotBeTold()
    {
        // Without a linked commit and with no project repository the release's URL names, the
        // commit and the tag are text: never a list of repositories to search in, never a guess.
        _handler.SetJsonResponse("api/releases/7", Sample());

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("abcdef12", cut.Find(".short-id[title='abcdef1234567890']").TextContent.Trim());
            Assert.Empty(cut.FindAll(".short-id[title='abcdef1234567890'] a"));
            Assert.Empty(cut.FindAll(".short-id[title='v2.3.0'] a"));
        });
    }

    [Fact]
    public void AnInternalCloneUrl_LeadsToTheCommitPage_WhateverHostItWasRehomedTo()
    {
        // An internal repository's clone URL is re-homed per environment (the release recorded the
        // agents' host), so it is matched by project and slug, not by its whole text.
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            RepositoryUrl = "https://host.docker.internal:5301/git/1/demo.git"
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/git/repos",
        [
            new GitLightRepoDto { Id = 4, ProjectId = 1, Slug = "other", CloneUrl = "https://localhost:5301/git/1/other.git" },
            new GitLightRepoDto { Id = 6, ProjectId = 1, Slug = "demo", CloneUrl = "https://localhost:5301/git/1/demo.git" }
        ]);

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() => Assert.Equal("/git-repositories/6/commits/abcdef1234567890",
            cut.Find(".short-id[title='abcdef1234567890'] a.short-id__text").GetAttribute("href")));
    }

    [Fact]
    public void ABareCommitHash_AndTheTag_LeadToTheCommitPage_WhenTheRepositoryIsKnown()
    {
        // The project's repository cloned from the release's URL holds the commit, so the link goes
        // to the commit itself rather than to the list of repositories.
        _handler.SetJsonResponse("api/releases/7", Sample());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/git/repos",
        [
            new GitLightRepoDto { Id = 5, CloneUrl = "https://github.com/acme/other.git" },
            new GitLightRepoDto { Id = 9, CloneUrl = "https://github.com/acme/demo" }
        ]);

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("/git-repositories/9/commits/abcdef1234567890",
                cut.Find(".short-id[title='abcdef1234567890'] a.short-id__text").GetAttribute("href"));
            Assert.Equal("/git-repositories/9/commits/abcdef1234567890",
                cut.Find(".short-id[title='v2.3.0'] a.short-id__text").GetAttribute("href"));
        });
    }

    [Fact]
    public void ChangelogLines_AreTypedByTheirConventionalPrefix_AndAFarColonStaysInTheText()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            Changelog = "* docs: readme\n  - refactor(core): split\nperf: faster\nTEST: more cases\n"
                + "feature flags are documented in the guide: see there\nPlain note\n-  \n"
        });

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll(".release-cl-text").Count));
        Assert.Equal(
            ["readme", "split", "faster", "more cases", "feature flags are documented in the guide: see there", "Plain note"],
            cut.FindAll(".release-cl-text").Select(text => text.TextContent));
        Assert.Equal(
            ["docs", "refactor", "perf", "test", "feat"],
            cut.FindAll(".release-cl-badge").Select(badge => badge.TextContent.Trim()));
    }

    [Fact]
    public void AChangelogOfBulletsOnly_IsShownAsWritten()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with { Changelog = " -- \n * " });

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() => Assert.Equal("--", cut.Find(".release-cl-text").TextContent.Split('\n')[0].Trim()));
        Assert.Empty(cut.FindAll(".release-cl-badge"));
    }

    [Fact]
    public void EmptyExplicitChangelog_FallsBackToLinkedCommitMessages()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            Changelog = null,
            Commits = [new CommitLinkDto { Id = 22, Sha = "abcdef1234567890", Message = "fix: repair installer" }]
        });

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Changelog", cut.Markup);
            Assert.Contains("repair installer", cut.Markup);
        });
    }

    [Fact]
    public void EmptyCommitLinkMessages_LoadInOneBatchForChangelog()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            Changelog = null,
            RepositoryUrl = "https://example.test/repo.git",
            Commits =
            [
                new CommitLinkDto { Id = 22, Sha = "abcdef1234567890" },
                new CommitLinkDto { Id = 23, Sha = "1234567890abcdef" }
            ]
        });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 17, ProjectId = 1, CloneUrl = "https://example.test/repo.git" }
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/git/repos/17/commit-messages", new Dictionary<string, string>
        {
            ["abcdef1234567890"] = "feat: show linked commit changelog",
            ["1234567890abcdef"] = "fix: batch lookup"
        });

        var cut = Render<ReleaseDetail>(parameters => parameters.Add(component => component.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("show linked commit changelog", cut.Markup);
            Assert.Contains("batch lookup", cut.Markup);
            Assert.Single(_handler.Requests, request =>
                request.Method == "POST" && request.Url.Contains("commit-messages", StringComparison.Ordinal));
            Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/gitgraph/commits/", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ReleaseStatusChanged_UpdatesMatchingReleaseWithoutReload()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with { Status = ReleaseStatus.Building });
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));
        cut.WaitForAssertion(() => Assert.Contains("Building", cut.Markup));
        var requestCount = _handler.Requests.Count;
        await cut.InvokeAsync(() =>
            cut.Instance.OnReleaseChanged(Sample() with { Status = ReleaseStatus.Deployed }));

        cut.WaitForAssertion(() => Assert.Contains("Deployed", cut.Markup));
        Assert.Equal(requestCount, _handler.Requests.Count);
    }

    private static ReleaseRunRefDto RunRef(int runId, int pipelineId, string pipelineName) => new()
    {
        RunId = runId,
        BuildNumber = runId,
        PipelineId = pipelineId,
        PipelineName = pipelineName,
        Status = PipelineStatus.Success,
        StartedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Provenance_ShowsTheCreatorApartFromLaterUses_AndBuildInputsApartFromDeliverables()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with { PipelineRunId = 300 });
        _handler.SetJsonResponse("api/releases/7/provenance", new ReleaseProvenanceDto
        {
            CreatedBy = RunRef(100, 2, "candidate"),
            BuildRuns = [RunRef(99, 1, "build")],
            Uses = [new ReleaseUseDto { Run = RunRef(300, 3, "deploy-prod"), Kind = ReleaseUseKind.Deploy }],
            ArtifactInputs =
            [
                new ReleaseArtifactInputDto
                {
                    ConsumerRunId = 100, StepName = "restore", Kind = ArtifactInputKind.Restore, ArtifactId = 55,
                    ArtifactName = "base-image", Sha256 = new string('c', 64), SourcePipelineRunId = 50
                }
            ],
            Packages = [new ReleasePackageInputDto { Name = "Serilog", Version = "4.0.0", PackageUrl = "pkg:nuget/Serilog@4.0.0", IsDirect = true, PipelineRunId = 100 }],
            PackageTotalCount = 1
        });

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ReleaseCreatedByPipeline", cut.Markup);
            Assert.Contains("href=\"/pipelines/2\"", cut.Markup);
            Assert.Contains("href=\"/pipelines/runs/100\"", cut.Markup);
            Assert.Contains("ReleaseDeliverablesBuiltBy", cut.Markup);
            Assert.Contains("href=\"/pipelines/runs/99\"", cut.Markup);
            Assert.Contains("ReleaseUses", cut.Markup);
            Assert.Contains("href=\"/pipelines/runs/300\"", cut.Markup);
            Assert.Contains("ReleaseBuildInputs", cut.Markup);
            Assert.Contains("href=\"/artifacts/55\"", cut.Markup);
            // The consumed artifact's short digest links to that artifact too.
            var digest = cut.Find($".short-id[title='{new string('c', 64)}'] a.short-id__text");
            Assert.Equal("/artifacts/55", digest.GetAttribute("href"));
            Assert.Equal("cccccccc", digest.TextContent);
            Assert.Contains("href=\"/pipelines/runs/50\"", cut.Markup);
            Assert.Contains("pkg:nuget/Serilog@4.0.0", cut.Markup);
            // The deployment that last recorded the release is a use, not a creation.
            Assert.DoesNotContain("ReleaseLastRecordedBy", cut.Markup);
        });
    }

    [Fact]
    public void Provenance_WithoutRecordedCreator_SaysSo_AndLabelsTheLastRecordingRunAsSuch()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with { PipelineRunId = 300 });
        _handler.SetJsonResponse("api/releases/7/provenance", new ReleaseProvenanceDto
        {
            LastRecordedBy = RunRef(300, 3, "deploy-prod")
        });

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ReleaseCreatorNotRecorded", cut.Markup);
            Assert.Contains("ReleaseLastRecordedBy", cut.Markup);
            Assert.Contains("href=\"/pipelines/runs/300\"", cut.Markup);
            Assert.DoesNotContain("ReleaseCreatedByPipeline", cut.Markup);
            Assert.Contains("ReleaseUsesEmpty", cut.Markup);
            Assert.Contains("ReleaseInputPackagesEmpty", cut.Markup);
        });
    }

    /// <summary>Decision D2: an archived release says what it lost and kept, when it was archived, and
    /// offers the relaunch from its commit.</summary>
    [Fact]
    public void AnArchivedRelease_ReadsAsArchived_AndOffersTheRelaunchFromItsCommit()
    {
        _handler.SetJsonResponse("api/releases/7", Sample() with
        {
            Status = ReleaseStatus.Archived,
            ArchivedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            CommitHash = "0123456789abcdef0123456789abcdef01234567"
        });

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ReleaseArchivedTitle", cut.Markup);
            Assert.Contains("ReleaseArchivedHint", cut.Markup);
            Assert.Contains("ReleaseArchivedAt", cut.Markup);
            Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Rerun", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void AReleaseThatIsNotArchived_ShowsNeitherTheArchiveNoticeNorTheRelaunch()
    {
        _handler.SetJsonResponse("api/releases/7", Sample());

        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, 7));

        cut.WaitForState(() => cut.Markup.Contains("v2.3.0"));
        Assert.DoesNotContain("ReleaseArchivedTitle", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Rerun", StringComparison.Ordinal));
    }
}
