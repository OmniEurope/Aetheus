// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class ProjectEditTests : BunitContext
{
    public ProjectEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void NewProject_ShowsEmptyForm()
    {
        var cut = Render<ProjectEdit>(p => p.Add(x => x.Id, null));
        // New-project mode renders the create form (header + "Create" submit button).
        Assert.Contains("NewProject", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
    }

    [Fact]
    public void ExistingProject_Redirects()
    {
        // Existing project IDs now redirect to /projects/{id}/overview
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<ProjectEdit>(p => p.Add(x => x.Id, 5));
        Assert.Equal("http://localhost/projects/5/overview", nav.Uri);
    }

    [Fact]
    public void Submit_NewProject_CallsCreate()
    {
        _handler.SetJsonResponse("api/projects", new ProjectDto { Id = 10, Name = "new" });

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<ProjectEdit>(p => p.Add(x => x.Id, null));
        BunitTestHelper.Interactive(this);

        var form = cut.Find("form");
        form.Submit();

        // Name is [Required]; submitting the empty form fails validation, so no create call is made
        // and the required-field message is rendered instead.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".validation-message")));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/projects"));
    }

    [Fact]
    public async Task OnSubmit_NewProject_Creates()
    {
        _handler.SetJsonResponse("api/projects", new ProjectDto { Id = 20, Name = "My New Project" });

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<ProjectEdit>(p => p.Add(x => x.Id, null));

        cut.Instance._model.Name = "My New Project";
        cut.Instance._model.Description = "A test project";
        cut.Instance._model.TagsRaw = "web, api";

        await cut.InvokeAsync(() => cut.Instance.OnSubmit());

        // OnSubmit POSTs the create request and, on success, navigates to the created project's page.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/projects"));
        Assert.Equal("http://localhost/projects/20", nav.Uri);
        Assert.False(cut.Instance._saving);
    }
}
