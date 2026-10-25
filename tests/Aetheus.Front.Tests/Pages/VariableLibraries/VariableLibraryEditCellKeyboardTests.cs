// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>An entry's key or value cell opens the row for editing (R-75, EditTrigger Cell), even when the
/// value is empty; Enter saves and Escape cancels in the open row.</summary>
public sealed class VariableLibraryEditCellKeyboardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditCellKeyboardTests()
    {
        var handler = _handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 1, EntryCount = 1 });
        handler.SetJsonResponse("api/variable-libraries/1/entries", new PaginatedResult<VariableEntryDto>
        {
            Items = [new VariableEntryDto { Id = 5, Key = "SITE_DOMAIN", Value = "aetheus.example", VersionCount = 1 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
    }

    private IRenderedComponent<VariableLibraryEdit> RenderWithEntry()
    {
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static bool IsEditing(IRenderedComponent<VariableLibraryEdit> cut) =>
        cut.FindAll("input").Any(input => input.GetAttribute("value") == "SITE_DOMAIN");

    // R-75: the grid edits a row from a single click or key anywhere in an editable cell (EditTrigger Cell).
    // The pointer is decided by the OE grid script, which bUnit does not run; the keyboard path is the real
    // button OE puts in every editable cell, which this drives.
    private static AngleSharp.Dom.IElement CellButton(IRenderedComponent<VariableLibraryEdit> cut, string column) =>
        cut.Find($"tbody td[data-omni-col='{column}'] button.omni-data-grid__edit-cell");

    [Theory]
    [InlineData("Key")]
    [InlineData("Value")]
    public async Task ActivatingTheButtonOfAnEditableCell_OpensTheRowForEditing(string column)
    {
        var cut = RenderWithEntry();

        await CellButton(cut, column).ClickAsync();

        cut.WaitForAssertion(() => Assert.True(IsEditing(cut)));
    }

    [Fact]
    public async Task AnEmptyValueCell_CanStillBeClickedToEditIt()
    {
        _handler.SetJsonResponse("api/variable-libraries/1/entries", new PaginatedResult<VariableEntryDto>
        {
            Items = [new VariableEntryDto { Id = 5, Key = "SITE_DOMAIN", Value = string.Empty, VersionCount = 1 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        var cut = RenderWithEntry();

        await CellButton(cut, "Value").ClickAsync();

        cut.WaitForAssertion(() => Assert.True(IsEditing(cut)));
    }

    [Fact]
    public void NoCellOfTheRowIsAClickableSpanAnyMore() =>
        Assert.Empty(RenderWithEntry().FindAll(".clickable-cell"));

    [Fact]
    public void WithoutAnyClick_TheRowStaysClosed() => Assert.False(IsEditing(RenderWithEntry()));

    private async Task<IRenderedComponent<VariableLibraryEdit>> RenderEditingAsync()
    {
        var cut = RenderWithEntry();
        await CellButton(cut, "Value").ClickAsync();
        cut.WaitForAssertion(() => Assert.True(IsEditing(cut)));
        return cut;
    }

    private static AngleSharp.Dom.IElement OpenRowCell(IRenderedComponent<VariableLibraryEdit> cut) =>
        cut.FindAll("div").First(div => div.HasAttribute("blazor:onkeydown") && div.QuerySelector("input") is not null);

    private bool Updated() =>
        _handler.Requests.Any(request => request.Method == "PUT"
            && request.Url.EndsWith("api/variable-libraries/1/entries/5", StringComparison.Ordinal));

    // In the open row, Enter saves the entry; Escape closes the row without saving; any other key types.
    [Fact]
    public async Task EnterInTheOpenRow_SavesTheEntry()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/variable-libraries/1/entries/5",
            new VariableEntryDto { Id = 5, Key = "SITE_DOMAIN", Value = "aetheus.example", VersionCount = 2 });
        var cut = await RenderEditingAsync();

        await OpenRowCell(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.True(Updated()));
    }

    [Fact]
    public async Task EscapeInTheOpenRow_ClosesItWithoutSaving()
    {
        var cut = await RenderEditingAsync();

        await OpenRowCell(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() => Assert.False(IsEditing(cut)));
        Assert.False(Updated());
    }

    [Fact]
    public async Task AnotherKeyInTheOpenRow_KeepsItOpen()
    {
        var cut = await RenderEditingAsync();

        await OpenRowCell(cut).KeyDownAsync(new KeyboardEventArgs { Key = "a" });

        Assert.True(IsEditing(cut));
        Assert.False(Updated());
    }
}
