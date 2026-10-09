// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using System.Text;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R2-080: the "outbound port 25" prerequisite of the mail setup assistant. Many hosting providers
/// block outbound SMTP, and a mail server that cannot reach port 25 accepts mail it will never deliver. The
/// agent opens the connection itself (no helper, no privilege), reads the SMTP greeting, says QUIT and prints
/// one <c>CHECK outbound-25 ok|fail detail</c> line, the format the mail page already reads. Only a
/// <c>220</c> greeting counts as reached: a refused or timed-out connection, or another answer, is a
/// failure, and the step exits non-zero so the task status says the same as the line.
/// </summary>
public sealed class MailOutboundProbeOperationExecutor(
    IOutboundTcpConnector connector,
    ILogger<MailOutboundProbeOperationExecutor> logger) : IOperationExecutor
{
    /// <summary>Longest wait for the connection and the greeting together.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private const int MaxGreetingBytes = 512;
    private const int MaxDetailLength = 200;

    public bool CanHandle(OperationKind kind) => kind == OperationKind.MailOutboundProbe;

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!CanHandle(kind) || !OperationTargetValidator.IsValid(kind, target))
        {
            await onOutput("Invalid outbound SMTP probe target", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var (reached, detail) = await ProbeAsync(target, cancellationToken).ConfigureAwait(false);
        await onOutput(
            reached
                ? $"Outbound SMTP reached {target}:{MailOutboundProbe.Port}."
                : $"Outbound SMTP to {target}:{MailOutboundProbe.Port} failed: {detail}",
            reached ? TaskLogLevel.Info : TaskLogLevel.Error).ConfigureAwait(false);
        await onOutput(
            $"CHECK\t{MailOutboundProbe.CheckComponent}\t{(reached ? "ok" : "fail")}\t{Clean(detail)}",
            TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(reached ? 0 : 1, false);
    }

    private async Task<(bool Reached, string Detail)> ProbeAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var stream = await connector.ConnectAsync(host, MailOutboundProbe.Port, timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var greeting = await ReadGreetingAsync(stream, timeout.Token).ConfigureAwait(false);
                if (!greeting.StartsWith("220", StringComparison.Ordinal))
                    return (false, greeting.Length == 0 ? "no greeting" : $"unexpected greeting: {greeting}");
                await stream.WriteAsync("QUIT\r\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                return (true, greeting);
            }
        }
        // The probe's whole point is to turn a network failure into a verdict, so these are caught.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"no answer within {ProbeTimeout.TotalSeconds:0} s (port 25 filtered?)");
        }
        catch (SocketException ex)
        {
            logger.LogInformation(ex, "Outbound SMTP probe to {Host} failed", host);
            return (false, $"connection failed: {ex.SocketErrorCode}");
        }
        catch (IOException ex)
        {
            logger.LogInformation(ex, "Outbound SMTP probe to {Host} was cut", host);
            return (false, "connection closed before the greeting");
        }
    }

    /// <summary>The first line the server sends, read byte by byte up to a bound (no line reader that could
    /// wait on a peer that never sends a newline: the timeout covers it).</summary>
    private static async Task<string> ReadGreetingAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxGreetingBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0 || buffer[length] == (byte)'\n') break;
            length += read;
        }
        return Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\r');
    }

    private static string Clean(string detail)
    {
        var flat = detail.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= MaxDetailLength ? flat : flat[..MaxDetailLength];
    }
}
