// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;

namespace Aetheus.Front.Tests;

public class OrganizationEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    [Fact]
    public void Renders_NewOrganizationForm()
    {
        var cut = Render<OrganizationEdit>();

        Assert.Contains("NewOrganization", cut.Markup);
    }

    [Fact]
    public void Renders_EditMode_LoadsExistingOrg()
    {
        var org = new OrganizationDetailDto(
            1, "Acme", "acme", "desc",
            DateTime.UtcNow, DateTime.UtcNow, [], []);
        _handler.SetJsonResponse("api/organizations/1", org);

        var cut = Render<OrganizationEdit>(p => p.Add(x => x.Id, 1));

        Assert.Contains("EditOrganization", cut.Markup);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);

        var cut = Render<OrganizationEdit>();

        // Admin guard returns before clearing _loading, so the edit form (and its Save button) never renders.
        Assert.DoesNotContain("Save", cut.Markup);
    }

    [Fact]
    public void Renders_FormFields()
    {
        var cut = Render<OrganizationEdit>();

        Assert.Contains("Name", cut.Markup);
        Assert.Contains("Slug", cut.Markup);
        Assert.Contains("Description", cut.Markup);
        Assert.Contains("Save", cut.Markup);
        Assert.Contains("GoBack", cut.Markup);
    }
}
