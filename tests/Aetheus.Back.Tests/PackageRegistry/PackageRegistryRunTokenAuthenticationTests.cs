// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Components.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// The registry authentication handler with a pipeline run's credential, driven through the real
/// <c>AuthenticateAsync</c>: accepted only while its run is running in its project and the pipeline owner is
/// an active administrator (the repository's single check, stubbed here), as a principal
/// without any role, from every header the NuGet and npm clients send it in.
/// </summary>
public sealed class PackageRegistryRunTokenAuthenticationTests
{
    private const int RunId = 42;
    private const int ProjectId = 5;
    private readonly IPackageRegistryRepository _repository = Substitute.For<IPackageRegistryRepository>();
    private readonly IPersonalAccessTokenService _pat = Substitute.For<IPersonalAccessTokenService>();
    private readonly PackageRunTokens _tokens = new(
        new TokenHashKeys(new JwtOptions { SigningKey = "unit-test-signing-key-least-32-bytes!!" }),
        TimeProvider.System);

    private async Task<AuthenticateResult> AuthenticateAsync(params (string Header, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (header, value) in headers) context.Request.Headers[header] = value;
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new PackageRegistryAuthenticationHandler(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, _pat, _tokens, _repository);
        await handler.InitializeAsync(
            new AuthenticationScheme(
                PackageRegistryAuthenticationHandler.SchemeName,
                PackageRegistryAuthenticationHandler.SchemeName,
                typeof(PackageRegistryAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private void RunIsAuthorized(bool active) =>
        _repository.IsRunAuthorizedAsync(RunId, ProjectId, Arg.Any<CancellationToken>()).Returns(active);

    private static string Basic(string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("aetheus:" + password));

    [Fact]
    public async Task ARunningRunsToken_AuthenticatesAsThatRun_WithoutAnyRole()
    {
        RunIsAuthorized(true);

        var result = await AuthenticateAsync(("Authorization", "Bearer " + _tokens.Mint(RunId, ProjectId)));

        Assert.True(result.Succeeded);
        var user = result.Principal!;
        Assert.Equal("pipeline-run-42", user.Identity!.Name);
        Assert.Equal("42", user.FindFirst(PackageRunTokens.RunClaim)!.Value);
        Assert.Equal("5", user.FindFirst(PackageRunTokens.ProjectClaim)!.Value);
        Assert.DoesNotContain(user.Claims, claim => claim.Type == ClaimTypes.Role);
        Assert.False(user.IsInRole("Admin"));
        Assert.True(PackageRegistryPolicies.IsPipelineRun(user));
        await _pat.DidNotReceiveWithAnyArgs().ValidateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheToken_IsReadFromTheBasicPasswordAndTheNuGetApiKey_AsTheClientsSendIt()
    {
        RunIsAuthorized(true);
        var token = _tokens.Mint(RunId, ProjectId);

        Assert.True((await AuthenticateAsync(("Authorization", Basic(token)))).Succeeded);
        Assert.True((await AuthenticateAsync(("X-NuGet-ApiKey", token))).Succeeded);
    }

    [Fact]
    public async Task ARunThatFinishedOrWhoseOwnerIsNoLongerAnActiveAdmin_IsRefused()
    {
        RunIsAuthorized(false);

        var result = await AuthenticateAsync(("Authorization", "Bearer " + _tokens.Mint(RunId, ProjectId)));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task AForgedToken_IsRefused_BeforeTheDatabaseIsAsked()
    {
        RunIsAuthorized(true);
        var token = _tokens.Mint(RunId, ProjectId);
        var forged = token.Replace(".42.5.", ".42.6.", StringComparison.Ordinal);

        var result = await AuthenticateAsync(("Authorization", "Bearer " + forged));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        await _repository.DidNotReceiveWithAnyArgs().IsRunAuthorizedAsync(default, default, TestContext.Current.CancellationToken);
    }
}
