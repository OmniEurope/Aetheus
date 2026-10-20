// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using UsersPage = Aetheus.Front.Components.Users.Users;

namespace Aetheus.Front.Tests.Pages;

public class UsersTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public UsersTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    [Fact]
    public void Renders_UsersList_WithData()
    {
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "User" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items =
            [
                new UserDto { Id = 1, Username = "admin", Email = "admin@test.com", IsActive = true, Roles = ["Admin"] },
                new UserDto { Id = 2, Username = "operator", Email = "op@test.com", IsActive = true, Roles = [] }
            ],
            TotalCount = 2,
            Page = 1,
            PageSize = 25
        });

        var cut = Render<UsersPage>();

        Assert.Contains("Users", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyUsersList()
    {
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "User" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 25
        });

        var cut = Render<UsersPage>();
        Assert.Contains("Users", cut.Markup);
    }

    [Fact]
    public void Renders_AtMostThreeRoleBadges_WithOverflowCount()
    {
        // The page reads the role names its Roles header filter offers.
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "Contributor", "Reader", "Ops" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items =
            [
                new UserDto
                {
                    Id = 1,
                    Username = "many-roles",
                    IsActive = true,
                    Roles = ["Admin", "Contributor", "Reader", "Operator", "Auditor"]
                }
            ],
            TotalCount = 1,
            Page = 1,
            PageSize = 50
        });

        var cut = Render<UsersPage>();
        cut.WaitForState(() => cut.Markup.Contains("many-roles"));

        Assert.Contains("Admin", cut.Markup);
        Assert.Contains("Contributor", cut.Markup);
        Assert.Contains("Reader", cut.Markup);
        Assert.Contains("+2", cut.Markup);
        Assert.Contains("Operator, Auditor", cut.Markup);
    }

    [Fact]
    public void Renders_UserEdit_NewUser()
    {
        _handler.SetPaginatedJsonResponse("api/roles",
        [
            new RoleDto { Id = 1, Name = "Admin" },
            new RoleDto { Id = 2, Name = "User" }
        ]);
        var cut = Render<UserEdit>();
        cut.WaitForState(() => cut.Markup.Contains("Admin"));

        // New-user mode renders the empty create form and a server-paged role selector.
        Assert.NotEmpty(cut.FindAll("input"));
        Assert.Contains("Admin", cut.Markup);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/roles"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/effective-permissions"));
    }

    [Fact]
    public void Renders_UserEdit_ExistingUser()
    {
        _handler.SetPaginatedJsonResponse("api/roles",
        [
            new RoleDto { Id = 1, Name = "Admin" },
            new RoleDto { Id = 2, Name = "User" }
        ]);
        _handler.SetJsonResponse("api/users/1", new UserDto
        {
            Id = 1,
            Username = "admin",
            Email = "admin@test.com",
            IsActive = true,
            Roles = ["Admin"]
        });

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("admin"));

        Assert.Contains("admin", cut.Markup);
    }
}
