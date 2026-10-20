// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R3-44: a service that refuses to start says why in its task, without SSH.</summary>
public class ServiceStartDiagnosticsTests
{
    private static (ServiceOperationExecutor Executor, List<string[]> Calls) Build(int startExitCode, bool timedOut = false, params string[] installedHelpers)
    {
        var calls = new List<string[]>();
        var executor = new ServiceOperationExecutor(Options.Create(new AetheusAgentOptions()), NullLogger<ServiceOperationExecutor>.Instance)
        {
            IsLinux = true,
            IsWindows = false,
            HelperExists = path => installedHelpers.Contains(path),
            RunProcessAsync = (psi, _, onOutput, _, _) =>
            {
                calls.Add([.. psi.ArgumentList]);
                var first = calls.Count == 1;
                return Task.FromResult(first ? new ExecutorResult(startExitCode, timedOut) : new ExecutorResult(3, false));
            }
        };
        return (executor, calls);
    }

    [Fact]
    public async Task AFailedApacheStart_AppendsItsStatusAndItsConfigTest_AndKeepsTheStartExitCode()
    {
        var (executor, calls) = Build(1, false, ServiceStartDiagnostics.ApacheConfigTestHelperPath);
        var messages = new List<string>();

        var result = await executor.ExecuteAsync(OperationKind.ServiceStart, "apache2.service", 30,
            (m, _) => { messages.Add(m); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(3, calls.Count);
        Assert.Equal(["-n", "/bin/systemctl", "start", "apache2"], calls[0]);
        Assert.Equal(["-n", "/bin/systemctl", "status", "apache2"], calls[1]);
        Assert.Equal(["-n", ServiceStartDiagnostics.ApacheConfigTestHelperPath], calls[2]);
        Assert.Contains(messages, m => m.Contains("apache2 did not start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedPostfixRestart_RunsTheMailHelperCheck_WhenTheHelperIsInstalled()
    {
        var (executor, calls) = Build(1, false, "/usr/local/lib/aetheus/mail-manage");

        await executor.ExecuteAsync(OperationKind.ServiceRestart, "postfix", 30, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(["-n", "/usr/local/lib/aetheus/mail-manage", "check"], calls[^1]);
    }

    [Fact]
    public async Task AHelperThatIsNotInstalled_IsSkipped_OnlyTheStatusIsRead()
    {
        var (executor, calls) = Build(1);

        await executor.ExecuteAsync(OperationKind.ServiceStart, "dovecot", 30, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls.Count);
        Assert.Equal(["-n", "/bin/systemctl", "status", "dovecot"], calls[1]);
    }

    [Theory]
    [InlineData(0, false, OperationKind.ServiceStart)]
    [InlineData(1, true, OperationKind.ServiceStart)]
    [InlineData(1, false, OperationKind.ServiceStop)]
    [InlineData(1, false, OperationKind.ServiceStatus)]
    public async Task ASuccessATimeoutOrAnotherVerb_AddsNothing(int exitCode, bool timedOut, OperationKind kind)
    {
        var (executor, calls) = Build(exitCode, timedOut, ServiceStartDiagnostics.ApacheConfigTestHelperPath);

        await executor.ExecuteAsync(kind, "apache2", 30, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Single(calls);
    }
}
