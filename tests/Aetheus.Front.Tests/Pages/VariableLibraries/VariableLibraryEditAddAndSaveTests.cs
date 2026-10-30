// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>What the entries grid shows right after an add (R-65) and right after a save (R-66).</summary>
public sealed class VariableLibraryEditAddAndSaveTests : BunitContext
{
    private const string EntriesUrl = "api/variable-libraries/1/entries";
    private const string FilterParameter = "Filters%5B0%5D";
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditAddAndSaveTests()
    {
        var handler = _handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 1, EntryCount = 1 });
        SetEntries("aetheus.example");
    }

    private void SetEntries(string value) =>
        _handler.SetJsonResponse(HttpMethod.Get, EntriesUrl, new PaginatedResult<VariableEntryDto>
        {
            Items = [new VariableEntryDto { Id = 5, Key = "SITE_DOMAIN", Value = value, VersionCount = 1 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });

    private static bool IsEntriesRead((string Method, string Url) request) =>
        request.Method == "GET" && request.Url.Contains(EntriesUrl, StringComparison.Ordinal);

    private List<string> EntryReads() =>
        [.. _handler.RequestsSnapshot().Where(IsEntriesRead).Select(request => request.Url)];

    private IRenderedComponent<VariableLibraryEdit> RenderWithEntry()
    {
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    // ---- R-65: the filter of a ?key= link must not hide the entry that was just added. ----

    [Fact]
    public async Task AddingAnEntryWhileTheKeyFilterOfTheLinkIsActive_ClearsTheFilterAndTheAddressKey()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EntriesUrl, new VariableEntryDto { Id = 6, Key = "OTHER_KEY", Value = "v" });
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://test/variable-libraries/1?key=DOMAIN");
        var cut = RenderWithEntry();
        // The link's filter is on a read of the grid before anything is added.
        await _handler.WhenRequestedAsync(
            request => IsEntriesRead(request) && request.Url.Contains(FilterParameter, StringComparison.Ordinal), 1, "the filtered first read");
        var readsBeforeAdd = EntryReads().Count;

        cut.Find("input[aria-label='Key']").Input("OTHER_KEY");
        await cut.Find("button[type='submit']").ClickAsync();
        await _handler.WhenRequestedAsync(IsEntriesRead, readsBeforeAdd + 1, "a read of the entries after the add");

        // Every read after the add is unfiltered, so the new key is among the rows the server returns.
        Assert.All(EntryReads().Skip(readsBeforeAdd), url => Assert.DoesNotContain(FilterParameter, url, StringComparison.Ordinal));
        Assert.DoesNotContain("key=", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAnEntryWhileAHeaderFilterIsActive_ClearsItToo()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EntriesUrl, new VariableEntryDto { Id = 6, Key = "OTHER_KEY", Value = "v" });
        var cut = RenderWithEntry();
        await cut.InvokeAsync(() => FilterEntriesAsync(cut, "Value", "example"));
        await _handler.WhenRequestedAsync(
            request => IsEntriesRead(request) && request.Url.Contains(FilterParameter, StringComparison.Ordinal), 1, "the filtered read");
        var readsBeforeAdd = EntryReads().Count;

        cut.Find("input[aria-label='Key']").Input("OTHER_KEY");
        await cut.Find("button[type='submit']").ClickAsync();
        await _handler.WhenRequestedAsync(IsEntriesRead, readsBeforeAdd + 1, "a read of the entries after the add");

        Assert.All(EntryReads().Skip(readsBeforeAdd), url => Assert.DoesNotContain(FilterParameter, url, StringComparison.Ordinal));
    }

    private static Task FilterEntriesAsync(IRenderedComponent<VariableLibraryEdit> cut, string column, string value) =>
        cut.FindComponent<AetheusDataGrid<VariableEntryDto>>().Instance.Grid!
            .SetFiltersAsync(new Dictionary<string, string?> { [column] = value });

    // ---- R-66: the saved value shows at once, not the old one for a moment. ----

    private static AngleSharp.Dom.IElement ValueCellButton(IRenderedComponent<VariableLibraryEdit> cut) =>
        cut.Find("tbody td[data-omni-col='Value'] button.omni-data-grid__edit-cell");

    private static string ValueCellText(IRenderedComponent<VariableLibraryEdit> cut) =>
        cut.Find("tbody td[data-omni-col='Value'] .library-cell-text").TextContent;

    private async Task<Task> ValidateTheEditAsync(IRenderedComponent<VariableLibraryEdit> cut, string newValue)
    {
        await ValueCellButton(cut).ClickAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("div.grid-edit-cell input[aria-label='Value']")));
        cut.Find("div.grid-edit-cell input[aria-label='Value']").Input(newValue);
        var editor = cut.FindAll("div").First(div => div.HasAttribute("blazor:onkeydown") && div.QuerySelector("input") is not null);
        // Not awaited: the handler runs until the server has answered, which is the moment under test.
        var saving = editor.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await _handler.WhenRequestedAsync(request => request.Method == "PUT", 1, "the save");
        await cut.InvokeAsync(() => { });
        return saving;
    }

    [Fact]
    public async Task ValidatingAnEdit_ShowsTheSavedValueAtOnce_WhileTheServerIsStillAnswering()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Put, $"{EntriesUrl}/5", async ct =>
        {
            await release.Task.WaitAsync(ct);
            return new VariableEntryDto { Id = 5, Key = "SITE_DOMAIN", Value = "changed.example", VersionCount = 2 };
        });
        var cut = RenderWithEntry();

        var saving = await ValidateTheEditAsync(cut, "changed.example");
        try
        {
            // The server has not answered yet: the row already reads the new value, never the old one.
            Assert.Equal("changed.example", ValueCellText(cut));
            Assert.DoesNotContain("aetheus.example", cut.Markup, StringComparison.Ordinal);
        }
        finally
        {
            SetEntries("changed.example");
            release.SetResult();
            await saving;
        }

        cut.WaitForAssertion(() => Assert.Equal("changed.example", ValueCellText(cut)));
        Assert.DoesNotContain("aetheus.example", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSave_PutsTheOldValueBackInTheRow()
    {
        _handler.SetResponse(HttpMethod.Put, $"{EntriesUrl}/5", System.Net.HttpStatusCode.InternalServerError);
        var cut = RenderWithEntry();

        var saving = await ValidateTheEditAsync(cut, "changed.example");
        await saving;

        // Nothing was saved, so the row must not keep showing the value that failed to save.
        Assert.Equal("aetheus.example", ValueCellText(cut));
    }
}
