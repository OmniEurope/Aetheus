// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary>Recette R1-37: the client's logout reaches the server, which ends the signed-in user's session.</summary>
public sealed class AuthControllerLogoutTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    private Task<IActionResult> LogoutAs(string nameIdentifier) =>
        new AuthController(_auth, Substitute.For<IServerEnrollmentService>(), new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, nameIdentifier)], "test"))
                }
            }
        }.Logout(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_signed_in_user_logging_out_ends_their_session()
    {
        Assert.IsType<NoContentResult>(await LogoutAs("12"));

        await _auth.Received(1).LogoutAsync(12, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_bootstrap_admin_has_no_user_id_and_nothing_to_forget()
    {
        Assert.IsType<NoContentResult>(await LogoutAs("bootstrap"));

        await _auth.DidNotReceive().LogoutAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
