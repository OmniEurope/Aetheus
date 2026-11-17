// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Shared.Components.DependencyUpdates;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>PLAN-007 (recette R-9): the Quality page shows each automatic dependency update with its runs or its reason, and the
/// project settings switch writes the setting at once. The HTTP side is a test handler: the real endpoint is not exercised here.</summary>
public sealed class ProjectDependencyUpdatesTests : BunitContext
{
    private const string OverviewUrl = "api/projects/4/dependency-updates";

    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDependencyUpdatesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static DependencyUpdatesOverviewDto Overview(DependencyUpdateStatus status, string? reason, params DependencyUpdateRunDto[] runs) =>
        new()
        {
            Enabled = true,
            Items =
            [
                new DependencyUpdateDto
                {
                    Id = 1, Ecosystem = "NuGet", PackageName = "Newtonsoft.Json", FromVersion = "13.0.1", TargetVersion = "13.0.3",
                    Advisory = "GHSA-xxxx", Status = status, Reason = reason, License = "MIT", LicenseOfTargetVersion = true,
                    Branch = status == DependencyUpdateStatus.BranchPushed ? "aetheus/deps/Newtonsoft.Json-13.0.3" : null,
                    RepositoryId = 7, CommitSha = status == DependencyUpdateStatus.BranchPushed ? "abc123" : null, Runs = runs
                }
            ]
        };

