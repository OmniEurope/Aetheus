// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The bootstrap identity a smoke or blue-green start step receives: the pipeline's own credentials
/// when it declares them all, otherwise the identity derived for the run, never overriding what the
/// pipeline did declare, and every secret registered for masking.
/// </summary>
public class PipelineDeploymentBootstrapIdentityTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();

    private PipelineHostOperationTaskFactory Build(Dictionary<string, string?> settings) =>
        new(
            Substitute.For<IPipelineRepository>(),
            Substitute.For<IPipelineGitService>(),
            Substitute.For<IEncryptionService>(),
            _masking,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Substitute.For<IAppDeployEnvProvider>(),
            Substitute.For<ILogger<PipelineHostOperationTaskFactory>>(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));

    private static Dictionary<string, string?> WithKey => new() { ["Deployment:BootstrapIdentityKey"] = "bootstrap-key-for-tests" };

    [Fact]
    public void Smoke_WithoutDeclaredCredentials_ReceivesTheDerivedIdentity()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.True(Build(WithKey).TryAddDeploymentBootstrapIdentity(7, vars, forSmoke: true, out var error));

        var identity = DeploymentBootstrapIdentity.For(
            new ConfigurationBuilder().AddInMemoryCollection(WithKey).Build(), 7, Now);
        Assert.Equal(string.Empty, error);
        Assert.Equal(identity.User, vars["AETHEUS_SMOKE_ADMIN_USER"]);
        Assert.Equal(identity.Password, vars["AETHEUS_SMOKE_ADMIN_PASSWORD"]);
        Assert.Equal(2, vars.Count);
        _masking.Received(1).RegisterRuntimeSecret(7, identity.Password);
        _masking.Received(1).RegisterRuntimeSecret(7, identity.Stamp);
    }

    [Fact]
    public void Start_FillsTheFourBootstrapNames_KeepingWhatThePipelineDeclared()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["DEPLOYMENT_BOOTSTRAP_USER"] = "declared-user" };

        Assert.True(Build(WithKey).TryAddDeploymentBootstrapIdentity(8, vars, forSmoke: false, out _));

        var identity = DeploymentBootstrapIdentity.For(
            new ConfigurationBuilder().AddInMemoryCollection(WithKey).Build(), 8, Now);
        Assert.Equal("declared-user", vars["DEPLOYMENT_BOOTSTRAP_USER"]);
        Assert.Equal(identity.Password, vars["DEPLOYMENT_BOOTSTRAP_PASSWORD"]);
        Assert.Equal(identity.Stamp, vars["BOOTSTRAP_STAMP"]);
        Assert.Equal("2026-10-16T08:10:00Z", vars["DEPLOYMENT_BOOTSTRAP_EXPIRES_AT_UTC"]);
    }

    [Fact]
    public void Smoke_WithEveryCredentialDeclared_KeepsThemAndMasksThePassword()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AETHEUS_SMOKE_ADMIN_USER"] = "smoke-admin",
            ["AETHEUS_SMOKE_ADMIN_PASSWORD"] = "smoke-password"
        };

        Assert.True(Build([]).TryAddDeploymentBootstrapIdentity(9, vars, forSmoke: true, out _));

        Assert.Equal("smoke-admin", vars["AETHEUS_SMOKE_ADMIN_USER"]);
        Assert.Equal("smoke-password", vars["AETHEUS_SMOKE_ADMIN_PASSWORD"]);
        _masking.Received(1).RegisterRuntimeSecret(9, "smoke-password");
        _masking.ReceivedWithAnyArgs(1).RegisterRuntimeSecret(default, default!);
    }

    [Fact]
    public void WithoutAnySigningKey_IsRefusedWithTheReason()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["AETHEUS_SMOKE_ADMIN_USER"] = "smoke-admin" };

        Assert.False(Build([]).TryAddDeploymentBootstrapIdentity(10, vars, forSmoke: true, out var error));

        Assert.Equal(
            "No signing key configured for the deployment bootstrap identity (set Deployment:BootstrapIdentityKey or Auth:EncryptionKey).",
            error);
        Assert.False(vars.ContainsKey("AETHEUS_SMOKE_ADMIN_PASSWORD"));
        _masking.DidNotReceiveWithAnyArgs().RegisterRuntimeSecret(default, default!);
    }
}
