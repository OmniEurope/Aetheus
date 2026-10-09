// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

public sealed class AuthControllerExternalLoginSecurityTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IServerEnrollmentService _enrollment = Substitute.For<IServerEnrollmentService>();

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ExternalLogin_MissingGatewaySecret_IsNotFoundAndFailsClosed(string? configuredSecret)
    {
        var controller = Build(new Dictionary<string, string?>
        {
            ["Auth:ExternalLogin:GatewaySecret"] = configuredSecret,
        });

        var result = await controller.ExternalLogin(Request(), TestContext.Current.CancellationToken);

        // R2-120: a feature that is not configured answers 404 with the ApiError body, never 503.
        var response = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.IsType<Aetheus.Shared.Components.Shared.ApiError>(response.Value);
        await _auth.DidNotReceiveWithAnyArgs().HandleExternalLoginAsync(
            default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task ExternalLogin_UntrustedCaller_IsRejected(string? suppliedSecret)
    {
        var controller = Build(new Dictionary<string, string?>
        {
            ["Auth:ExternalLogin:GatewaySecret"] = "trusted-gateway-secret"
        });
        if (suppliedSecret is not null)
            controller.Request.Headers["X-Aetheus-External-Auth"] = suppliedSecret;

        var result = await controller.ExternalLogin(Request(), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        await _auth.DidNotReceiveWithAnyArgs().HandleExternalLoginAsync(
            default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExternalLogin_AuthenticatedGateway_ForwardsVerifiedClaims()
    {
        var controller = Build(new Dictionary<string, string?>
        {
            ["Auth:ExternalLogin:GatewaySecret"] = "trusted-gateway-secret"
        });
        controller.Request.Headers["X-Aetheus-External-Auth"] = "trusted-gateway-secret";
        _auth.HandleExternalLoginAsync("oidc", "subject-1", "Alice", "alice@example.test", Arg.Any<CancellationToken>())
            .Returns(new LoginResponse { Token = "token" });

        var result = await controller.ExternalLogin(Request(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _auth.Received(1).HandleExternalLoginAsync(
            "oidc", "subject-1", "Alice", "alice@example.test", Arg.Any<CancellationToken>());
    }

    private AuthController Build(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AuthController(_auth, _enrollment, config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ExternalLoginRequest Request() => new()
    {
        Provider = "oidc",
        SubjectId = "subject-1",
        DisplayName = "Alice",
        Email = "alice@example.test"
    };
}
