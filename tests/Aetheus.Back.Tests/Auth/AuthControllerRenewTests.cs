// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>Only a token naming a numeric user is renewed, and only while the service still knows that
/// user as active.</summary>
public sealed class AuthControllerRenewTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    private Task<ActionResult<LoginResponse>> RenewAs(params Claim[] claims) =>
        new AuthController(_auth, Substitute.For<IServerEnrollmentService>(), new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        }.Renew(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(null)]
    [InlineData("bootstrap")]
    public async Task ATokenWithoutANumericUser_IsNotRenewed(string? nameIdentifier)
    {
        var claims = nameIdentifier is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, nameIdentifier)];

        var result = await RenewAs(claims);

        Assert.Equal("Token not eligible for renewal.", Assert.IsType<ApiError>(Assert.IsType<UnauthorizedObjectResult>(result.Result).Value).Message);
        await _auth.DidNotReceiveWithAnyArgs().RenewTokenAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AUserNoLongerActive_IsNotRenewed()
    {
        _auth.RenewTokenAsync(12, Arg.Any<CancellationToken>()).Returns((LoginResponse?)null);

        var result = await RenewAs(new Claim(ClaimTypes.NameIdentifier, "12"));

        Assert.Equal("User no longer exists or is disabled.", Assert.IsType<ApiError>(Assert.IsType<UnauthorizedObjectResult>(result.Result).Value).Message);
    }

    [Fact]
    public async Task AnActiveUser_GetsTheFreshToken()
    {
        var renewed = new LoginResponse();
        _auth.RenewTokenAsync(12, Arg.Any<CancellationToken>()).Returns(renewed);

        var result = await RenewAs(new Claim(ClaimTypes.NameIdentifier, "12"));

        Assert.Same(renewed, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
