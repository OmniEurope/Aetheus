// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Environments;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests;

public class EnvironmentEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public EnvironmentEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupDefaultResponses()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "P1" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 1, Name = "web-01" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
    }

    [Fact]
    public void Renders_NewEnvironmentForm()
    {
        SetupDefaultResponses();

        var cut = Render<EnvironmentEdit>();

        Assert.Contains("NewEnvironment", cut.Markup);
    }

    [Fact]
    public void Renders_EditMode_LoadsExisting()
    {
        SetupDefaultResponses();
        _handler.SetJsonResponse("api/environments/1", new EnvironmentDto
        {
            Id = 1,
            Name = "Prod",
            Description = "Production env",
            Type = EnvironmentType.Production,
            Servers = []
        });

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));

        // Edit mode renders the loaded environment's name in the header.
        Assert.Contains("Prod", cut.Markup);
    }

    [Fact]
    public void EditMode_UnknownEnvironment_ShowsNotFoundInsteadOfTheLoader()
    {
        // FRONT-001: a 404 used to leave _detail null, so the page spun on the loader forever.
        SetupDefaultResponses();
        _handler.SetResponse(HttpMethod.Get, "api/environments/404", System.Net.HttpStatusCode.NotFound);

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 404));

        cut.WaitForAssertion(() => Assert.Contains("EnvironmentNotFound", cut.Markup));
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void EditMode_LoadFailure_ShowsTheFailureInsteadOfTheLoader()
    {
        SetupDefaultResponses();
        _handler.SetResponse(HttpMethod.Get, "api/environments/500", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 500));

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup));
        Assert.DoesNotContain("EnvironmentNotFound", cut.Markup);
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void Renders_FormFields()
    {
        SetupDefaultResponses();

        var cut = Render<EnvironmentEdit>();

        Assert.Contains("Name", cut.Markup);
        Assert.Contains("Description", cut.Markup);
        Assert.Contains("Type", cut.Markup);
    }

    [Fact]
    public void InputEvents_AreSentByTheCreateForm()
    {
        SetupDefaultResponses();
        _handler.SetJsonResponse(HttpMethod.Post, "api/environments", new EnvironmentDto
        {
            Id = 9,
            Name = "portfolio-test",
            Type = EnvironmentType.Testing,
            ProjectId = 1,
            Servers = []
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/environments/new?projectId=1");
        var cut = Render<EnvironmentEdit>();

        cut.Find("input#Name").Input("portfolio-test");
        cut.Find("textarea#Description").Input("Local Portfolio validation environment.");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("api/environments", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("api/environments", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateEnvironmentRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("portfolio-test", request!.Name);
        Assert.Equal("Local Portfolio validation environment.", request.Description);
        Assert.Equal(1, request.ProjectId);
    }

    [Fact]
    public void Renders_DeleteButton_InEditMode()
    {
        SetupDefaultResponses();
        _handler.SetJsonResponse("api/environments/1", new EnvironmentDto
        {
            Id = 1,
            Name = "Prod",
            Type = EnvironmentType.Production,
            Servers = []
        });

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));

        Assert.Contains("Delete", cut.Markup);
    }

    [Fact]
    public void DoesNotRender_DeleteButton_InNewMode()
    {
        SetupDefaultResponses();

        var cut = Render<EnvironmentEdit>();

        var markup = cut.Markup;
        Assert.DoesNotContain("delete", markup.ToLowerInvariant().Replace("localizeddata", "").Replace("deleteenvironment", ""));
    }

    [Fact]
    public async Task NewEnvironment_ProjectIdQueryChange_RebindsModel()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "P1" }, new ProjectDto { Id = 2, Name = "P2" }],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://test/environments/new?projectId=1");
        var cut = Render<EnvironmentEdit>();

        cut.Instance.ProjectId = 2;
        var method = typeof(EnvironmentEdit).GetMethod(
            "OnParametersSetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        var model = typeof(EnvironmentEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, model.GetType().GetProperty("ProjectId")!.GetValue(model));
    }

    [Fact]
    public async Task NewEnvironment_SourceFromAnotherProject_PrefillsAndCreatesDeepCopy()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto { Id = 3, Name = "Atlas" },
                new ProjectDto { Id = 8, Name = "Shared platform" }
            ],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 5, Name = "shared-01" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items =
            [
                new EnvironmentDto
                {
                    Id = 77,
                    Name = "Reference staging",
                    Description = "Reusable settings",
                    Type = EnvironmentType.Staging,
                    ProjectId = 8,
                    ProjectName = "Shared platform",
                    RequireApproval = true,
                    ApprovalTimeoutMinutes = 90,
                    Servers = [new EnvironmentServerDto { ServerId = 5, ServerName = "shared-01" }]
                }
            ],
            TotalCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/environments", new EnvironmentDto
        {
            Id = 90,
            Name = "Reference staging",
            ProjectId = 3
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/environments/new?projectId=3");
        var cut = Render<EnvironmentEdit>();
        cut.WaitForAssertion(() => Assert.Contains("StartFromExistingEnvironment", cut.Markup));

        var nullableDropdowns = cut.FindComponents<OmniDropDown<int?>>();
        var sourceDropdown = nullableDropdowns.Single(dropdown =>
            dropdown.Instance.Placeholder == "CreateEnvironmentFromScratch");
        var sourceOptions = sourceDropdown.Instance.Options;
        Assert.Contains(
            "Reference staging · Shared platform",
            sourceOptions.Single().Text);

        await cut.InvokeAsync(() => sourceDropdown.Instance.ValueChanged.InvokeAsync(77));

        Assert.Equal("Reference staging", cut.Find("input#Name").GetAttribute("value"));
        Assert.Equal("Reusable settings", cut.Find("textarea#Description").GetAttribute("value"));
        var projectDropdown = cut.FindComponents<OmniDropDown<int?>>()
            .Single(dropdown => dropdown.Instance.Placeholder == "NoProject");
        Assert.Equal(3, projectDropdown.Instance.Value);
        var serversDropdown = Assert.Single(cut.FindComponents<OmniMultiSelect<int>>());
        Assert.Equal([5], serversDropdown.Instance.Value);

        cut.Find("button[type='submit']").Click();
        cut.WaitForAssertion(() => Assert.Contains(
            _handler.RequestDetails,
            requestDetail => requestDetail.Method == "POST"));
        var body = _handler.RequestDetails.Last(request => request.Method == "POST").Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateEnvironmentRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(77, request!.SourceEnvironmentId);
        Assert.Equal(3, request.ProjectId);
    }
}
