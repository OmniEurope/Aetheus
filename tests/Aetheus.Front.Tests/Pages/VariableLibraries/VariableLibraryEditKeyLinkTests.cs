// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>R-56: a ?key= link to a variable that does not exist yet opens the add form with the key filled.</summary>
public sealed class VariableLibraryEditKeyLinkTests : BunitContext
{
    private const string EntriesUrl = "api/variable-libraries/1/entries";
    private const string FilterParameter = "Filters%5B0%5D";
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditKeyLinkTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 1, EntryCount = 1 });
    }

    private void SetEntries(params string[] keys) =>
        _handler.SetJsonResponse(HttpMethod.Get, EntriesUrl, new PaginatedResult<VariableEntryDto>
        {
            Items = [.. keys.Select((key, index) => new VariableEntryDto { Id = index + 1, Key = key, Value = "v", VersionCount = 1 })],
            TotalCount = keys.Length,
            Page = 1,
            PageSize = 25
        });

    private IRenderedComponent<VariableLibraryEdit> RenderLinked(string key)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://test/variable-libraries/1?key={key}");
        return Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
    }

    [Fact]
    public async Task AMissingKey_OpensTheAddFormWithTheKeyFilled_AndTheValueFocused()
    {
        SetEntries();
        var cut = RenderLinked("API_HOST");

        cut.WaitForAssertion(() => Assert.Equal("API_HOST", cut.Find("input[aria-label='Key']").GetAttribute("value")));
        await cut.Instance.BackgroundRefresh;

        Assert.Equal(string.Empty, cut.Find("input[aria-label='Value']").GetAttribute("value") ?? string.Empty);
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations,
            invocation => invocation.Identifier == "Aetheus.focusById" && invocation.Arguments[0] as string == "library-new-entry-value"));
        // The filter is gone from the address.
        cut.WaitForAssertion(() => Assert.DoesNotContain("key=", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExistingKey_KeepsTheFilter_AndTheAddFormStaysEmpty()
    {
        SetEntries("API_HOST");
        var cut = RenderLinked("API_HOST");

        cut.WaitForAssertion(() => Assert.Contains("API_HOST", cut.Markup, StringComparison.Ordinal));
        await cut.Instance.BackgroundRefresh;

        Assert.Equal(string.Empty, cut.Find("input[aria-label='Key']").GetAttribute("value") ?? string.Empty);
        Assert.Contains("key=API_HOST", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Contains(_handler.RequestsSnapshot(), request =>
            request.Url.Contains(EntriesUrl, StringComparison.Ordinal) && request.Url.Contains(FilterParameter, StringComparison.Ordinal));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.focusById");
    }
}
