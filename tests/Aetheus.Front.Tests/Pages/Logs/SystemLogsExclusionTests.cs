// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-28 (exclusion rules, the second tab) and R-29 (the "View message" button is always blue) on the
/// system logs page.</summary>
public class SystemLogsExclusionTests : BunitContext
{
    private const string EntriesUrl = "api/system-logs/entries";
    private readonly BunitTestHelper.TestHandler _handler;

    public SystemLogsExclusionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/system-logs/files", new List<SystemLogFileDto>());
        _handler.SetJsonResponse(EntriesUrl, new PaginatedResult<SystemLogEntryDto>
        {
            TotalCount = 2,
            Items =
            [
                new SystemLogEntryDto(1, DateTime.UtcNow, "Error", "Aetheus.Back", "boom", "System.Exception: boom", null),
                new SystemLogEntryDto(2, DateTime.UtcNow, "Warning", "Aetheus.Back", "plain warning", null, null)
            ]
        });
    }

    /// <summary>R-29 (2026-10-30): the button is blue on every row, with or without an exception, and never red.</summary>
    [Fact]
    public void R29_ViewMessageButton_IsAlwaysBlue_WithOrWithoutAnException()
    {
        var cut = Render<SystemLogs>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button[title='ViewMessage']").Count));

        foreach (var button in cut.FindAll("button[title='ViewMessage']"))
        {
            Assert.Contains("omni-button--primary", button.ClassName, StringComparison.Ordinal);
            Assert.DoesNotContain("omni-button--danger", button.ClassName, StringComparison.Ordinal);
            Assert.DoesNotContain("omni-button--ghost", button.ClassName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task R28_WithAnActiveOrganization_TheFirstTabAsksForTheLinesNoRuleMatches()
    {
        await ActivateOrganizationAsync(7);

        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.True(Requested("organizationId=7", "excluded=false")));
    }

    [Fact]
    public async Task R28_TheSecondTab_AsksForTheLinesARuleMatches()
    {
        await ActivateOrganizationAsync(7);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "filtered"));

        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.True(Requested("organizationId=7", "excluded=true")));
        // A line already filtered out is excluded: its menu copies the message and offers no "exclude".
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button[title='MoreActions']").Count));
        cut.FindAll("button[title='MoreActions']")[0].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[title='CopyMessage']")));
        Assert.Empty(cut.FindAll("[title='SystemLogRuleExclude']"));
    }

    [Fact]
    public async Task R28_TheFirstTab_OffersToExcludeALineFromTheLinesMenu()
    {
        await ActivateOrganizationAsync(7);

        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button[title='MoreActions']").Count));
        cut.FindAll("button[title='MoreActions']")[0].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[title='SystemLogRuleExclude']")));
        Assert.NotEmpty(cut.FindAll("[title='CopyMessage']"));
    }

    [Theory]
    [InlineData("Slow request POST /api/tasks/claim: 1628 ms (threshold 1500 ms), status 200.", "Slow request POST /api/tasks/claim")]
    [InlineData("HTTP 409 for POST /api/pipelines: conflict", "HTTP 409 for POST /api/pipelines")]
    [InlineData("no colon in this message", "no colon in this message")]
    public void R28_SuggestPattern_KeepsTheKindOfLine_WithoutItsFigures(string message, string expected) =>
        Assert.Equal(expected, SystemLogs.SuggestPattern(message));

    [Fact]
    public void R28_SuggestPattern_NeverExceedsTheRuleLength() =>
        Assert.Equal(SaveSystemLogExclusionRuleRequest.MaxPatternLength, SystemLogs.SuggestPattern(new string('x', 1000)).Length);

    private async Task ActivateOrganizationAsync(int organizationId)
    {
        _handler.SetJsonResponse("api/organizations/me",
            new List<MyOrganizationDto> { new(organizationId, "Org", "org", OrganizationRole.Owner) });
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
    }

    private bool Requested(params string[] fragments) =>
        _handler.Requests.Any(request => request.Url.Contains(EntriesUrl, StringComparison.Ordinal)
            && fragments.All(fragment => request.Url.Contains(fragment, StringComparison.Ordinal)));
}
