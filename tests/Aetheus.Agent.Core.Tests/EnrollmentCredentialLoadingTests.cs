// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Persisted credentials restore the agent's identity, and a credentials file that cannot be
/// unprotected or parsed is quarantined so the next start re-enrols instead of looping without
/// credentials. Each test owns its work directory.</summary>
public sealed class EnrollmentCredentialLoadingTests : IDisposable
{
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), $"agent-creds-{Guid.NewGuid():N}");
    private readonly IServerApiClient _apiClient = Substitute.For<IServerApiClient>();
    private readonly ICredentialProtector _protector = Substitute.For<ICredentialProtector>();
    private readonly AgentState _state = new(TimeProvider.System);

    public EnrollmentCredentialLoadingTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true);
    }

    private string CredentialPath => Path.Combine(_workDirectory, ".credentials");

    private EnrollmentService Service() => new(
        _apiClient,
        Options.Create(new AetheusAgentOptions { ServerUrl = "http://test", WorkDirectory = _workDirectory }),
        _state,
        new ConfigurationBuilder().Build(),
        _protector,
        Substitute.For<IShellRunner>(),
        TimeProvider.System,
        NullLogger<EnrollmentService>.Instance);

    private void StoreCredentials(string plainText)
    {
        File.WriteAllBytes(CredentialPath, [1, 2, 3]);
        _protector.Unprotect(Arg.Any<byte[]>()).Returns(Encoding.UTF8.GetBytes(plainText));
    }

    [Fact]
    public async Task NoCredentialsFile_LeavesTheAgentUnenrolled()
    {
        await Service().LoadPersistedCredentialsAsync(TestContext.Current.CancellationToken);

        Assert.False(_state.IsEnrolled);
        _protector.DidNotReceiveWithAnyArgs().Unprotect(default!);
    }

    [Fact]
    public async Task ValidCredentials_RestoreTheIdentityAndTheBearerToken()
    {
        StoreCredentials("""{"ServerId":5,"BearerToken":"tok-123"}""");

        await Service().LoadPersistedCredentialsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, _state.ServerId);
        Assert.Equal("tok-123", _state.BearerToken);
        _apiClient.Received(1).SetBearerToken("tok-123");
    }

    [Fact]
    public async Task CredentialsMissingTheToken_RestoreNothing()
    {
        StoreCredentials("""{"ServerId":5}""");

        await Service().LoadPersistedCredentialsAsync(TestContext.Current.CancellationToken);

        Assert.False(_state.IsEnrolled);
        _apiClient.DidNotReceiveWithAnyArgs().SetBearerToken(default!);
        Assert.True(File.Exists(CredentialPath));
    }

    [Fact]
    public async Task CredentialsThatCannotBeUnprotected_AreQuarantined()
    {
        File.WriteAllBytes(CredentialPath, [9, 9, 9]);
        _protector.Unprotect(Arg.Any<byte[]>()).Returns(_ => throw new CryptographicException("key domain changed"));

        await Service().LoadPersistedCredentialsAsync(TestContext.Current.CancellationToken);

        Assert.False(_state.IsEnrolled);
        Assert.False(File.Exists(CredentialPath));
        Assert.Single(Directory.GetFiles(_workDirectory, ".credentials.corrupt-*"));
    }

    [Fact]
    public async Task CredentialsThatAreNotJson_AreQuarantined()
    {
        StoreCredentials("not json at all");

        await Service().LoadPersistedCredentialsAsync(TestContext.Current.CancellationToken);

        Assert.False(_state.IsEnrolled);
        Assert.False(File.Exists(CredentialPath));
        Assert.Single(Directory.GetFiles(_workDirectory, ".credentials.corrupt-*"));
    }
}
