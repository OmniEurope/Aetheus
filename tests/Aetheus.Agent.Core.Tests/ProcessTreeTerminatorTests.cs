// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel;
using System.Diagnostics;
using Aetheus.Agent.Core.Services;
using Aetheus.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Neighbour of AGC-014: killing the tree of a <c>sudo -n</c> child fails with EPERM, because the tree
/// holds processes of another user. <see cref="Process.Kill(bool)"/> reports it as an
/// <see cref="AggregateException"/> of <see cref="Win32Exception"/>s. Thrown from the runner's finally,
/// it replaced the task's real outcome with an unhandled exception and left the child running unlogged.
/// </summary>
public sealed class ProcessTreeTerminatorTests
{
    [Fact]
    public void ATreeKillRefusedWithEperm_FallsBackToTheRootProcessAndSaysSo()
    {
        var calls = new List<bool>();
        var logger = new RecordingLogger();

        var killed = ProcessTreeTerminator.Kill(
            entireTree =>
            {
                calls.Add(entireTree);
                if (entireTree)
                    throw new AggregateException(new Win32Exception(1, "Operation not permitted"));
            },
            processId: 4242,
            logger);

        Assert.True(killed);
        Assert.Equal([true, false], calls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("4242", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void ARootKillRefusedToo_IsLoggedAsAnErrorInsteadOfThrown(Type refusal)
    {
        var logger = new RecordingLogger();

        var killed = ProcessTreeTerminator.Kill(
            _ => throw (Exception)Activator.CreateInstance(refusal)!,
            processId: 4243,
            logger);

        Assert.True(killed);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("4243", StringComparison.Ordinal));
    }

    [Fact]
    public void AProcessThatAlreadyExited_IsNotKilledAgain()
    {
        var calls = new List<bool>();

        var killed = ProcessTreeTerminator.Kill(
            entireTree =>
            {
                calls.Add(entireTree);
                throw new InvalidOperationException("No process is associated with this object.");
            },
            processId: 4244,
            NullLogger.Instance);

        Assert.False(killed);
        Assert.Equal([true], calls);
    }

    /// <summary>The real primitive on Linux: a child the agent owns is killed with its whole tree.</summary>
    [PlatformFact("linux")]
    public async Task OnLinux_AnOwnedChildTreeIsKilled()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 60 & wait"])
        {
            UseShellExecute = false,
        })!;

        Assert.True(ProcessTreeTerminator.Kill(process, NullLogger.Instance));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(wait.Token);
        Assert.True(process.HasExited);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
