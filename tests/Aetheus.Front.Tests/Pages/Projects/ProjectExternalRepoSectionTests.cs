// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectExternalRepoSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectExternalRepoSectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void NotAttached_ShowsAttachForm()
    {
        _handler.SetResponse("api/external-repos/project/1", HttpStatusCode.NotFound);

        var cut = Render<ProjectExternalRepoSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("OwnerOrGroup"));
        Assert.Contains("Attach", cut.Markup);
    }

    [Fact]
    public void NoContent_ShowsAttachForm_AndTheSameProjectIsReadOnce()
    {
        // An absent external repository returns 204; re-rendering the same project does not repeat
        // the request.
        _handler.SetResponse("api/external-repos/project/1", HttpStatusCode.NoContent);

        var cut = Render<ProjectExternalRepoSection>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("OwnerOrGroup"));
        cut.Render(p => p.Add(c => c.ProjectId, 1));

        Assert.Single(_handler.Requests, request => request.Url.EndsWith("api/external-repos/project/1", StringComparison.Ordinal));
    }

    [Fact]
    public void Attached_ShowsMirrorStatus()
    {
        _handler.SetJsonResponse("api/external-repos/project/1", new ExternalRepoDto
        {
            ProjectId = 1,
            ProviderType = GitProviderType.GitHub,
            OwnerOrGroup = "acme",
            RepositoryName = "demo",
            MirrorStatus = GitMirrorStatus.Ready
        });

        var cut = Render<ProjectExternalRepoSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("acme"));
        Assert.Contains("Enum_GitMirrorStatus_Ready", cut.Markup);
        Assert.Contains("SyncNow", cut.Markup);
    }

    [Fact]
    public void R534_AnAdditionalSource_SaysItsRole_AndTheYamlAPipelineWrites()
    {
        _handler.SetJsonResponse("api/external-repos/project/1", new ExternalRepoDto
        {
            ProjectId = 1,
            ProviderType = GitProviderType.GitHub,
            OwnerOrGroup = "acme",
            RepositoryName = "demo",
            MirrorStatus = GitMirrorStatus.Ready,
            Slug = "demo-public",
            DefaultBranch = "main",
            IsProjectSource = false
        });

        var cut = Render<ProjectExternalRepoSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("acme"));
        Assert.Contains("ExternalRepoRoleAdditionalSource", cut.Markup);
        Assert.Contains("repository: demo-public", cut.Find("pre").TextContent);
        Assert.Contains("branch: main", cut.Find("pre").TextContent);
        // The hint of a project source (the project clones the mirror) is not the one shown here.
        Assert.DoesNotContain("ExternalRepoCloneHint", cut.Markup);
    }

    [Fact]
    public void R534_TheProjectsOwnExternalSource_KeepsItsCard_WithoutThePipelineYaml()
    {
        _handler.SetJsonResponse("api/external-repos/project/1", new ExternalRepoDto
        {
            ProjectId = 1,
            ProviderType = GitProviderType.GitHub,
            OwnerOrGroup = "acme",
            RepositoryName = "demo",
            MirrorStatus = GitMirrorStatus.Ready,
            Slug = "demo",
            IsProjectSource = true
        });

        var cut = Render<ProjectExternalRepoSection>(p => p.Add(c => c.ProjectId, 1));

        cut.WaitForState(() => cut.Markup.Contains("acme"));
        Assert.Contains("ExternalRepoRoleProjectSource", cut.Markup);
        Assert.Contains("ExternalRepoCloneHint", cut.Markup);
        Assert.Empty(cut.FindAll("pre"));
    }
}
