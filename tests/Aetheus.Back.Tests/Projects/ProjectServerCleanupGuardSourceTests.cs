// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): the guards of a project deletion come from the current user's Write
/// permission on servers and from the control plane's configured public URLs. </summary>
public sealed class ProjectServerCleanupGuardSourceTests
{
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly HttpContextAccessor _http = new();

    private ProjectServerCleanupGuardSource Source(Dictionary<string, string?>? settings = null) =>
        new(_authz, _http, new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());

    private void SignedIn() => _http.HttpContext = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "dev")], "test")),
    };

    [Fact]
    public async Task TheWritableServers_AreThoseTheUserHoldsWriteOn()
    {
        SignedIn();
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns([3]);

        var guards = await Source().GetAsync(TestContext.Current.CancellationToken);

        Assert.True(guards.Writable.Allows(3));
        Assert.False(guards.Writable.Allows(4));
    }

    [Fact]
    public async Task EveryServer_IsWritable_OnlyWhenTheRoleGrantsWriteOnAll()
    {
        SignedIn();
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);

        Assert.True((await Source().GetAsync(TestContext.Current.CancellationToken)).Writable.Allows(4));
    }

    [Fact]
    public async Task WithoutAnAuthenticatedUser_NoServerIsWritable()
    {
        var guards = await Source().GetAsync(TestContext.Current.CancellationToken);

        Assert.False(guards.Writable.Allows(3));
        await _authz.DidNotReceiveWithAnyArgs().GetAccessibleResourceIdsAsync(default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheControlPlaneHosts_AreTheHostsOfItsPublicApiAndFrontEndUrls()
    {
        var guards = await Source(new()
        {
            ["Aetheus:PublicApiBaseUrl"] = "https://Aetheus-Api.Example.com/",
            ["Cors:FrontendUrl"] = "https://localhost:5401",
            ["Cors:Origins:0"] = "https://deploy.example.com",
            ["Cors:Origins:1"] = "https://accept.example.com:8443",
            ["Cors:Origins:2"] = "",
            ["Cors:Origins:3"] = "not a url",
        }).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["accept.example.com", "aetheus-api.example.com", "deploy.example.com", "localhost"], guards.ControlPlaneHosts.Order(StringComparer.Ordinal));
    }
}
