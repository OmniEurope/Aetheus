// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class VariableLibraryEditExtendedTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupLibrary(int id = 1)
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse($"api/variable-libraries/{id}", new VariableLibraryDetailDto
        {
            Id = id,
            Name = "config",
            Description = "App configuration",
            ProjectId = 1,
            ProjectName = "App",
            RowVersion = Guid.NewGuid(),
            EntryCount = 1
        });
        // Rows come from the paged entries endpoint; the detail payload never carries them.
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, $"api/variable-libraries/{id}/entries",
        [
            new VariableEntryDto { Id = 1, Key = "BASE_URL", Value = "https://example.com" }
        ]);
        _handler.SetJsonResponse(HttpMethod.Post, $"api/variable-libraries/{id}/entries",
            new VariableEntryDto { Id = 2, Key = "NEW", Value = "val" });
        _handler.SetJsonResponse($"api/variable-libraries/{id}/export",
            new List<VariableEntryDto> { new() { Id = 1, Key = "BASE_URL", Value = "https://example.com" } });
    }

    private IRenderedComponent<VariableLibraryEdit> RenderExisting(int id = 1)
    {
        SetupLibrary(id);
        return Render<VariableLibraryEdit>(p => p.Add(x => x.Id, id));
    }

    [Fact]
    public void Renders_ExistingLibrary_ShowsName()
    {
        var cut = RenderExisting();
        Assert.Contains("config", cut.Markup);
    }

    [Fact]
    public void Renders_ExistingLibrary_ShowsEntryKey()
    {
        var cut = RenderExisting();
        Assert.Contains("BASE_URL", cut.Markup);
    }

    /// <summary>R1-4: an entry row keeps one visible action (Edit); History and Delete are in its menu.</summary>
    [Fact]
    public void AnEntryRowShowsEditAsItsOnlyAction_AndKeepsTheOthersInTheMenu()
    {
        var cut = RenderExisting();

        Assert.Single(cut.FindAll("button[title='Edit']"));
        Assert.Empty(cut.FindAll("button[title='History'], button[title='Delete']"));
        Assert.NotNull(cut.FindMenuItem("History"));
        Assert.NotNull(cut.FindMenuItem("Delete"));
    }

    [Fact]
    public void Renders_NewLibrary_ShowsForm()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        var cut = Render<VariableLibraryEdit>();
        Assert.Contains("NewVariableLibrary", cut.Markup);
    }

    // ImportEntries removed - calls Dialog.OpenAsync which hangs in bUnit

    [Fact]
    public async Task AddEntry_EmptyKey_MakesNoRequest()
    {
        var cut = RenderExisting();

        SetFormValue(cut.Instance, "_newEntry", "Key", "");
        // OnValidSubmit runs on the renderer dispatcher; the grid refresh that follows the POST requires it.
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        // Empty key short-circuits before the create call - no entry POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/variable-libraries/1/entries"));
    }

    [Fact]
    public async Task AddEntry_ValidKey_CallsApi()
    {
        var cut = RenderExisting();

        SetFormValue(cut.Instance, "_newEntry", "Key", "PORT");
        SetFormValue(cut.Instance, "_newEntry", "Value", "8080");
        // OnValidSubmit runs on the renderer dispatcher; the grid refresh that follows the POST requires it.
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddEntry", "_newEntry"));

        // A valid key drives a real POST to the library's entries endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/variable-libraries/1/entries"));
    }

    [Fact]
    public void EditEntry_SetsEditFields()
    {
        var cut = RenderExisting();

        var entry = new VariableEntryDto { Id = 1, Key = "BASE_URL", Value = "https://example.com" };
        typeof(VariableLibraryEdit).GetMethod("EditEntry", Priv)!.Invoke(cut.Instance, [entry]);

        var key = (string)typeof(VariableLibraryEdit).GetField("_editEntryKey", Priv)!.GetValue(cut.Instance)!;
        var val = (string)typeof(VariableLibraryEdit).GetField("_editEntryValue", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("BASE_URL", key);
        Assert.Equal("https://example.com", val);
    }

    [Fact]
    public async Task ReloadDetail_RefetchesFromApi()
    {
        var cut = RenderExisting();

        var method = typeof(VariableLibraryEdit).GetMethod("ReloadDetail", Priv)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        var detail = typeof(VariableLibraryEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) as VariableLibraryDetailDto;
        Assert.NotNull(detail);
        Assert.Equal("config", detail.Name);
    }

    [Fact]
    public async Task ExportEntries_CallsJsAndApi()
    {
        var cut = RenderExisting();

        var method = typeof(VariableLibraryEdit).GetMethod("ExportEntries", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ExportEntries fetches the entries, then hands the JSON to the JS downloadFile helper.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/variable-libraries/1/export"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
    }
}
