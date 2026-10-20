// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class DefaultShellRunnerTests
{
    private readonly DefaultShellRunner _runner = new(NullLogger<DefaultShellRunner>.Instance);

    [Fact]
    public async Task RunAsync_CommandCompletes_ReturnsStdout()
    {
        var output = await _runner.RunAsync("echo aetheus-shell-runner", TestContext.Current.CancellationToken);

        Assert.Contains("aetheus-shell-runner", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWithStdinAsync_EchoProcess_RoundTripsPayload()
    {
        var (file, args) = StdinEchoCommand();

        var output = await _runner.RunWithStdinAsync(file, args, "payload with spaces\n", TestContext.Current.CancellationToken);

        Assert.Contains("payload with spaces", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunExecAsync_SucceedingProcess_ReturnsSeparateSuccessfulChannels()
    {
        var (file, args) = SucceedingCommand();

        var result = await _runner.RunExecAsync(file, args, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("observable-output", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public async Task RunExecAsync_FailingProcess_PreservesExitCodeAndStderr()
    {
        var (file, args) = FailingCommand();

        var result = await _runner.RunExecAsync(file, args, TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("observable-error", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PublicMethods_InvalidRequiredArguments_FailFast(int method)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            if (method == 0)
                await _runner.RunAsync(" ", TestContext.Current.CancellationToken);
            else if (method == 1)
                await _runner.RunWithStdinAsync("", [], "input", TestContext.Current.CancellationToken);
            else
                await _runner.RunExecAsync("", [], TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task RunExecAsync_Timeout_CancelsAndTerminatesChild()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aetheus-descendant-{Guid.NewGuid():N}.marker");
        var (file, args) = DescendantMarkerCommand(marker);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _runner.RunExecAsync(file, args, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100)));

            await Task.Delay(TimeSpan.FromSeconds(WitnessDelaySeconds + 1), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(marker), "The descendant process survived cancellation and wrote its marker.");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// The positive control of the test above: the same witness, left to run, does write its marker.
    /// Without it a witness that can never write (a quoting slip once made the POSIX one target a
    /// file named after a literal quote) would let the survival assertion pass whatever happens.
    /// </summary>
    [Fact]
    public async Task DescendantWitness_LeftToRun_WritesItsMarker()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aetheus-descendant-{Guid.NewGuid():N}.marker");
        var (file, args) = DescendantMarkerCommand(marker);
        try
        {
            var result = await _runner.RunExecAsync(file, args, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(60));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("child-survived", (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RunAsync_Timeout_CancelsAndTerminatesShellTree()
    {
        var command = OperatingSystem.IsWindows()
            ? "ping 127.0.0.1 -n 6 >NUL"
            : "sleep 5";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(command, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task RunWithStdinAsync_NullArgumentsFailBeforeStartingProcess()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _runner.RunWithStdinAsync("unused", null!, "input", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _runner.RunWithStdinAsync("unused", [], null!, TestContext.Current.CancellationToken));
    }

    private static (string File, IReadOnlyList<string> Args) StdinEchoCommand() => OperatingSystem.IsWindows()
        ? ("findstr.exe", ["/R", ".*"])
        : ("/bin/cat", []);

    // Not `dotnet --version`: that resolves the SDK the working directory's global.json pins, so it
    // measured the host's SDK layout rather than DefaultShellRunner (exit 155, SDK not found, on the CI
    // host in runs 2413 and 2417).
    private static (string File, IReadOnlyList<string> Args) SucceedingCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/d", "/s", "/c", "echo observable-output"])
        : ("/bin/sh", ["-c", "echo observable-output"]);

    private static (string File, IReadOnlyList<string> Args) FailingCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/d", "/s", "/c", "echo observable-error 1>&2 & exit /b 7"])
        : ("/bin/sh", ["-c", "echo observable-error >&2; exit 7"]);

    private static (string File, IReadOnlyList<string> Args) SlowCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 6 >NUL"])
        : ("/bin/sh", ["-c", "sleep 5"]);

    /// <summary>How long the witness waits before writing its marker. Wide on purpose: on a loaded CI
    /// host the tree kill once landed more than 0.5 s after the 100 ms timeout, so the marker was
    /// written before the kill and the survival assertion failed with no defect in the runner.</summary>
    private const int WitnessDelaySeconds = 3;

    private static (string File, IReadOnlyList<string> Args) DescendantMarkerCommand(string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            // Encoded, so no quote crosses the cmd -> powershell boundary: a quoted -Command reached
            // PowerShell as a string literal, which it printed instead of running, and the marker
            // was never written.
            var windowsMarker = marker.Replace("'", "''", StringComparison.Ordinal);
            var script = $"Start-Sleep -Seconds {WitnessDelaySeconds}; [IO.File]::WriteAllText('{windowsMarker}', 'child-survived')";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var child = $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}";
            return ("cmd.exe", ["/d", "/s", "/c", child]);
        }

        var escapedMarker = marker.Replace("'", "'\\''", StringComparison.Ordinal);
        return ("/bin/sh", ["-c", $"/bin/sh -c 'sleep {WitnessDelaySeconds}; printf child-survived > \"{escapedMarker}\"' & wait"]);
    }
}
