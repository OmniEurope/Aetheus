// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

/// <summary>R-76 (owner decision 2026-10-28): the organization page edits the "develop branch" setting, off by default, behind
/// a "?" help, and saves it with the rest of the General tab.</summary>
public sealed class OrganizationDetailDevelopBranchTests : BunitContext
{
    private const string SwitchId = "organization-auto-create-develop-branch";

    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationDetailDevelopBranchTests() =>
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);

    private IRenderedComponent<OrganizationDetail> RenderOrganization(bool autoCreateDevelopBranch)
    {
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            1, "Acme", "acme", "", DateTime.UtcNow, DateTime.UtcNow, [], [], autoCreateDevelopBranch));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotNull(DevelopSwitch(cut)));
        return cut;
    }

    private static IRenderedComponent<OmniSwitch<bool>> DevelopSwitch(IRenderedComponent<OrganizationDetail> cut) =>
        cut.FindComponents<OmniSwitch<bool>>().Single(item => item.Instance.Id == SwitchId);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSettingIsShownAsTheOrganizationStoresIt(bool stored)
    {
        var cut = RenderOrganization(stored);

        Assert.Equal(stored, DevelopSwitch(cut).Instance.Value);
    }

    [Fact]
    public void TheSettingHasItsLabelAndAHelpMark()
    {
        var cut = RenderOrganization(false);

        var field = cut.FindComponents<OmniFormField>().Single(item => item.Instance.For == SwitchId);
        Assert.Equal("OrganizationAutoCreateDevelopBranch", field.Instance.Label);
        Assert.Equal("OrganizationAutoCreateDevelopBranchHelp", field.Instance.Help);
        Assert.Contains("OrganizationAutoCreateDevelopBranchOption", DevelopSwitch(cut).Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SavingTheGeneralTab_SendsTheSetting(bool stored, bool toggledTo)
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/organizations/1",
            new OrganizationDto(1, "Acme", "acme", "", 0, 0, DateTime.UtcNow, DateTime.UtcNow));
        var cut = RenderOrganization(stored);

        await cut.InvokeAsync(() => DevelopSwitch(cut).Instance.ValueChanged.InvokeAsync(toggledTo));
        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == HttpMethod.Put.Method));
        var body = _handler.RequestDetails.Single(request => request.Method == HttpMethod.Put.Method
            && request.Url.EndsWith("api/organizations/1", StringComparison.OrdinalIgnoreCase)).Body!;
        var sent = JsonSerializer.Deserialize<UpdateOrganizationRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(toggledTo, sent.AutoCreateDevelopBranch);
    }
}
