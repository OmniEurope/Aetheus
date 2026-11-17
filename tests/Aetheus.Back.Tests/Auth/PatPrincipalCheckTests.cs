// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PersonalAccessTokens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

// R-54: one rule recognises a PAT principal, wherever a PAT is refused.
public sealed class PatPrincipalCheckTests
{
    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7"), .. claims], authenticationType));

    [Fact]
    public void PatSchemeWithoutScopeClaim_IsPat()
        => Assert.True(PatPrincipalCheck.IsPatPrincipal(Principal(PatConstants.SchemeName)));

    [Fact]
    public void TokenIdClaimUnderAnotherScheme_IsPat()
        => Assert.True(PatPrincipalCheck.IsPatPrincipal(Principal("GitBasic", new Claim(PatConstants.TokenIdClaimType, "3"))));

    [Fact]
    public void ScopeClaimOnly_IsPat()
        => Assert.True(PatPrincipalCheck.IsPatPrincipal(Principal("other", new Claim(PatConstants.ScopeClaimType, PatConstants.ReadOnlyScopeValue))));

    [Fact]
    public void InteractiveJwtSession_IsNotPat()
        => Assert.False(PatPrincipalCheck.IsPatPrincipal(Principal("Bearer")));

    [Fact]
    public async Task TokenManagement_RefusesPatPrincipalWithoutScopeClaim()
    {
        var service = Substitute.For<IPersonalAccessTokenService>();
        var controller = new PersonalAccessTokensController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(PatConstants.SchemeName) }
            }
        };

        var action = await controller.GetMine(new PaginationRequest(), TestContext.Current.CancellationToken);

        var refused = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        await service.DidNotReceive().GetForUserAsync(Arg.Any<int>(), Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renew_RefusesPatPrincipalWithoutScopeClaim()
    {
        var authService = Substitute.For<IAuthService>();
        var controller = new AuthController(
            authService, Substitute.For<IServerEnrollmentService>(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(PatConstants.SchemeName) }
            }
        };

        var action = await controller.Renew(TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedObjectResult>(action.Result);
        await authService.DidNotReceive().RenewTokenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
