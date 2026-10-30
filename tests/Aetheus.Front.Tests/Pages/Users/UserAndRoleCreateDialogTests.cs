// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Users;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Users;

public class UserAndRoleCreateDialogTests : BunitContext
{
    public UserAndRoleCreateDialogTests() =>
        BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true).SetOrganizations();

    [Fact]
    public void RoleCreateDialog_Renders_Form()
    {
        var cut = Render<RoleCreateDialog>();

        Assert.NotEmpty(cut.FindAll("input"));
    }
}
