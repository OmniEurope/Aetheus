// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// Behavioural tests for ProjectFormDialog.razor(.cs). Covers the create vs. edit render paths
/// (field prefill, submit label, Delete button presence) and the successful submit, which issues
/// the real create/update API call and closes the dialog with the resulting <see cref="ProjectDto"/>
/// payload (captured by a spy OmniDialogService).
/// </summary>
public class ProjectFormDialogTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectFormDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The dialog injects ConfirmHelper (concrete, not in the shared DI graph) - register it so the
        // component can resolve its dependencies and render at all.
        Services.AddScoped<ConfirmHelper>();
    }

    /// <summary>
    /// Registers a spy OmniDialogService (lazy factory so the provider isn't built early) that records the
    /// Close payload. OnSubmit lands on the spy, so we can assert exactly what was sent back to the caller.
    /// </summary>
    private void RegisterSpyDialog() =>
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ProjectDetailDto MakeProject() => new()
    {
        Id = 42,
        Name = "Existing Project",
        Description = "An existing description",
        RepositoryUrl = "https://github.com/owner/repo.git",
        DefaultBranch = "main",
        Status = ProjectStatus.Active,
        Tags = ["alpha", "beta"],
        ArtifactRetentionDays = 30,
        ArtifactLatestRetentionDays = 5,
        ReleaseNumberingPattern = "1.0.$(BUILD_BUILDID)"
    };

    // ── Create mode render ────────────────────────────────────────────────────

    [Fact]
    public void Renders_CreateMode_WithCreateButton_AndNoDelete()
    {
        var cut = Render<ProjectFormDialog>(); // Project == null → create mode

        // _isNew drives the template; the submit label is "Create" and the Delete button is hidden.
        var isNew = (bool)typeof(ProjectFormDialog).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(isNew);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Delete"));
        Assert.Null(cut.FindMenuItem("Delete"));
    }

    [Fact]
    public void Renders_CreateMode_AllCoreFields()
    {
        var cut = Render<ProjectFormDialog>();

        // The localizer stub echoes the key, so the OmniFormField labels surface verbatim.
        Assert.Contains("Name", cut.Markup);
        Assert.Contains("RepositoryUrl", cut.Markup);
        Assert.Contains("DefaultBranch", cut.Markup);
        Assert.Contains("TagsCommaSeparated", cut.Markup);
        Assert.Contains("ArtifactRetentionDays", cut.Markup);
        // Status selector is edit-only - absent in create mode.
        Assert.DoesNotContain("Status", cut.Markup);
    }

    [Fact]
    public void InputEvents_AreSentByTheProjectForm()
    {
        RegisterSpyDialog();
        _handler.SetJsonResponse(HttpMethod.Post, "api/projects",
            new ProjectDto { Id = 7, Name = "Template Reuse Validation" });
        var cut = Render<ProjectFormDialog>();

        cut.Find("input#project-form-name").Input("Template Reuse Validation");
        cut.Find("textarea#Description").Input("Local template reuse validation.");
        cut.Find("input#RepositoryUrl").Input("https://github.com/owner/repo.git");
        cut.Find("input#DefaultBranch").Input("develop");
        cut.Find("input#Tags").Input("local, template-reuse");
        cut.Find("input#ReleaseNumberingPattern").Input("2.0.$(BUILD_BUILDID)");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Spy().Closed));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("api/projects", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateProjectRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("Template Reuse Validation", request!.Name);
        Assert.Equal("Local template reuse validation.", request.Description);
        Assert.Equal("https://github.com/owner/repo.git", request.RepositoryUrl);
        Assert.Equal("develop", request.DefaultBranch);
        Assert.Equal(["local", "template-reuse"], request.Tags);
        Assert.Equal("2.0.$(BUILD_BUILDID)", request.ReleaseNumberingPattern);
    }

    // ── Edit mode render ──────────────────────────────────────────────────────

    [Fact]
    public void Renders_EditMode_PrefilledFromProject()
    {
        var project = MakeProject();

        var cut = Render<ProjectFormDialog>(p => p.Add(x => x.Project, project));

        var isNew = (bool)typeof(ProjectFormDialog).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.False(isNew);

        // OnInitialized copies the passed project into the editable form model.
        var model = typeof(ProjectFormDialog).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var modelType = model.GetType();
        Assert.Equal("Existing Project", modelType.GetProperty("Name")!.GetValue(model));
        Assert.Equal("An existing description", modelType.GetProperty("Description")!.GetValue(model));
        Assert.Equal("https://github.com/owner/repo.git", modelType.GetProperty("RepositoryUrl")!.GetValue(model));
        Assert.Equal("main", modelType.GetProperty("DefaultBranch")!.GetValue(model));
        // Tags are flattened back to the comma-separated raw input.
        Assert.Equal("alpha, beta", modelType.GetProperty("TagsRaw")!.GetValue(model));
        Assert.Equal(30, modelType.GetProperty("ArtifactRetentionDays")!.GetValue(model));
    }

    [Fact]
    public void Renders_EditMode_WithSaveButton_DeleteButton_AndStatusField()
    {
        var cut = Render<ProjectFormDialog>(p => p.Add(x => x.Project, MakeProject()));

        // Edit mode: submit label flips to "Save", Delete appears in the "More actions" menu (STD-BTN
        // detection 11), and the Status selector shows.
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Save"));
        Assert.NotNull(cut.FindMenuItem("Delete"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
        Assert.Contains("Status", cut.Markup);
    }

    // ── Successful submit ─────────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_CreateMode_PostsProject_AndClosesWithResult()
    {
        RegisterSpyDialog();
        var created = new ProjectDto { Id = 7, Name = "Fresh Project" };
        _handler.SetJsonResponse(HttpMethod.Post, "api/projects", created);

        var cut = Render<ProjectFormDialog>();
        var model = typeof(ProjectFormDialog).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Fresh Project");

        var submit = typeof(ProjectFormDialog).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(() => (Task)submit.Invoke(cut.Instance, [])!);

        // A create POST was actually sent to the projects endpoint…
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/projects"));
        // …and the dialog closed with the created project as its payload (so the caller can refresh).
        Assert.True(Spy().Closed);
        var payload = Assert.IsType<ProjectDto>(Spy().LastResult);
        Assert.Equal(7, payload.Id);
        Assert.Equal("Fresh Project", payload.Name);
    }

    [Fact]
    public async Task OnSubmit_EditMode_PutsProject_AndClosesWithResult()
    {
        RegisterSpyDialog();
        var updated = new ProjectDto { Id = 42, Name = "Existing Project" };
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/42", updated);

        var cut = Render<ProjectFormDialog>(p => p.Add(x => x.Project, MakeProject()));

        var submit = typeof(ProjectFormDialog).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(() => (Task)submit.Invoke(cut.Instance, [])!);

        // Edit mode issues a PUT to the per-id route and closes with the updated DTO.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/projects/42"));
        Assert.True(Spy().Closed);
        var payload = Assert.IsType<ProjectDto>(Spy().LastResult);
        Assert.Equal(42, payload.Id);
    }

    [Fact]
    public async Task OnSubmit_WhenApiReturnsNull_DoesNotCloseDialog()
    {
        RegisterSpyDialog();
        // 404 → PostJsonAsync yields null → the dialog stays open and shows an error toast instead.
        _handler.SetResponse(HttpMethod.Post, "api/projects", HttpStatusCode.NotFound);

        var cut = Render<ProjectFormDialog>();
        var model = typeof(ProjectFormDialog).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Doomed");

        var submit = typeof(ProjectFormDialog).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(() => (Task)submit.Invoke(cut.Instance, [])!);

        // A failed create must not close the dialog (no payload sent back to the caller).
        Assert.False(Spy().Closed);
    }
}
