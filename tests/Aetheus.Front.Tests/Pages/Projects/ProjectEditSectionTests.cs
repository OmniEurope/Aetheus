// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public class ProjectEditSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectEditSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=",
            []);
    }

    [Fact]
    public void Renders_FormFields_WhenProjectProvided()
    {
        var project = new ProjectDetailDto
        {
            Id = 1,
            Name = "TestProject",
            Description = "A test project",
            RepositoryUrl = "https://github.com/test",
            DefaultBranch = "main",
            Status = ProjectStatus.Active,
            Tags = ["web", "api"]
        };

        _handler.SetJsonResponse("api/projects/1", project);

        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, project));

        Assert.Contains("TestProject", cut.Markup);
    }

    [Fact]
    public void Renders_AetheusLoader_WhenNoProject()
    {
        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, null));

        // With Project null the section shows the loading placeholder, not the edit form.
        Assert.Contains("aetheus-loader-logo", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Save"));
    }

    [Fact]
    public void Renders_StatusDropdown()
    {
        var project = new ProjectDetailDto
        {
            Id = 2,
            Name = "Project2",
            Status = ProjectStatus.Archived,
            Tags = []
        };

        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, project));

        Assert.Contains("Project2", cut.Markup);
    }

    [Fact]
    public void Renders_SaveButton()
    {
        var project = new ProjectDetailDto
        {
            Id = 3,
            Name = "Project3",
            Tags = []
        };

        _handler.SetJsonResponse("api/projects/3", project);

        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, project));

        // The submit button carries the localized "Save" text and the save icon.
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Save"));
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Save);
    }

    [Fact]
    public void Renders_DeleteButton()
    {
        var project = new ProjectDetailDto
        {
            Id = 4,
            Name = "Project4",
            Tags = ["tag1"]
        };

        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, project));

        // The Delete button carries the localized "Delete" text and the delete icon.
        Assert.Contains(cut.FindAll("button.omni-button"), b => b.TextContent.Contains("Delete"));
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Delete);
    }

    [Fact]
    public void Renders_TagsField_WithValues()
    {
        var project = new ProjectDetailDto
        {
            Id = 5,
            Name = "Project5",
            Tags = ["frontend", "backend", "devops"]
        };

        var cut = Render<ProjectEditSection>(p => p
            .Add(x => x.Project, project));

        Assert.Contains("frontend, backend, devops", cut.Markup);
    }

    [Fact]
    public void InternalRepository_PopulatesDefaultBranchDropdown()
    {
        var project = new ProjectDetailDto
        {
            Id = 6,
            Name = "BranchedProject",
            DefaultBranch = "develop",
            Tags = []
        };
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=6",
            [new GitLightRepoDto { Id = 12, ProjectId = 6, Name = "repo", DefaultBranch = "main" }]);
        _handler.SetPaginatedJsonResponse(
            "api/git/repos/12/branches",
            new[]
            {
                new GitLightBranchDto { Name = "main", IsDefault = true },
                new GitLightBranchDto { Name = "develop" },
                new GitLightBranchDto { Name = "release/next" }
            });

        var cut = Render<ProjectEditSection>(parameters =>
            parameters.Add(component => component.Project, project));

        cut.WaitForAssertion(() =>
        {
            var branchDropdown = Assert.Single(cut.FindComponents<OmniDropDown<string>>());
            Assert.Equal(
                ["main", "develop", "release/next"],
                branchDropdown.Instance.Options.Select(option => option.Value));
            Assert.Equal("develop", branchDropdown.Instance.Value);
        });
    }

    // SITE-A11Y-005: the filterable branch picker was announced as "Select": its label named an id no control
    // carried and the aria-label attribute never reached the input. The combobox input must carry the id the
    // label names and the field name as its accessible name.
    [Fact]
    public void DefaultBranchPicker_IsNamedByItsLabel()
    {
        var project = new ProjectDetailDto { Id = 8, Name = "Named", DefaultBranch = "develop", Tags = [] };
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=8",
            [new GitLightRepoDto { Id = 13, ProjectId = 8, Name = "repo", DefaultBranch = "main" }]);
        _handler.SetPaginatedJsonResponse(
            "api/git/repos/13/branches",
            new[] { new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" } });

        var cut = Render<ProjectEditSection>(parameters => parameters.Add(component => component.Project, project));

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[role=combobox]");
            const string id = "oe-pages-projects-projectdetailsections-projecteditsection-4";
            Assert.Equal(id, input.GetAttribute("id"));
            Assert.Equal("DefaultBranch", input.GetAttribute("aria-label"));
            Assert.Contains("DefaultBranch", cut.Find($"label[for={id}]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MultipleInternalRepositories_DoNotChooseAnArbitraryBranchSource()
    {
        var project = new ProjectDetailDto
        {
            Id = 7,
            Name = "AmbiguousProject",
            DefaultBranch = "release/custom",
            Tags = []
        };
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=7",
            [
                new GitLightRepoDto { Id = 21, ProjectId = 7, Name = "first", DefaultBranch = "main" },
                new GitLightRepoDto { Id = 22, ProjectId = 7, Name = "second", DefaultBranch = "develop" }
            ]);

        var cut = Render<ProjectEditSection>(parameters =>
            parameters.Add(component => component.Project, project));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<OmniDropDown<string>>());
            Assert.Contains("release/custom", cut.Markup);
        });
        Assert.DoesNotContain(
            _handler.Requests,
            request => request.Url.Contains("/branches", StringComparison.Ordinal));
    }

    [Fact]
    public void SameProjectRerender_PreservesBranchBeingEdited()
    {
        var project = new ProjectDetailDto
        {
            Id = 8,
            Name = "StableProject",
            DefaultBranch = "main",
            Tags = []
        };

        var cut = Render<ProjectEditSection>(parameters =>
            parameters.Add(component => component.Project, project));
        cut.Find("input#oe-pages-projects-projectdetailsections-projecteditsection-4").Input("develop");

        cut.Render(parameters => parameters.Add(component => component.Project, project));

        Assert.Equal("develop", cut.Find("input#oe-pages-projects-projectdetailsections-projecteditsection-4").GetAttribute("value"));
    }

    [Fact]
    public void DifferentProjectAndRepositoryDefaults_RenderExplicitWarning()
    {
        var project = new ProjectDetailDto
        {
            Id = 9,
            Name = "MismatchProject",
            DefaultBranch = "develop",
            Tags = []
        };
        _handler.SetPaginatedJsonResponse<GitLightRepoDto>(
            HttpMethod.Get,
            "api/git/repos?page=1&pageSize=100&projectId=9",
            [new GitLightRepoDto { Id = 90, ProjectId = 9, Name = "repo", DefaultBranch = "main" }]);
        _handler.SetPaginatedJsonResponse(
            "api/git/repos/90/branches",
            new[]
            {
                new GitLightBranchDto { Name = "main", IsDefault = true },
                new GitLightBranchDto { Name = "develop" }
            });

        var cut = Render<ProjectEditSection>(parameters =>
            parameters.Add(component => component.Project, project));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("ProjectGitDefaultBranchMismatch", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("develop", cut.Markup, StringComparison.Ordinal);
            var branchDropdown = Assert.Single(cut.FindComponents<OmniDropDown<string>>());
            Assert.Contains("main", branchDropdown.Instance.Options.Select(option => option.Value));
        });
    }
}
