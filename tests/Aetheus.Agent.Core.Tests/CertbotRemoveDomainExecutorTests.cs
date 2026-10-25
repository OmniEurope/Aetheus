// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-12: reissuing a certificate without one of its names runs the root-owned manage helper
/// argv-exact (<c>sudo -n aetheus-certbot-manage remove-name &lt;certificate&gt; &lt;name&gt;</c>), after the agent
/// checked both values again.</summary>
public class CertbotRemoveDomainExecutorTests
{
    private static CertbotOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunProcessAsync = calls.RunAsync
        };

    private static Task<ExecutorResult> Remove(CertbotOperationExecutor executor, string certificate, string? domain, OutputLog log)
    {
        var env = domain is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [CertbotTaskVariables.RemoveDomain] = domain };
        return executor.ExecuteAsync(OperationKind.CertbotRemoveDomain, certificate, env, 180, log.Write, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void RemoveDomain_IsHandledByTheCertbotExecutor() =>
        Assert.True(Build(new ProcessRunCalls()).CanHandle(OperationKind.CertbotRemoveDomain));

    [Fact]
    public async Task RemoveDomain_RunsTheManageHelper_WithTheCertificateAndTheName()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Remove(Build(calls), "aetheus.sonytumen.com", "aetheus-api.sonytumen.com", log);

        Assert.Equal(0, result.ExitCode);
        var run = Assert.Single(calls.Runs);
        Assert.Equal("sudo", run.FileName);
        Assert.Equal(["-n", "/usr/local/lib/aetheus/aetheus-certbot-manage", "remove-name", "aetheus.sonytumen.com", "aetheus-api.sonytumen.com"], run.Argv);
    }

    [Theory]
    [InlineData("aetheus.sonytumen.com", null)]
    [InlineData("aetheus.sonytumen.com", "")]
    [InlineData("aetheus.sonytumen.com", "-d")]
    [InlineData("aetheus.sonytumen.com", "a.example.com b.example.com")]
    [InlineData("aetheus.sonytumen.com", "aetheus-api.sonytumen.com\n")]
    [InlineData("../live", "aetheus-api.sonytumen.com")]
    [InlineData("-n", "aetheus-api.sonytumen.com")]
    public async Task RemoveDomain_RefusesAnInvalidCertificateOrName_WithoutRunningAnything(string certificate, string? domain)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Remove(Build(calls), certificate, domain, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task RemoveDomain_IsLinuxOnly()
    {
        var calls = new ProcessRunCalls();

        var result = await Remove(Build(calls, isLinux: false), "aetheus.sonytumen.com", "aetheus-api.sonytumen.com", new OutputLog());

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
    }
}
