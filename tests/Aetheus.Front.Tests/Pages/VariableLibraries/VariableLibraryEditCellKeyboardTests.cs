// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>An entry's key or value cell opens the row for editing from the keyboard with Enter or
/// Space, as a click does; any other key leaves the row as it is.</summary>
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

    [Theory]
    [InlineData("SITE_DOMAIN", "Enter")]
    [InlineData("aetheus.example", " ")]
    [InlineData("SITE_DOMAIN", "Spacebar")]
    public async Task EnterOrSpaceOnACell_OpensTheRowForEditing(string cell, string key)
    {
        var cut = RenderWithEntry();

        await cut.FindAll(".clickable-cell").Single(span => span.TextContent == cell).KeyDownAsync(new KeyboardEventArgs { Key = key });

        cut.WaitForAssertion(() => Assert.True(IsEditing(cut)));
    }

    [Fact]
    public async Task AnotherKeyOnACell_LeavesTheRowAsItIs()
    {
        var cut = RenderWithEntry();

        await cut.FindAll(".clickable-cell").Single(span => span.TextContent == "SITE_DOMAIN").KeyDownAsync(new KeyboardEventArgs { Key = "a" });

        Assert.False(IsEditing(cut));
    }

    private async Task<IRenderedComponent<VariableLibraryEdit>> RenderEditingAsync()
    {
        var cut = RenderWithEntry();
        await cut.FindAll(".clickable-cell").Single(span => span.TextContent == "SITE_DOMAIN").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
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
