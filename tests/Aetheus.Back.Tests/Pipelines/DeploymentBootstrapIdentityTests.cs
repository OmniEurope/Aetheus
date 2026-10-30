// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The identity a deployment hands to the candidate colour so its authenticated smoke exercises real
/// JWT, RBAC and SignalR paths. It exists as a derivation rather than a value because the step that
/// starts the colour and the step that probes it are separate tasks, and the only channel between two
/// tasks is a run variable echoed into the run log.
/// </summary>
public sealed class DeploymentBootstrapIdentityTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();

    private static readonly DateTime Now = new(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The whole point: two tasks agree without exchanging anything. If this drifted, the colour would
    /// start with one credential and the probe would try another, and the deployment would fail after
    /// the cutover rather than before it.
    /// </summary>
    [Fact]
    public void SameRun_DerivesTheSameCredentialForEveryStep()
    {
        var configuration = Configuration(("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef"));

        var up = DeploymentBootstrapIdentity.For(configuration, 1797, Now);
        var smoke = DeploymentBootstrapIdentity.For(configuration, 1797, Now.AddMinutes(4));

        Assert.Equal(up.User, smoke.User);
        Assert.Equal(up.Password, smoke.Password);
        Assert.Equal(up.Stamp, smoke.Stamp);
    }

    /// <summary>Everyone signs in by e-mail, the deployment identity included: its name is an address in the
    /// reserved ".invalid" domain, and the backend's bootstrap login accepts exactly that.</summary>
    [Fact]
    public void DerivedIdentity_IsAnEmailAddress_ThatTheBootstrapLoginAccepts()
    {
        var configuration = Configuration(("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef"));
        var identity = DeploymentBootstrapIdentity.For(configuration, 1797, Now);
        Assert.EndsWith("@bootstrap.invalid", identity.User, StringComparison.Ordinal);

        var backend = Configuration(
            ("Auth:DeploymentBootstrapUser", identity.User),
            ("Auth:DeploymentBootstrapPassword", identity.Password),
            ("Auth:DeploymentBootstrapExpiresAtUtc", identity.ExpiresAtUtc));
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));

        Assert.NotNull(Aetheus.Back.Components.Auth.BootstrapCredentialPolicy.Match(
            new Aetheus.Shared.Components.Settings.LoginRequest { Email = identity.User.ToUpperInvariant(), Password = identity.Password },
            backend, time, hasDbUsers: true));
        Assert.Null(Aetheus.Back.Components.Auth.BootstrapCredentialPolicy.Match(
            new Aetheus.Shared.Components.Settings.LoginRequest { Email = identity.User.Split('@')[0], Password = identity.Password },
            backend, time, hasDbUsers: true));
    }

    [Fact]
    public void DifferentRuns_DeriveDifferentCredentials()
    {
        var configuration = Configuration(("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef"));

        var first = DeploymentBootstrapIdentity.For(configuration, 1797, Now);
        var second = DeploymentBootstrapIdentity.For(configuration, 1798, Now);

        Assert.NotEqual(first.User, second.User);
        Assert.NotEqual(first.Password, second.Password);
        Assert.NotEqual(first.Stamp, second.Stamp);
    }

    /// <summary>
    /// The run id is public on a production deployment, so an empty-keyed HMAC would make this
    /// credential predictable to anyone reading the run page. A missing key is a misconfiguration, not
    /// a downgrade to a weaker identity.
    /// </summary>
    [Fact]
    public void NoConfiguredKey_FailsClosedInsteadOfDerivingFromNothing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DeploymentBootstrapIdentity.For(Configuration(), 1797, Now));

        Assert.Contains("Deployment:BootstrapIdentityKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedKey_IsPreferredOverTheSharedEncryptionSecret()
    {
        var shared = DeploymentBootstrapIdentity.For(
            Configuration(("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef")), 1797, Now);
        var dedicated = DeploymentBootstrapIdentity.For(
            Configuration(
                ("Deployment:BootstrapIdentityKey", "fedcba9876543210fedcba9876543210"),
                ("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef")),
            1797, Now);

        Assert.NotEqual(shared.Password, dedicated.Password);
    }

    /// <summary>
    /// The credential has to be long enough that it cannot be guessed, and the window short enough
    /// that a JWT minted from it cannot outlive the deployment that created it.
    /// </summary>
    [Fact]
    public void Credential_IsHighEntropyAndTheWindowIsBounded()
    {
        var identity = DeploymentBootstrapIdentity.For(
            Configuration(("Auth:EncryptionKey", "0123456789abcdef0123456789abcdef")), 1797, Now);

        Assert.StartsWith("deploy-smoke-", identity.User, StringComparison.Ordinal);
        Assert.Equal(64, identity.Password.Length);
        Assert.Equal(64, identity.Stamp.Length);
        Assert.All(identity.Password, character => Assert.Contains(character, "0123456789abcdef"));
        Assert.NotEqual(identity.Password, identity.Stamp);
        Assert.Equal("2026-08-16T02:10:00Z", identity.ExpiresAtUtc);
        Assert.True(DeploymentBootstrapIdentity.LifetimeMinutes <= 15);
    }
}
