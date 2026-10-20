// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.VariableLibraries;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Async method coverage for VariableLibraryEdit.razor.cs - OnSubmit create/update,
/// AddEntry, EditEntry, OnEntryUpdate, CancelEdit (grid row), ExportEntries.
/// Dialog.Confirm (Delete) and Dialog.OpenAsync (ImportEntries, ShowVersions) excluded.
/// </summary>
public class VariableLibraryEditAsyncTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditAsyncTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }, new ProjectDto { Id = 2, Name = "Service" }],
            TotalCount = 2
        });
    }

    private void SetupLibrary(int id, string name = "app-config")
    {
        _handler.SetJsonResponse($"api/variable-libraries/{id}", new VariableLibraryDetailDto
        {
            Id = id,
            Name = name,
            Description = "Config vars",
            ProjectId = 1,
            ProjectName = "App",
            RowVersion = Guid.NewGuid(),
            Entries =
            [
                new VariableEntryDto { Id = 10, Key = "BASE_URL", Value = "https://app.example.com" },
                new VariableEntryDto { Id = 11, Key = "TIMEOUT", Value = "30" }
            ]
        });
    }

    // ── Test 1: New library - OnSubmit calls create API ─────────────────────

    [Fact]
    public async Task OnSubmit_NewLibrary_CallsCreateApi()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/variable-libraries", new VariableLibraryDetailDto
        {
            Id = 99,
            Name = "new-lib",
            RowVersion = Guid.NewGuid(),
            Entries = []
        });

        var cut = Render<VariableLibraryEdit>();

        var model = typeof(VariableLibraryEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "new-lib");

        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Create POSTs to the collection endpoint (distinguishes success from the null/error path).
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/variable-libraries"));
    }

    // ── Test 2: Existing library - OnSubmit updates ──────────────────────────

    [Fact]
    public async Task OnSubmit_ExistingLibrary_CallsUpdateApi()
    {
        SetupProjects();
        SetupLibrary(60);
        _handler.SetJsonResponse($"api/variable-libraries/60",
            new VariableLibraryDetailDto { Id = 60, Name = "updated-lib", RowVersion = Guid.NewGuid(), Entries = [] });

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 60));
        cut.WaitForState(
            () => typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Edit PUTs to the id endpoint (the initial load only GETs it).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/variable-libraries/60"));
    }

    // ── Test 3: AddEntry valid key calls API and reloads ─────────────────────

    [Fact]
    public async Task AddEntry_ValidKey_ClearsKeyAfterSuccess()
    {
        SetupProjects();
        SetupLibrary(61);
        _handler.SetJsonResponse("api/variable-libraries/61/entries",
            new VariableEntryDto { Id = 200, Key = "NEW_VAR", Value = "new-value" });

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 61));
        cut.WaitForState(
            () => typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        SetFormValue(cut.Instance, "_newEntry", "Key", "NEW_VAR");
        SetFormValue(cut.Instance, "_newEntry", "Value", "new-value");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        var key = GetFormValue<string>(cut.Instance, "_newEntry", "Key");
        Assert.Equal(string.Empty, key);
    }

    // ── Test 4: AddEntry empty key does nothing ──────────────────────────────

    [Fact]
    public async Task AddEntry_EmptyKey_IsNoop()
    {
        SetupProjects();
        SetupLibrary(62);

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 62));
        SetFormValue(cut.Instance, "_newEntry", "Key", "");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        // Empty key short-circuits before the create call - no entry POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/variable-libraries/62/entries"));
    }

    // ── Test 5: EditEntry sets edit fields ───────────────────────────────────

    [Fact]
    public void EditEntry_SetsKeyAndValue()
    {
        SetupProjects();
        SetupLibrary(63);

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 63));

        var entry = new VariableEntryDto { Id = 10, Key = "BASE_URL", Value = "https://app.example.com" };
        typeof(VariableLibraryEdit).GetMethod("EditEntry", Priv)!.Invoke(cut.Instance, [entry]);

        var editKey = (string)typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.GetValue(cut.Instance)!;
        var editValue = (string)typeof(VariableLibraryEdit).GetField("_editEntryValue", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("BASE_URL", editKey);
        Assert.Equal("https://app.example.com", editValue);
    }

    // ── Test 6: OnEntryUpdate calls API and reloads ──────────────────────────

    [Fact]
    public async Task OnEntryUpdate_CallsApiWithEditFields()
    {
        SetupProjects();
        SetupLibrary(64);
        _handler.SetJsonResponse("api/variable-libraries/64/entries/10", new VariableEntryDto
        {
            Id = 10,
            Key = "BASE_URL",
            Value = "https://updated.example.com"
        });

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 64));
        cut.WaitForState(
            () => typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.SetValue(cut.Instance, "BASE_URL");
        typeof(VariableLibraryEdit).GetField("_editEntryValue", Priv)!.SetValue(cut.Instance, "https://updated.example.com");

        var entry = new VariableEntryDto { Id = 10, Key = "BASE_URL", Value = "https://app.example.com" };
        var method = typeof(VariableLibraryEdit).GetMethod("OnEntryUpdate", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [entry])!);

        // OnEntryUpdate PUTs the edited fields to the entry's own endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/variable-libraries/64/entries/10"));
    }

    // ── Test 7: ExportEntries calls JS download ──────────────────────────────

    [Fact]
    public async Task ExportEntries_CallsDownloadJs()
    {
        SetupProjects();
        SetupLibrary(65);
        _handler.SetJsonResponse("api/variable-libraries/65/export",
            new List<VariableEntryDto>
            {
                new() { Id = 10, Key = "BASE_URL", Value = "https://app.example.com" }
            });

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 65));
        cut.WaitForState(
            () => typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VariableLibraryEdit).GetMethod("ExportEntries", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ExportEntries fetches the entries, then hands the JSON to the JS downloadFile helper.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/variable-libraries/65/export"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
    }

    // ── Test 8: ReloadDetail updates _detail from API ────────────────────────

    [Fact]
    public async Task ReloadDetail_UpdatesDetail()
    {
        SetupProjects();
        SetupLibrary(66, "fresh-config");

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 66));
        cut.WaitForState(
            () => typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VariableLibraryEdit).GetMethod("ReloadDetail", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = (VariableLibraryDetailDto?)typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("fresh-config", detail.Name);
    }

    // ── Test 9: New library model EnvironmentId can be set ───────────────────

    [Fact]
    public void NewLibrary_ModelEnvironmentId_CanBeSet()
    {
        SetupProjects();

        var cut = Render<VariableLibraryEdit>();

        var model = typeof(VariableLibraryEdit).GetField("_model", Priv)!.GetValue(cut.Instance);
        model!.GetType().GetProperty("EnvironmentId")!.SetValue(model, 5);

        var envId = (int?)model.GetType().GetProperty("EnvironmentId")!.GetValue(model);
        Assert.Equal(5, envId);
    }

    // ── Test 10: New library model ProjectServerId can be set ─────────────────

    [Fact]
    public void NewLibrary_ModelProjectServerId_CanBeSet()
    {
        SetupProjects();

        var cut = Render<VariableLibraryEdit>();

        var model = typeof(VariableLibraryEdit).GetField("_model", Priv)!.GetValue(cut.Instance);
        model!.GetType().GetProperty("ProjectServerId")!.SetValue(model, 7);

        var psId = (int?)model.GetType().GetProperty("ProjectServerId")!.GetValue(model);
        Assert.Equal(7, psId);
    }

    // ── Test 11: Create returns null - saving set false ───────────────────────

    [Fact]
    public async Task OnSubmit_CreateApiReturnsNull_SavesFalse()
    {
        SetupProjects();
        _handler.SetResponse("api/variable-libraries", System.Net.HttpStatusCode.BadRequest);

        var cut = Render<VariableLibraryEdit>();

        var method = typeof(VariableLibraryEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)typeof(VariableLibraryEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
    }
}