    [Fact]
    public void ThePanel_ShowsTheBranchAndLinksEachRun()
    {
        _handler.SetJsonResponse(HttpMethod.Get, OverviewUrl, Overview(DependencyUpdateStatus.BranchPushed, null,
            new DependencyUpdateRunDto { PipelineId = 11, PipelineName = "app-ci", RunId = 1100, Status = PipelineStatus.Success },
            new DependencyUpdateRunDto { PipelineId = 14, PipelineName = "app-qa" }));

        var cut = Render<ProjectDependencyUpdatesPanel>(p => p.Add(x => x.ProjectId, 4));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='dependency-update-item']")));
        var item = cut.Find("[data-testid='dependency-update-item']");
        Assert.Contains("Newtonsoft.Json", item.TextContent, StringComparison.Ordinal);
        // The test localizer gives the key back (no {0}), so the branch line shows by its key.
        Assert.Contains("DependencyUpdateBranch", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("DependencyUpdateStatusBranchPushed", item.TextContent, StringComparison.Ordinal);
        Assert.Equal("/pipelines/runs/1100", Assert.Single(item.QuerySelectorAll("a")).GetAttribute("href"));
        // The QA row has no run yet (it starts when the CI succeeds), and no candidate is ever listed.
        Assert.Contains("app-qa", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("DependencyUpdateRunNotFound", item.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate", item.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePanel_MarksAMajorUpdateWithABranchAsNeedingReview()
    {
        _handler.SetJsonResponse(HttpMethod.Get, OverviewUrl, new DependencyUpdatesOverviewDto
        {
            Enabled = true,
            Items =
            [
                new DependencyUpdateDto
                {
                    Id = 2, Ecosystem = "NuGet", PackageName = "Newtonsoft.Json", FromVersion = "13.0.1", TargetVersion = "14.0.0",
                    Advisory = "GHSA-xxxx", Status = DependencyUpdateStatus.BranchPushed, IsMajorUpgrade = true,
                    Branch = "aetheus/deps/Newtonsoft.Json-14.0.0", RepositoryId = 7, CommitSha = "abc123"
                },
                new DependencyUpdateDto
                {
                    Id = 3, Ecosystem = "NuGet", PackageName = "Other", FromVersion = "1.0.0", TargetVersion = "2.0.0",
                    Advisory = "GHSA-yyyy", Status = DependencyUpdateStatus.Refused, IsMajorUpgrade = true, Reason = "No license."
                },
                new DependencyUpdateDto
                {
                    Id = 4, Ecosystem = "NuGet", PackageName = "Patch", FromVersion = "1.0.0", TargetVersion = "1.0.1",
                    Advisory = "GHSA-zzzz", Status = DependencyUpdateStatus.BranchPushed, Branch = "aetheus/deps/Patch-1.0.1"
                }
            ]
        });

        var cut = Render<ProjectDependencyUpdatesPanel>(p => p.Add(x => x.ProjectId, 4));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='dependency-update-item']").Count));
        var items = cut.FindAll("[data-testid='dependency-update-item']");
        var withBranch = Assert.Single(items[0].QuerySelectorAll("[data-testid='dependency-update-major']"));
        Assert.Contains("DependencyUpdateMajorReview", withBranch.TextContent, StringComparison.Ordinal);
        // A refused major is shown as a major but not as waiting for a review: nothing was written.
        var refused = Assert.Single(items[1].QuerySelectorAll("[data-testid='dependency-update-major']"));
        Assert.Equal("DependencyUpdateMajor", refused.TextContent.Trim());
        Assert.Empty(items[2].QuerySelectorAll("[data-testid='dependency-update-major']"));
    }

    [Fact]
    public void ThePanel_SaysWhyNothingWasWritten()
    {
        _handler.SetJsonResponse(HttpMethod.Get, OverviewUrl,
            Overview(DependencyUpdateStatus.Refused, "The license GPL-3.0-only does not allow commercial resale."));

        var cut = Render<ProjectDependencyUpdatesPanel>(p => p.Add(x => x.ProjectId, 4));

        cut.WaitForAssertion(() => Assert.Contains("GPL-3.0-only",
            cut.Find("[data-testid='dependency-update-reason']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePanel_SaysWhenItCannotRead()
    {
        _handler.SetResponse(HttpMethod.Get, OverviewUrl, HttpStatusCode.InternalServerError);

        var cut = Render<ProjectDependencyUpdatesPanel>(p => p.Add(x => x.ProjectId, 4));

        cut.WaitForAssertion(() => Assert.Contains("DependencyUpdatesUnavailable", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingsSwitch_WritesTheSettingAtOnce()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        var cut = Render<ProjectEditSection>(p => p.Add(x => x.Project, new ProjectDetailDto { Id = 4, Name = "Atlas", DefaultBranch = "main" }));
        // The dependency updates switch comes first; the migration gate switch (R-13) follows it.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniSwitch<bool>>().Count));
        Assert.True(cut.FindComponents<OmniSwitch<bool>>()[0].Instance.Value);

        cut.InvokeAsync(() => cut.FindComponents<OmniSwitch<bool>>()[0].Instance.ValueChanged.InvokeAsync(false));

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == HttpMethod.Put.Method
            && request.Url.EndsWith("api/projects/4/dependency-updates/settings", StringComparison.OrdinalIgnoreCase)
            && (request.Body ?? string.Empty).Contains("\"enabled\":false", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>R-13: the migration gate (expand/contract model) can be turned on outside the wizard, from the project settings; it
    /// starts from the project's own value and tells the page to reload the project the overview reads.</summary>
    [Fact]
    public void TheMigrationGateSwitch_StartsFromTheProject_AndWritesTheSettingAtOnce()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        _handler.SetResponse(HttpMethod.Put, "api/projects/4/migration-gate", HttpStatusCode.NoContent);
        var changed = 0;
        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, new ProjectDetailDto { Id = 4, Name = "Atlas", DefaultBranch = "main", MigrationGateEnabled = false })
            .Add(x => x.OnMigrationGateChanged, () => changed++));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniSwitch<bool>>().Count));
        var gate = cut.FindComponents<OmniSwitch<bool>>()[1];
        Assert.False(gate.Instance.Value);

        cut.InvokeAsync(() => gate.Instance.ValueChanged.InvokeAsync(true));

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == HttpMethod.Put.Method
            && request.Url.EndsWith("api/projects/4/migration-gate", StringComparison.OrdinalIgnoreCase)
            && (request.Body ?? string.Empty).Contains("\"enabled\":true", StringComparison.OrdinalIgnoreCase)));
        cut.WaitForAssertion(() => Assert.Equal(1, changed));
        Assert.True(cut.FindComponents<OmniSwitch<bool>>()[1].Instance.Value);
    }

    [Fact]
    public void TheMigrationGateSwitch_KeepsItsValue_WhenTheServerRefuses()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        _handler.SetResponse(HttpMethod.Put, "api/projects/4/migration-gate", HttpStatusCode.InternalServerError);
        var changed = 0;
        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, new ProjectDetailDto { Id = 4, Name = "Atlas", DefaultBranch = "main", MigrationGateEnabled = false })
            .Add(x => x.OnMigrationGateChanged, () => changed++));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<OmniSwitch<bool>>().Count));

        cut.InvokeAsync(() => cut.FindComponents<OmniSwitch<bool>>()[1].Instance.ValueChanged.InvokeAsync(true));

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == HttpMethod.Put.Method
            && request.Url.EndsWith("api/projects/4/migration-gate", StringComparison.OrdinalIgnoreCase)));
        Assert.False(cut.FindComponents<OmniSwitch<bool>>()[1].Instance.Value);
        Assert.Equal(0, changed);
    }
}
