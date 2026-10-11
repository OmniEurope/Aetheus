// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;

namespace Aetheus.Front.Tests;

public class OrganizationDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationDetailTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    [Fact]
    public void Renders_WithOrganizationData()
    {
        var org = new OrganizationDetailDto(
            1, "Acme Corp", "acme", "A test org",
            DateTime.UtcNow, DateTime.UtcNow,
            [new OrganizationMemberDto(1, 10, "alice", "a@b.c", OrganizationRole.Owner, DateTime.UtcNow)],
            [new OrganizationProjectDto(1, "Project 1", ProjectStatus.Active)]);

        _handler.SetJsonResponse("api/organizations/1", org);
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 10, Username = "alice" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Project 1" }],
            TotalCount = 1
        });

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));

        Assert.Contains("Acme Corp", cut.Markup);
    }

    [Fact]
    public void Renders_NotFound_WhenOrgIsNull()
    {
        _handler.SetJsonResponse("api/organizations/99", (OrganizationDetailDto?)null!);
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 99));

        Assert.Contains("NotFound", cut.Markup);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
        handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            1, "X", "x", "", DateTime.UtcNow, DateTime.UtcNow, [], []));

        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));

        // The admin guard redirects before LoadAsync, so no organization fetch is issued.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/organizations"));
    }

    [Fact]
    public void OrganizationIdChange_ReloadsSameComponentInstance()
    {
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            1, "First org", "first", "", DateTime.UtcNow, DateTime.UtcNow, [], []));
        _handler.SetJsonResponse("api/organizations/2", new OrganizationDetailDto(
            2, "Second org", "second", "", DateTime.UtcNow, DateTime.UtcNow, [], []));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("Second org", cut.Markup));
        Assert.DoesNotContain("First org", cut.Markup);
    }

    [Fact]
    public async Task LoadFailure_StopsSpinnerAndRetryLoadsOrganization()
    {
        _handler.SetResponse("api/organizations/1", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup));
        Assert.Empty(cut.FindComponents<AetheusLoader>());
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            1, "Recovered", "recovered", "", DateTime.UtcNow, DateTime.UtcNow, [], []));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        var load = typeof(OrganizationDetail).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance, [])!);
        cut.Render();

        cut.WaitForAssertion(() => Assert.Contains("Recovered", cut.Markup));
        Assert.DoesNotContain("LoadFailed", cut.Markup);
    }
}
