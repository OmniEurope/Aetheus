// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.Shared;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageRegistry;

/// <summary>
/// The pipeline run's package registry credential: issued only when the pipeline owner is an active
/// administrator, accepted only with its own run, project and expiry, under a key of its own (never
/// another purpose's key).
/// </summary>
public sealed class PackageRunTokensTests
{
    private const string SigningKey = "unit-test-signing-key-least-32-bytes!!";
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    private PackageRunTokens Tokens(string signingKey = SigningKey) =>
        new(new TokenHashKeys(new JwtOptions { SigningKey = signingKey }), _time);

    [Fact]
    public void AMintedToken_ValidatesToItsRunAndProject_AndPassesTheHarnessCharacterSet()
    {
        var tokens = Tokens();

        var token = tokens.Mint(42, 5);

        Assert.StartsWith(PackageRunTokens.Prefix, token, StringComparison.Ordinal);
        Assert.True(PackageRunTokens.IsRunToken(token));
        // publish-observability-package.sh refuses anything outside [0-9A-Za-z._~+/=-].
        Assert.Matches("^[0-9A-Za-z._~+/=-]+$", token);
        Assert.Equal((42, 5), tokens.Validate(token));
    }

    [Fact]
    public void AnExpiredToken_IsRefused_AndOneStillWithinItsLife_IsAccepted()
    {
        var tokens = Tokens();
        var token = tokens.Mint(42, 5);

        _time.Advance(PackageRunTokens.Ttl - TimeSpan.FromSeconds(1));
        Assert.NotNull(tokens.Validate(token));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(tokens.Validate(token));
    }

    [Theory]
    [InlineData(0)] // run id
    [InlineData(1)] // project id
    [InlineData(2)] // expiry
    public void ChangingAnySignedField_BreaksTheSignature(int field)
    {
        var tokens = Tokens();
        var parts = tokens.Mint(42, 5)[PackageRunTokens.Prefix.Length..].Split('.');
        parts[field] = field switch
        {
            0 => "43",
            1 => "6",
            _ => (long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 3600)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        Assert.Null(tokens.Validate(PackageRunTokens.Prefix + string.Join('.', parts)));
    }

    [Fact]
    public void ATamperedSignature_IsRefused()
    {
        var tokens = Tokens();
        var token = tokens.Mint(42, 5);
        var last = token[^1] == '0' ? '1' : '0';

        Assert.Null(tokens.Validate(token[..^1] + last));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aetheus_pat_abc")]
    [InlineData("aetheus-pkg-run.")]
    [InlineData("aetheus-pkg-run.42.5.9999999999")]
    [InlineData("aetheus-pkg-run.42.5.9999999999.zz")]
    [InlineData("aetheus-pkg-run.-1.5.9999999999.0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("aetheus-pkg-run.42.5.9999999999.000000000000000000000000000000000000000000000000000000000000000g")]
    [InlineData("aetheus-pkg-run.42.5.9999999999.0000000000000000000000000000000000000000000000000000000000000000.x")]
    public void AMalformedToken_IsRefused(string token) => Assert.Null(Tokens().Validate(token));

    [Fact]
    public void ATokenSignedUnderAnotherServerKey_IsRefused()
    {
        var token = Tokens("another-signing-key-of-at-least-32-bytes").Mint(42, 5);

        Assert.Null(Tokens().Validate(token));
    }

    [Fact]
    public void TheSignature_UsesTheKeyOfItsOwnPurpose()
    {
        var keys = new TokenHashKeys(new JwtOptions { SigningKey = SigningKey });
        const string message = "package-run-token:v1:42:5:1";

        Assert.NotEqual(
            Convert.ToBase64String(keys.Sign(TokenHashPurpose.PackageRunToken, message)),
            keys.Hash(TokenHashPurpose.PersonalAccessToken, message));
        Assert.NotEqual(keys.KeyFor(TokenHashPurpose.PackageRunToken), keys.KeyFor(TokenHashPurpose.PersonalAccessToken));
    }

    [Fact]
    public async Task TheIssuer_MintsForAnActiveAdministratorOwner()
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        repository.GetRunOwnerStateAsync(42, 5, Arg.Any<CancellationToken>()).Returns(PackageRunOwnerState.Allowed);
        var tokens = Tokens();

        var grant = await new PackageRunTokenIssuer(tokens, repository)
            .MintAsync(42, 5, TestContext.Current.CancellationToken);

        Assert.Null(grant.Refusal);
        Assert.Equal((42, 5), tokens.Validate(grant.Token!));
    }

    [Theory]
    [InlineData(PackageRunOwnerState.OwnerMissing, "no owner")]
    [InlineData(PackageRunOwnerState.OwnerInactive, "disabled")]
    [InlineData(PackageRunOwnerState.OwnerNotAdmin, "Admin role")]
    [InlineData(PackageRunOwnerState.RunNotFound, "does not belong")]
    public async Task TheIssuer_RefusesWithTheReason_WhenTheOwnerIsNotAnActiveAdministrator(
        PackageRunOwnerState state, string reason)
    {
        var repository = Substitute.For<IPackageRegistryRepository>();
        repository.GetRunOwnerStateAsync(42, 5, Arg.Any<CancellationToken>()).Returns(state);

        var grant = await new PackageRunTokenIssuer(Tokens(), repository)
            .MintAsync(42, 5, TestContext.Current.CancellationToken);

        Assert.Null(grant.Token);
        Assert.Contains(reason, grant.Refusal, StringComparison.Ordinal);
    }
}
