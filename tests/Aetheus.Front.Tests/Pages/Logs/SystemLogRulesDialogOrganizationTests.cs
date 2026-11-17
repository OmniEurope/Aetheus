// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Logs;

/// <summary>
/// R-34: the exclusion rules dialog needs an organization. Without one it offers the choice itself, and the rules
/// of the organization chosen there are read. The mocked HTTP proves what the dialog asks for, not the real endpoint.
/// </summary>
public sealed class SystemLogRulesDialogOrganizationTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public SystemLogRulesDialogOrganizationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto>());
        _handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto> { new(7, "Acme", []) });
        _handler.SetJsonResponse("api/system-log-rules?organizationId=7", new List<SystemLogExclusionRuleDto>());
    }

    [Fact]
    public async Task WithoutAnOrganization_TheDialogOffersTheChoice_AndReadsTheRulesOfTheOneChosen()
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        var cut = Render<SystemLogRulesDialog>();

        var picker = cut.WaitForElement("[data-testid='organization-required-picker'] select", BunitTestHelper.AsyncRenderWait);
        Assert.Contains("Choose an organization to manage its exclusion rules.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("system-log-rules", StringComparison.Ordinal));

        // An option's value is its position in the list, not the organization id: choose it by what it says.
        picker.Change(picker.QuerySelectorAll("option").Single(option => option.TextContent.Trim() == "Acme").GetAttribute("value"));

        await _handler.WhenGetAsync("api/system-log-rules?organizationId=7");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='organization-required-picker']")), BunitTestHelper.AsyncRenderWait);
    }
}
