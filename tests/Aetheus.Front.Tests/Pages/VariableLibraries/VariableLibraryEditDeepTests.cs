// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.VariableLibraries;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VarLibDeepCoverage;

/// <summary>
/// Deep coverage for VariableLibraryEdit.razor.cs - OnParametersSetAsync,
/// OnSubmit (new + edit), AddEntry, EditEntry, OnEntryUpdate, DeleteEntry
/// (guard only - Dialog.Confirm hangs), ShowVersions, ExportEntries, ImportEntries,
/// ReloadDetail.
/// </summary>
public class VariableLibraryEditDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static VariableLibraryDetailDto MakeDetail(int id = 1) => new()
    {
        Id = id,
        Name = "My Library",
        Description = "Desc",
        ProjectId = 1,
        Entries =
        [
            new VariableEntryDto { Id = 10, Key = "DB_URL", Value = "postgres://localhost" }
        ]
    };

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/projects/1/servers",
            new List<ProjectServerDto> { new() { Id = 7, ProjectId = 1, DisplayName = "App server" } });
    }

    private void SetupLibrary(int id = 1)
    {
        _handler.SetJsonResponse($"api/variable-libraries/{id}", MakeDetail(id));
    }

    private static int? ModelInt(IRenderedComponent<VariableLibraryEdit> cut, string prop)
    {
        var model = typeof(VariableLibraryEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        return (int?)model.GetType().GetProperty(prop)!.GetValue(model);
    }

    // ── New library ──────────────────────────────────────────────────────────

    [Fact]
    public void Renders_NewLibrary()
    {
        SetupProjects();
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // New-library mode (Id=0) loads the project list but must never fetch a library detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/variable-libraries/"));
    }

    [Fact]
    public async Task OnParametersSet_WithProjectId_SetsModel()
    {
        SetupProjects();
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(VariableLibraryEdit).GetProperty("ProjectId")!.SetValue(cut.Instance, (int?)1);
        var method = typeof(VariableLibraryEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ProjectId=1 is in the loaded project list → bound onto the model.
        Assert.Equal(1, ModelInt(cut, "ProjectId"));
    }

    [Fact]
    public async Task OnParametersSet_WithEnvironmentId_SetsModel()
    {
        SetupProjects();
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(VariableLibraryEdit).GetProperty("EnvironmentId")!.SetValue(cut.Instance, (int?)5);
        var method = typeof(VariableLibraryEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // No ProjectId → the EnvironmentId branch binds onto the model.
        Assert.Equal(5, ModelInt(cut, "EnvironmentId"));
    }

    [Fact]
    public async Task OnParametersSet_WithProjectServerId_SetsModel()
    {
        SetupProjects();
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(VariableLibraryEdit).GetProperty("ProjectServerId")!.SetValue(cut.Instance, (int?)7);
        var method = typeof(VariableLibraryEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // No ProjectId/EnvironmentId → the ProjectServerId branch binds onto the model.
        Assert.Equal(7, ModelInt(cut, "ProjectServerId"));
    }

    // ── Existing library ─────────────────────────────────────────────────────

    [Fact]
    public void Renders_ExistingLibrary()
    {
        SetupProjects();
        SetupLibrary(1);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var detail = (VariableLibraryDetailDto?)typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
    }

    [Fact]
    public void LibraryIdChange_ReloadsSameComponentInstance()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/variable-libraries/1", MakeDetail(1) with { Name = "First library" });
        _handler.SetJsonResponse("api/variable-libraries/2", MakeDetail(2) with { Name = "Second library" });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        SetFormValue(cut.Instance, "_newEntry", "Key", "STALE_NEW_KEY");
        SetFormValue(cut.Instance, "_newEntry", "Value", "stale-new-value");
        typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.SetValue(cut.Instance, "STALE_EDIT_KEY");
        typeof(VariableLibraryEdit).GetField("_editEntryValue", Priv)!.SetValue(cut.Instance, "stale-edit-value");
        typeof(VariableLibraryEdit).GetField("_editingEntry", Priv)!.SetValue(
            cut.Instance, new VariableEntryDto { Id = 10, Key = "STALE", Value = "stale" });

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("Second library", cut.Markup));
        Assert.DoesNotContain("First library", cut.Markup);
        Assert.Equal(string.Empty, GetFormValue<string>(cut.Instance, "_newEntry", "Key"));
        Assert.Equal(string.Empty, GetFormValue<string>(cut.Instance, "_newEntry", "Value"));
        Assert.Equal(string.Empty, typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.GetValue(cut.Instance));
        Assert.Equal(string.Empty, typeof(VariableLibraryEdit).GetField("_editEntryValue", Priv)!.GetValue(cut.Instance));
        Assert.Null(typeof(VariableLibraryEdit).GetField("_editingEntry", Priv)!.GetValue(cut.Instance));
    }

    // ── Same Id - skips reload ────────────────────────────────────────────────

    [Fact]
    public async Task OnParametersSetAsync_SameId_SkipsReload()
    {
        SetupProjects();
        SetupLibrary(1);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // Mutate the loaded detail to detect whether the same-Id call reloads (it must not).
        var detailField = typeof(VariableLibraryEdit).GetField("_detail", Priv)!;
        var marker = new VariableLibraryDetailDto { Id = 1, Name = "SENTINEL", ProjectId = 1, Entries = [] };
        detailField.SetValue(cut.Instance, marker);

        var method = typeof(VariableLibraryEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // _previousId == Id → early return, the detail reference is untouched.
        Assert.Same(marker, (VariableLibraryDetailDto?)detailField.GetValue(cut.Instance));
    }

    // ── OnSubmit - new ────────────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_New_CreatesLibrary()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/variable-libraries", new VariableLibraryDetailDto { Id = 99, Name = "New" });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var model = typeof(VariableLibraryEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "New Library");

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Create succeeded (Id=99) → busy flag cleared and navigation to the new library.
        Assert.False((bool)typeof(VariableLibraryEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
        Assert.EndsWith("/variable-libraries/99", nav.Uri);
    }

    // ── OnSubmit - edit ────────────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_Edit_Updates()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetJsonResponse("api/variable-libraries/1", MakeDetail(1));
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Update succeeded → busy flag released and a "Saved" success toast surfaced.
        Assert.False((bool)typeof(VariableLibraryEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
        Assert.Contains(notif.Toasts(), m => m.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task OnSubmit_EditFailure_DoesNotReportSuccess()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetResponse(HttpMethod.Put, "api/variable-libraries/1", System.Net.HttpStatusCode.Conflict);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        Assert.DoesNotContain(Services.Toasts(),
            message => message.Severity == OmniSeverity.Success);
        Assert.False((bool)typeof(VariableLibraryEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task OnDelete_FailedStatusDoesNotNavigateOrReportSuccess()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetResponse(HttpMethod.Delete, "api/variable-libraries/1", System.Net.HttpStatusCode.Conflict);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var method = typeof(VariableLibraryEdit).GetMethod("OnDelete", Priv)!;

        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.False(nav.Uri.EndsWith("/variable-libraries", StringComparison.Ordinal));
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
        Assert.DoesNotContain(Services.Toasts(),
            message => message.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task OnSubmit_MalformedResponse_ReleasesSavingFlag()
    {
        SetupProjects();
        _handler.SetRawResponse("api/variable-libraries", "{");
        var cut = Render<VariableLibraryEdit>(parameters => parameters.Add(component => component.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!));

        Assert.False((bool)typeof(VariableLibraryEdit)
            .GetField("_saving", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task LoadFailure_StopsSpinnerAndOffersWorkingRetry()
    {
        _handler.SetResponse(HttpMethod.Get, "api/projects", System.Net.HttpStatusCode.ServiceUnavailable);
        var cut = Render<VariableLibraryEdit>(parameters => parameters.Add(component => component.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        Assert.Contains("LoadError", cut.Markup, StringComparison.Ordinal);

        SetupProjects();
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
        var retry = typeof(VariableLibraryEdit).GetMethod("RetryLoadAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)retry.Invoke(cut.Instance, [])!);

        cut.WaitForAssertion(() =>
            Assert.False((bool)typeof(VariableLibraryEdit)
                .GetField("_loadFailed", Priv)!.GetValue(cut.Instance)!));
    }

    // ── AddEntry - empty key does nothing ────────────────────────────────────

    [Fact]
    public async Task AddEntry_EmptyKey_LeavesTheValueUnchanged()
    {
        SetupProjects();
        SetupLibrary(1);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        SetFormValue(cut.Instance, "_newEntry", "Key", "   ");
        SetFormValue(cut.Instance, "_newEntry", "Value", "v");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        // Whitespace/empty key → guard returns: value preserved, no toast.
        Assert.Equal("v", GetFormValue<string>(cut.Instance, "_newEntry", "Value"));
        Assert.Empty(notif.Toasts());
    }

    // ── AddEntry - with key ───────────────────────────────────────────────────

    [Fact]
    public async Task AddEntry_WithKey_CallsApi()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetJsonResponse("api/variable-libraries/1/entries", new VariableEntryDto { Id = 50, Key = "MY_KEY" });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        SetFormValue(cut.Instance, "_newEntry", "Key", "MY_KEY");
        SetFormValue(cut.Instance, "_newEntry", "Value", "my_value");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        // Entry created → key/value inputs reset and a success toast surfaces.
        Assert.Equal(string.Empty, GetFormValue<string>(cut.Instance, "_newEntry", "Key"));
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }

    // ── EditEntry - sets edit fields ──────────────────────────────────────────

    [Fact]
    public void EditEntry_SetsEditFields()
    {
        SetupProjects();
        SetupLibrary(1);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var entry = new VariableEntryDto { Id = 10, Key = "MY_KEY", Value = "MY_VAL" };
        var method = typeof(VariableLibraryEdit).GetMethod("EditEntry", Priv)!;
        method.Invoke(cut.Instance, [entry]);

        var editKey = (string)typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("MY_KEY", editKey);
    }

    // ── OnEntryUpdate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnEntryUpdate_CallsApi()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetJsonResponse("api/variable-libraries/1/entries/10", new VariableEntryDto { Id = 10, Key = "K" });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var entry = new VariableEntryDto { Id = 10, Key = "K", Value = "V" };
        var method = typeof(VariableLibraryEdit).GetMethod("OnEntryUpdate", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [entry])!);

        // OnEntryUpdate persists then raises a "Saved" success toast.
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }

    [Fact]
    public async Task OnEntryUpdate_FailedResponseDoesNotReportSuccess()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetResponse(
            HttpMethod.Put,
            "api/variable-libraries/1/entries/10",
            System.Net.HttpStatusCode.Conflict);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var entry = new VariableEntryDto { Id = 10, Key = "K", Value = "V" };
        var method = typeof(VariableLibraryEdit).GetMethod("OnEntryUpdate", Priv)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [entry])!);

        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
        Assert.DoesNotContain(Services.Toasts(),
            message => message.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task DeleteEntry_FailedStatusDoesNotReportSuccess()
    {
        SetupProjects();
        SetupLibrary(1);
        _handler.SetResponse(
            HttpMethod.Delete,
            "api/variable-libraries/1/entries/10",
            System.Net.HttpStatusCode.Conflict);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var method = typeof(VariableLibraryEdit).GetMethod("DeleteEntry", Priv)!;

        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new VariableEntryDto { Id = 10, Key = "DB_URL", Value = "postgres://localhost" }])!);
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
        Assert.DoesNotContain(Services.Toasts(),
            message => message.Severity == OmniSeverity.Success);
    }

    // ── ReloadDetail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadDetail_RefreshesDetail()
    {
        SetupProjects();
        SetupLibrary(1);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // Clear the loaded detail, then ReloadDetail must re-fetch it from the API.
        typeof(VariableLibraryEdit).GetField("_detail", Priv)!.SetValue(cut.Instance, null);
        var method = typeof(VariableLibraryEdit).GetMethod("ReloadDetail", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = (VariableLibraryDetailDto?)typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("My Library", detail!.Name);
    }

    [Fact]
    public async Task TheEntriesExport_ReadsTheRequestedPageOfThisLibrary()
    {
        SetupProjects();
        SetupLibrary();
        _handler.SetPaginatedJsonResponse("api/variable-libraries/1/entries",
            new List<VariableEntryDto> { new() { Id = 10, Key = "DB_URL", Value = "postgres://localhost" } });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<AetheusDataGrid<VariableEntryDto>>()));
        var grid = cut.FindComponent<AetheusDataGrid<VariableEntryDto>>().Instance;

        var page = await cut.InvokeAsync(() => grid.ExportRead!(new GridLoadArgs { Skip = 25, Top = 25 }));

        Assert.Equal("DB_URL", Assert.Single(page.Items).Key);
        var url = _handler.Requests.Last(r => r.Url.Contains("api/variable-libraries/1/entries?", StringComparison.Ordinal)).Url;
        Assert.Contains("page=2", url, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", url, StringComparison.Ordinal);
    }
}
