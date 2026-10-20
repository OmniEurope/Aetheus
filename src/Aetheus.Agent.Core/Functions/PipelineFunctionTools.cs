// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The side effects a function may have outside its own process: running a tool, downloading a
/// file, waiting, and the platform it runs on. Functions take them through this seam so their
/// tests can stand in for a toolchain, the network or the clock; outside tests it is
/// <see cref="PipelineFunctionTools.Default"/>.
/// </summary>
internal interface IPipelineFunctionTools
{
    /// <summary>Runs a process to completion through the agent's <see cref="ProcessRunner"/>: output is
    /// streamed line by line (stderr prefixed <c>[stderr]</c>), and cancelling the token kills the
    /// process tree and throws.</summary>
    Task<ExecutorResult> RunAsync(ProcessStartInfo process, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken);

    /// <summary>Downloads <paramref name="source"/> to <paramref name="destination"/>, retrying a
    /// transient failure, and throws when it cannot.</summary>
    Task DownloadAsync(Uri source, string destination, CancellationToken cancellationToken);

    /// <summary>Waits, honouring the token.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>Whether the host is Linux on x86_64 (<c>uname -s</c> Linux, <c>uname -m</c> x86_64).</summary>
    bool IsLinuxX64 { get; }
}

/// <inheritdoc cref="IPipelineFunctionTools"/>
internal sealed class PipelineFunctionTools : IPipelineFunctionTools
{
    // The function's own token carries the step timeout; this bound only keeps the runner's timer valid.
    private const int ProcessBoundSeconds = 7 * 24 * 3600;
    private const int DownloadAttempts = 4;

    private static readonly HttpClient s_http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static PipelineFunctionTools Default { get; } = new();

    private PipelineFunctionTools()
    {
    }

    public bool IsLinuxX64 =>
        OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public Task<ExecutorResult> RunAsync(
        ProcessStartInfo process, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(process, ProcessBoundSeconds, onOutput, NullLogger.Instance, cancellationToken);

    /// <summary>Same retry policy as the scripts' <c>curl -fsSL --retry 3 --retry-delay 2
    /// --connect-timeout 15</c>: up to three retries two seconds apart, an HTTP error is a failure.</summary>
    public async Task DownloadAsync(Uri source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await s_http
                    .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(destination);
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < DownloadAttempts && ex is HttpRequestException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
