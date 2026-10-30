// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public class PipelinesTemplatesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public PipelinesTemplatesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void PipelinesPage_UnifiesUsedPipelinesAndModels()
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var cut = Render<Aetheus.Front.Components.Pipelines.Pipelines>();

        Assert.Contains("UsedPipelines", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Templates", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("templates", "/pipelines?tab=models")]
    [InlineData("pipelines/fleet", "/pipelines")]
    public void LegacyCatalogRoutes_RedirectToUnifiedHub(string legacyPath, string expectedPath)
    {
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo(legacyPath);

        var cut = Render<Aetheus.Front.Components.Pipelines.Pipelines>();

        Assert.EndsWith(expectedPath, nav.Uri, StringComparison.Ordinal);

        // The alias routes are served by this very component, so the router reuses the running
        // instance and OnInitialized never runs a second time. Redirecting before initializing left
        // the hub with no permissions and therefore no tabs at all, which is what this asserts.
        Assert.Contains("UsedPipelines", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Templates", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesPage_LoadsAndLinksDedicatedRoutes()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new()
            {
                Id = 7,
                Name = "Build Template",
                Category = "CI",
                Version = 3,
                UpdatedAt = new DateTime(2026, 8, 9, 10, 0, 0),
                PipelineCount = 4,
                LatestRunAt = new DateTime(2026, 8, 10, 8, 0, 0),
                CanEdit = true
            }
        });

        var cut = Render<PipelineTemplates>();
        cut.WaitForState(() => cut.Markup.Contains("Build Template", StringComparison.Ordinal));

        Assert.Contains("href=\"/templates/7\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("v3", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v@template.Version", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Category", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Updated", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("LatestRunShort", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">4<", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/pipelines?templateId=7\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("title=\"ViewPipelinesUsingTemplate\"", cut.Markup, StringComparison.Ordinal);
        // STD-BTN detection 11: with write access the row shows Edit and Delete; Use, Versions and Export
        // sit in its "More actions" menu.
        Assert.NotNull(cut.Find("button[title=\"Edit\"]"));
        Assert.NotNull(cut.Find("button[title=\"Delete\"]"));
        Assert.Empty(cut.FindAll("button[title=\"UseTemplate\"]"));
        var use = cut.FindMenuItem("UseTemplate");
        Assert.NotNull(use);
        Assert.Equal("UseTemplate", use.GetAttribute("title"));
        Assert.Equal("VersionHistoryTitle", cut.FindMenuItem("Versions")?.GetAttribute("title"));
        Assert.NotNull(cut.FindMenuItem("Export"));
    }

    // D9 / SITE-FUNC-TEMPLATE-USE-001: a user-made template opened the role wizard, which knows only
    // the built-in role templates and left every role unchecked; it now opens the prefilled editor.
    [Theory]
    [InlineData(9, "QA", "/pipelines/new?templateId=9")]
    [InlineData(1, "application-ci", "/pipelines/setup?templateId=1")]
    public async Task TemplatesPage_UseOpensEditorForCustomTemplateAndWizardForRoleTemplate(
        int id, string name, string expectedPath)
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = id, Name = name, Category = "CI", Version = 1, CanEdit = true }
        });
        var cut = Render<PipelineTemplates>();
        cut.WaitForState(() => cut.Markup.Contains(name, StringComparison.Ordinal));

        await cut.FindMenuItem("UseTemplate")!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith(expectedPath, nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesPage_LoadFailure_ShowsRetry()
    {
        _handler.SetResponse("api/pipelines/templates", System.Net.HttpStatusCode.ServiceUnavailable);

        var cut = Render<PipelineTemplates>();
        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));

        Assert.False((bool)typeof(PipelineTemplates).GetField("_loading", Priv)!.GetValue(cut.Instance)!);
        Assert.True((bool)typeof(PipelineTemplates).GetField("_loadFailed", Priv)!.GetValue(cut.Instance)!);
        Assert.Contains("Retry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplatesPage_PermissionChangesHideWriteActions()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<PipelineTemplates>();
        Assert.Contains("NewTemplate", cut.Markup, StringComparison.Ordinal);

        var permissions = Services.GetRequiredService<PermissionService>();
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));

        cut.WaitForAssertion(() => Assert.DoesNotContain("NewTemplate", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TemplateEditPage_UseOnACustomTemplateOpensThePrefilledEditor()
    {
        _handler.SetJsonResponse("api/pipelines/templates/9", new PipelineTemplateDto
        {
            Id = 9,
            Name = "QA",
            Category = "CI",
            Version = 2,
            YamlContent = "name: qa\nstages: []"
        });
        var cut = Render<PipelineTemplateEdit>(parameters => parameters.Add(component => component.Id, 9));
        cut.WaitForState(() => cut.Markup.Contains("UseTemplate", StringComparison.Ordinal));

        await cut.FindAll("button").First(button => button.TextContent.Contains("UseTemplate", StringComparison.Ordinal))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/pipelines/new?templateId=9", nav.Uri, StringComparison.Ordinal);
    }

    // A shared delivery template is read by every organization and changed by a site Admin only: the server
    // says so with CanEdit, and the page shows it read-only instead of offering a save that returns 403.
    [Theory]
    [InlineData(true, "SharedTemplateReadOnlyNote")]
    [InlineData(false, "ReadOnlyTemplateNote")]
    public void TemplateEditPage_WhenTheServerSaysItCannotBeChanged_IsReadOnly(bool isShared, string expectedNote)
    {
        _handler.SetJsonResponse("api/pipelines/templates/3", new PipelineTemplateDto
        {
            Id = 3,
            Name = "application-ci",
            Category = "CI",
            Version = 2,
            YamlContent = "name: ci\nstages: []",
            IsShared = isShared,
            CanEdit = false
        });

        var cut = Render<PipelineTemplateEdit>(parameters => parameters.Add(component => component.Id, 3));
        cut.WaitForAssertion(() => Assert.Contains(expectedNote, cut.Markup, StringComparison.Ordinal));

        Assert.Empty(cut.FindAll("button[type=submit]"));
        Assert.DoesNotContain("Save", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input#ChangelogEntry"));
        Assert.Empty(cut.FindComponents<MonacoEditor>());
        Assert.Equal("name: ci\nstages: []", cut.FindComponent<OmniCodeViewer>().Instance.Code);
        Assert.True(cut.Find("input#Category").HasAttribute("disabled"));
        Assert.True(cut.Find("textarea#Description").HasAttribute("disabled"));
        // Reading and using the template stay available.
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("UseTemplate", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateEditPage_WhenTheServerSaysItCanBeChanged_OffersTheSave()
    {
        _handler.SetJsonResponse("api/pipelines/templates/3", new PipelineTemplateDto
        {
            Id = 3,
            Name = "own-ci",
            Category = "CI",
            Version = 1,
            YamlContent = "name: ci\nstages: []",
            CanEdit = true
        });

        var cut = Render<PipelineTemplateEdit>(parameters => parameters.Add(component => component.Id, 3));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[type=submit]")));

        Assert.NotEmpty(cut.FindAll("input#ChangelogEntry"));
        Assert.Single(cut.FindComponents<MonacoEditor>());
        Assert.Empty(cut.FindComponents<OmniCodeViewer>());
        Assert.DoesNotContain("ReadOnlyTemplateNote", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesPage_ASharedTemplateTheUserCannotChange_OffersUseAndNoEditOrDelete()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 3, Name = "application-ci", Version = 2, IsShared = true, CanEdit = false },
            new() { Id = 4, Name = "own-ci", Version = 1, CanEdit = true }
        });

        var cut = Render<PipelineTemplates>();
        cut.WaitForState(() => cut.Markup.Contains("own-ci", StringComparison.Ordinal));

        var rows = cut.FindAll("tbody tr");
        var shared = rows.Single(row => row.TextContent.Contains("application-ci", StringComparison.Ordinal));
        var own = rows.Single(row => row.TextContent.Contains("own-ci", StringComparison.Ordinal));
        Assert.Empty(shared.QuerySelectorAll("button[title=\"Edit\"]"));
        Assert.Empty(shared.QuerySelectorAll("button[title=\"Delete\"]"));
        Assert.NotEmpty(shared.QuerySelectorAll("button[title=\"UseTemplate\"]"));
        Assert.Contains("SharedTemplate", shared.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(own.QuerySelectorAll("button[title=\"Edit\"]"));
        Assert.NotEmpty(own.QuerySelectorAll("button[title=\"Delete\"]"));
        Assert.DoesNotContain("SharedTemplate", own.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void NewTemplatePage_StartsWithUsableYamlAndInitialChangelog()
    {
        var cut = Render<PipelineTemplateEdit>();
        var model = typeof(PipelineTemplateEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var modelType = model.GetType();

        Assert.Equal("Pipeline", modelType.GetProperty("Category")!.GetValue(model));
        Assert.Equal("InitialTemplateVersion", modelType.GetProperty("ChangelogEntry")!.GetValue(model));
        Assert.Contains("git status --short", (string)modelType.GetProperty("YamlContent")!.GetValue(model)!, StringComparison.Ordinal);
    }

    [Fact]
    public void NewTemplatePage_InputEventsAreSentByTheCreateForm()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/templates", new PipelineTemplateDto
        {
            Id = 42,
            Name = "Reusable validation",
            Category = "Validation",
            Version = 1
        });
        var cut = Render<PipelineTemplateEdit>();

        cut.Find("input#Name").Input("Reusable validation");
        cut.Find("input#Category").Input("Validation");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("api/pipelines/templates", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("api/pipelines/templates", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreatePipelineTemplateRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("Reusable validation", request!.Name);
        Assert.Equal("Validation", request.Category);
        Assert.Contains("git status --short", request.YamlContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesPage_RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 4, Name = "dotnet-ci", Version = 2, PipelineCount = 3, UpdatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), CanEdit = true }
        });

        var cut = Render<PipelineTemplates>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The template and usage links, then the row buttons.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
