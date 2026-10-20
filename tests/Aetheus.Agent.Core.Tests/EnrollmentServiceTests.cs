// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class EnrollmentServiceTests
{
    private readonly IServerApiClient _apiClientMock = Substitute.For<IServerApiClient>();
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configMock = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
    private readonly AetheusAgentOptions _agentOptions;
    private readonly IOptions<AetheusAgentOptions> _options;
    private readonly AgentState _agentState = new();

    public EnrollmentServiceTests()
    {
        _agentOptions = new AetheusAgentOptions
        {
            ServerUrl = "http://localhost:5301"
        };
        _options = Options.Create(_agentOptions);
    }

    [Fact]
    public void IsEnrolled_WhenServerIdAndToken_ReturnsTrue()
    {
        _agentState.ServerId = 1;
        _agentState.BearerToken = "test-token";
        var svc = CreateService();

        Assert.True(svc.IsEnrolled);
    }

    [Fact]
    public void IsEnrolled_WhenMissingServerId_ReturnsFalse()
    {
        _agentState.BearerToken = "test-token";
        var svc = CreateService();

        Assert.False(svc.IsEnrolled);
    }

    [Fact]
    public void IsEnrolled_WhenMissingToken_ReturnsFalse()
    {
        _agentState.ServerId = 1;
        var svc = CreateService();

        Assert.False(svc.IsEnrolled);
    }

    [Fact]
    public async Task EnrollAsync_AlreadyEnrolled_SetsBearerTokenAndReturnsTrue()
    {
        _agentState.ServerId = 1;
        _agentState.BearerToken = "existing-token";
        var svc = CreateService();

        var result = await svc.EnrollAsync(TestContext.Current.CancellationToken);

        Assert.True(result);
        _apiClientMock.Received(1).SetBearerToken("existing-token");
        await _apiClientMock.DidNotReceive().RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrollAsync_NoRegistrationToken_ReturnsFalse()
    {
        _configMock["Aetheus:RegistrationToken"].Returns((string?)null);
        var svc = CreateService();

        var result = await svc.EnrollAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        await _apiClientMock.DidNotReceive().RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrollAsync_ServerReturnsNull_ReturnsFalse()
    {
        _configMock["Aetheus:RegistrationToken"].Returns("reg-token-123");
        _apiClientMock
            .RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns((ServerRegistrationResponse?)null);
        var svc = CreateService();

        var result = await svc.EnrollAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        await _apiClientMock.Received(1).RegisterAsync(
            Arg.Is<ServerRegistrationRequest>(r => r.RegistrationToken == "reg-token-123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrollAsync_RegistrationPublishesCompatibilityContract()
    {
        _configMock["Aetheus:RegistrationToken"].Returns("reg-token-123");
        _apiClientMock
            .RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServerRegistrationResponse { ServerId = 42, BearerToken = "new-bearer" });
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(
                "git",
                Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "--version" })),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "git version 2.50.0", string.Empty));
        var tempDir = Path.Combine(Path.GetTempPath(), $"aetheus-test-{Guid.NewGuid()}");
        _agentOptions.WorkDirectory = tempDir;
        var svc = CreateService(shell);

        try
        {
            Assert.True(await svc.EnrollAsync(TestContext.Current.CancellationToken));

            await _apiClientMock.Received(1).RegisterAsync(
                Arg.Is<ServerRegistrationRequest>(request =>
                    request.AgentProtocolVersion == AgentProtocol.CurrentVersion
                    && request.AgentCapabilities != null
                    && request.AgentCapabilities.Contains(AgentCapabilities.SelfUpdate)
                    && request.AgentCapabilities.Contains(AgentCapabilities.PipelineBuild)
                    && request.AgentCapabilities.Contains(AgentCapabilities.PipelineFunctions)
                    // The deploy grant is only known from the first heartbeat's probe.
                    && !request.AgentCapabilities.Contains(AgentCapabilities.DeploymentFunctions)
                    && request.AgentCapabilities.SequenceEqual(
                        request.AgentCapabilities.Order(StringComparer.Ordinal))
                    // Include the machine fingerprint at enrollment, or null when unavailable, so
                    // the same host can revive its server record.
                    && request.MachineIdHash == MachineIdentity.ReadHash()),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EnrollAsync_Success_SetsCredentialsAndReturnsTrue()
    {
        _configMock["Aetheus:RegistrationToken"].Returns("reg-token-123");
        _apiClientMock
            .RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServerRegistrationResponse { ServerId = 42, BearerToken = "new-bearer" });

        // Use a temp directory for credential persistence
        var tempDir = Path.Combine(Path.GetTempPath(), $"aetheus-test-{Guid.NewGuid()}");
        _agentOptions.WorkDirectory = tempDir;

        var svc = CreateService();

        try
        {
            var result = await svc.EnrollAsync(TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.Equal(42, _agentState.ServerId);
            Assert.Equal("new-bearer", _agentState.BearerToken);
            _apiClientMock.Received(1).SetBearerToken("new-bearer");

            // Verify credentials were persisted
            var credPath = Path.Combine(tempDir, ".credentials");
            Assert.True(File.Exists(credPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private EnrollmentService CreateService(IShellRunner? shell = null) =>
        new(_apiClientMock, _options, _agentState, _configMock, new PassThroughCredentialProtector(), shell ?? Substitute.For<IShellRunner>(), TimeProvider.System, NullLogger<EnrollmentService>.Instance);

    private sealed class PassThroughCredentialProtector : ICredentialProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
