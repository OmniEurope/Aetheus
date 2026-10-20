// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests;

/// <summary>What the application tells its own audience measurement.</summary>
public sealed class AuthStateProviderAnalyticsTests
{
    private static string Jwt(object payload)
    {
        static string Part(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))}.{Part(JsonSerializer.SerializeToUtf8Bytes(payload))}.s";
    }

    [Fact]
    public async Task ASignedInVisitor_IsNamedByTheTokensOpaqueIdentifier_AndNobodyAfterSignOut()
    {
        var js = Substitute.For<IJSRuntime>();
        var auth = new AuthStateProvider(js, NullLogger<AuthStateProvider>.Instance);
        Assert.Null(auth.VisitorForAnalytics());

        await auth.LoginAsync(Jwt(new Dictionary<string, object>
        {
            ["unique_name"] = "sony",
            ["aetheus:avid"] = "opaque-visitor-identifier",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }));

        Assert.Equal("opaque-visitor-identifier", auth.VisitorForAnalytics());
        // The measurement is told the visitor changed, before the caller navigates away from the sign-in page.
        await js.Received().InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "Aetheus.analyticsIdentityChanged", Arg.Any<object?[]?>());

        await auth.LogoutAsync();

        Assert.Null(auth.VisitorForAnalytics());
    }

    [Fact]
    public async Task AnExpiredToken_NamesNobody()
    {
        var auth = new AuthStateProvider(Substitute.For<IJSRuntime>(), NullLogger<AuthStateProvider>.Instance);

        await auth.LoginAsync(Jwt(new Dictionary<string, object>
        {
            ["aetheus:avid"] = "opaque-visitor-identifier",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()
        }));

        Assert.Null(auth.VisitorForAnalytics());
    }
}
