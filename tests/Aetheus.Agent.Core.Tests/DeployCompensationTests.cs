// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Once a deploy has switched what runs, every way out that is not a healthy release must put the
/// previous one back, and a rollback is only announced when the previous release really is back.
/// </summary>
public sealed class DeployCompensationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-deploy-compensation-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task CancellationDuringHealthGate_RepointsCurrentToThePreviousReleaseOnItsOwnBudget()
    {
        using var taskCts = new CancellationTokenSource();
        var shell = new ScriptedShell((call, index) =>
        {
            // The first restart starts the new release, then the task is cancelled mid-stabilisation.
            if (call.FileName == "sudo" && index == 0) taskCts.Cancel();
            // The rollback's own restart is refused so the gate ends without a stabilisation wait.
            return call.FileName == "sudo" && index > 0 ? 1 : 0;
        });
        var executor = CreateExecutor(shell);
        var appDir = Path.Combine(_root, "app");
        var previous = Path.Combine(appDir, "releases", "41");
        var output = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ActivateReleaseAsync(
            "app", appDir, Path.Combine(appDir, "current"), Path.Combine(appDir, "releases", "42"), previous,
            60, 0, null, (message, _) => { output.Add(message); return Task.CompletedTask; }, taskCts.Token));

        var rollbackLink = Assert.Single(shell.Calls, call => call.FileName == "ln" && call.Args.Contains(previous));
        Assert.False(rollbackLink.TokenCancelled);
        Assert.Equal(2, shell.Calls.Count(call => call.FileName == "sudo"));
        Assert.False(shell.Calls.Last(call => call.FileName == "sudo").TokenCancelled);
        Assert.Contains(output, line => line.Contains("rolling back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContainerRollback_WhenThePreviousImageCannotBeRetagged_DoesNotAnnounceARollback()
    {
        var releaseDir = Directory.CreateDirectory(Path.Combine(_root, "release")).FullName;
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "compose.yml"), "services: {}\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "image.tar"), "tar", TestContext.Current.CancellationToken);
        var shell = new ScriptedShell((call, _) => call.Args switch
        {
            ["info", ..] => 0,
            [.., "up", "-d", "--wait"] => 1,
            ["tag", ..] => 1,
            _ => 0,
        }, stdOut: call => call.Args switch
        {
            ["info", ..] => "27.0.0",
            [.., "config", "--images"] => "registry.local/app:stable\n",
            ["image", "inspect", ..] => "sha256:previous",
            _ => string.Empty,
        });
        var output = new List<string>();

        var result = await CreateExecutor(shell).DeployContainerAsync(
            "app", releaseDir, "compose.yml", new Dictionary<string, string>(), 60,
            (message, _) => { output.Add(message); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(output, line => line.Contains("DEPLOY_ROLLED_BACK", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("could not restore the previous image", StringComparison.Ordinal));
        // The failed release is not re-upped under the previous release's name.
        Assert.Single(shell.Calls, call => call.Args.Contains("up"));
    }

    private static DeployOperationExecutor CreateExecutor(IShellRunner shell) =>
        new(Substitute.For<IServerApiClient>(), shell, Options.Create(new AetheusAgentOptions()),
            NullLogger<DeployOperationExecutor>.Instance);

    private sealed record ShellCall(string FileName, string[] Args, bool TokenCancelled);

    /// <summary>Records every invocation and throws like the real runner when its token is cancelled.</summary>
    private sealed class ScriptedShell(Func<ShellCall, int, int> exitCode, Func<ShellCall, string>? stdOut = null) : IShellRunner
    {
        private readonly Dictionary<string, int> _perFile = [];

        internal List<ShellCall> Calls { get; } = [];

        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            var call = new ShellCall(fileName, [.. args], ct.IsCancellationRequested);
            Calls.Add(call);
            ct.ThrowIfCancellationRequested();
            var index = _perFile.GetValueOrDefault(fileName);
            _perFile[fileName] = index + 1;
            var code = exitCode(call, index);
            return Task.FromResult(new ShellExecResult(code, stdOut?.Invoke(call) ?? string.Empty, code == 0 ? string.Empty : "refused"));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) =>
            RunExecAsync(fileName, args, ct, timeout);
    }
}
