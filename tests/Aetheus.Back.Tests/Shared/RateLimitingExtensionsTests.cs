// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Configuration;
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.Tests.Extensions;

public sealed class RateLimitingExtensionsTests
{
    [Fact]
    public void ResolveGlobalLimit_PartitionsAgentByServerWithDedicatedBound()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("ServerId", "4")],
                AgentTokenAuthenticationHandler.SchemeName))
        };

        var result = RateLimitingExtensions.ResolveGlobalLimit(context);

        Assert.Equal("agent:4", result.PartitionKey);
        Assert.Equal(BackendRuntimeDefaults.AuthenticatedAgentRateLimit, result.PermitLimit);
    }

    [Fact]
    public void ResolveGlobalLimit_KeepsHumanUsersOnTheirOwnLowerPartition()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin")],
                "Bearer"))
        };

        var result = RateLimitingExtensions.ResolveGlobalLimit(context);

        Assert.Equal("u:admin", result.PartitionKey);
        Assert.Equal(BackendRuntimeDefaults.AuthenticatedUserRateLimit, result.PermitLimit);
    }

    [Fact]
    public void BearerSelector_AgentPolicyEndpoint_ForwardsBeforeGlobalRateLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer opaque-agent-token";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute
            {
                Policy = AgentTokenAuthenticationHandler.SchemeName
            }),
            "agent endpoint"));

        var scheme = BearerAuthenticationSchemeSelector.Resolve(context);

        Assert.Equal(AgentTokenAuthenticationHandler.SchemeName, scheme);
    }

    [Fact]
    public void BearerSelector_RegularEndpoint_DoesNotTreatOpaqueTokenAsAgent()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer opaque-token";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            EndpointMetadataCollection.Empty,
            "regular endpoint"));

        var scheme = BearerAuthenticationSchemeSelector.Resolve(context);

        Assert.Null(scheme);
    }

    [Fact]
    public void BearerSelector_PersonalAccessToken_PreservesPatForwarding()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {PatConstants.TokenPrefix}opaque-token";

        var scheme = BearerAuthenticationSchemeSelector.Resolve(context);

        Assert.Equal(PatConstants.SchemeName, scheme);
    }

    [Fact]
    public void ResolveOtlpPartitionKey_CannotBeRotatedWithUntrustedIngestHeader()
    {
        var first = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        first.Request.Headers["x-aetheus-ingest-key"] = "first-attacker-value";
        var second = new DefaultHttpContext();
        second.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        second.Request.Headers["x-aetheus-ingest-key"] = "second-attacker-value";

        var firstKey = RateLimitingExtensions.ResolveOtlpPartitionKey(first);
        var secondKey = RateLimitingExtensions.ResolveOtlpPartitionKey(second);

        Assert.Equal("otlp-address:192.0.2.10", firstKey);
        Assert.Equal(firstKey, secondKey);
        Assert.DoesNotContain("attacker", firstKey, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOtlpPartitionKey_IsolatesDifferentRemoteAddresses()
    {
        var first = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        var second = new DefaultHttpContext();
        second.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.11");

        Assert.NotEqual(
            RateLimitingExtensions.ResolveOtlpPartitionKey(first),
            RateLimitingExtensions.ResolveOtlpPartitionKey(second));
    }

    [Theory]
    [InlineData(nameof(AuthController.Login), "login")]
    [InlineData(nameof(AuthController.RegisterServer), "enrollment")]
    [InlineData(nameof(AuthController.Renew), "auth-token")]
    [InlineData(nameof(AuthController.RefreshToken), "auth-token")]
    public void AuthEndpoints_UseIndependentRateLimitPolicies(string methodName, string expectedPolicy)
    {
        var method = typeof(AuthController).GetMethod(methodName)!;
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true)
            .Cast<EnableRateLimitingAttribute>());

        Assert.Equal(expectedPolicy, attribute.PolicyName);
    }

    [Fact]
    public void Login_UsesValidationAuditFilterForPreActionFailures()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.Login))!;
        var filter = Assert.Single(method.GetCustomAttributes(typeof(ServiceFilterAttribute), true)
            .Cast<ServiceFilterAttribute>());

        Assert.Equal(typeof(LoginValidationAuditFilter), filter.ServiceType);
    }

    [Theory]
    [InlineData("/api/auth/external-login", "external-login-global")]
    [InlineData("/API/AUTH/EXTERNAL-LOGIN", "external-login-global")]
    [InlineData("/api/servers", "ip:192.0.2.10")]
    [InlineData("", "ip:192.0.2.10")]
    public void ResolveGlobalPartition_SharesOneBucketForExternalSignIn(string path, string expectedKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Path = path;

        Assert.Equal(expectedKey, RateLimitingExtensions.ResolveGlobalPartition(context).PartitionKey);
    }

    [Fact]
    public void ResolveGlobalLimit_AnonymousWithoutAddress_FallsBackToUnknown()
    {
        var result = RateLimitingExtensions.ResolveGlobalLimit(new DefaultHttpContext());

        Assert.Equal("ip:unknown", result.PartitionKey);
        Assert.Equal(BackendRuntimeDefaults.AuthenticatedUserRateLimit, result.PermitLimit);
    }

    [Fact]
    public void ResolveAuthTokenPartitionKey_AuthenticatedUser_IsKeyedByName()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "Bearer"))
        };

        Assert.Equal("user:admin", RateLimitingExtensions.ResolveAuthTokenPartitionKey(context));
    }

    [Fact]
    public void ResolveAuthTokenPartitionKey_Anonymous_IsKeyedByAddressOrUnknown()
    {
        var withAddress = new DefaultHttpContext();
        withAddress.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        Assert.Equal("ip:192.0.2.10", RateLimitingExtensions.ResolveAuthTokenPartitionKey(withAddress));
        Assert.Equal("ip:unknown", RateLimitingExtensions.ResolveAuthTokenPartitionKey(new DefaultHttpContext()));
    }

    [Fact]
    public async Task WriteRejection_OnLogin_AuditsAddressAndCleanedUserAgentThenAnswersJson()
    {
        var audit = Substitute.For<IAuditService>();
        var context = RejectedContext("/api/auth/login", audit);
        context.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.HttpContext.Request.Headers.UserAgent = "agent\u0007/1.0" + new string('x', 300);

        await RateLimitingExtensions.WriteRejectionAsync(context, TestContext.Current.CancellationToken);

        var expectedUserAgent = ("agent/1.0" + new string('x', 300))[..200];
        await audit.Received(1).LogAsync("LoginFailed.RateLimited", "Authentication", null,
            $"IP: 192.0.2.10, UA: {expectedUserAgent}", Arg.Any<CancellationToken>());
        Assert.Equal("application/json", context.HttpContext.Response.ContentType?.Split(';')[0]);
        Assert.Contains("Too many authentication attempts", ReadBody(context.HttpContext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRejection_OnLoginWithoutAddressOrUserAgent_AuditsUnknowns()
    {
        var audit = Substitute.For<IAuditService>();
        var context = RejectedContext("/API/AUTH/LOGIN", audit);

        await RateLimitingExtensions.WriteRejectionAsync(context, TestContext.Current.CancellationToken);

        await audit.Received(1).LogAsync("LoginFailed.RateLimited", "Authentication", null,
            "IP: unknown, UA: unknown", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteRejection_ElsewhereThanLogin_AnswersJsonWithoutAudit()
    {
        var audit = Substitute.For<IAuditService>();
        var context = RejectedContext("/api/servers", audit);

        await RateLimitingExtensions.WriteRejectionAsync(context, TestContext.Current.CancellationToken);

        Assert.Empty(audit.ReceivedCalls());
        Assert.Contains("Too many authentication attempts", ReadBody(context.HttpContext), StringComparison.Ordinal);
    }

    private static OnRejectedContext RejectedContext(string path, IAuditService audit)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(audit).BuildServiceProvider()
        };
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();
        return new OnRejectedContext { HttpContext = httpContext, Lease = RateLimitPartition.GetNoLimiter("test").Factory("test").AttemptAcquire() };
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
