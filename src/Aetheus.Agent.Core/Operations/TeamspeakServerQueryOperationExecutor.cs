// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// S-TECH-87: runs a single TeamSpeak ServerQuery write/query command over a native
/// <see cref="ITeamspeakQueryClient"/> (TcpClient) instead of the legacy <c>printf … | nc</c> shell
/// task. The inner ServerQuery line arrives as the operation target (already
/// <c>EscapeServerQuery</c>-encoded server-side); the query port is in <c>TEAMSPEAK_QUERY_PORT</c>.
/// The serveradmin credential is read from the agent's local credentials file and never travels in
/// the task, so this runs as the unprivileged agent process - no shell, no <c>nc</c>, no sudo hop.
/// </summary>
public sealed class TeamspeakServerQueryOperationExecutor(ITeamspeakQueryClient queryClient) : EnvironmentOperationExecutor
{
    public override bool CanHandle(OperationKind kind) => kind == OperationKind.TeamspeakServerQuery;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("TeamSpeak ServerQuery operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var queryCommand = ResolveQueryCommand(target, envVars);
        if (string.IsNullOrWhiteSpace(queryCommand))
        {
            await onOutput("Empty ServerQuery command", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var connection = await ReadConnectionAsync(envVars, onOutput, cancellationToken).ConfigureAwait(false);
        if (connection is null) return new ExecutorResult(-1, false);
        return await RunQueryAsync(connection.Value.Port, connection.Value.Credential, queryCommand, onOutput, cancellationToken).ConfigureAwait(false);
    }

    // S-TECH-W9K7: a secret-bearing command (channel/server password, token value, snapshot blob)
    // travels in the encrypted TEAMSPEAK_QUERY_CMD env var instead of the plaintext-at-rest Command
    // column; prefer it when present and fall back to the operation target otherwise.
    internal static string ResolveQueryCommand(string target, IReadOnlyDictionary<string, string> envVars) =>
        envVars.TryGetValue("TEAMSPEAK_QUERY_CMD", out var protectedCmd) && !string.IsNullOrWhiteSpace(protectedCmd)
            ? protectedCmd
            : target;

    private static async Task<(int Port, string Credential)?> ReadConnectionAsync(
        IReadOnlyDictionary<string, string> envVars, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var queryPort = await ServerQueryHelper.GetQueryPortAsync(envVars, onOutput).ConfigureAwait(false);
        if (queryPort is null) return null;
        var credential = await ServerQueryHelper.ReadCredentialAsync(onOutput, ct).ConfigureAwait(false);
        return credential is null ? null : (queryPort.Value, credential);
    }

    /// <summary>Logs in, runs the command and reports whether every answer of the server succeeded.</summary>
    internal async Task<ExecutorResult> RunQueryAsync(
        int port, string credential, string queryCommand, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        // The TCP client writes the script verbatim and reads until the server closes on `quit`.
        // The credential is EscapeServerQuery-encoded so a password with spaces/special chars cannot
        // break the login line; the command was already encoded server-side.
        var script = $"login serveradmin {ServerQueryHelper.EscapeServerQuery(credential)}\nuse sid=1\n{queryCommand}\nquit\n";
        var reply = await queryClient.ExecuteAsync(port, script, ct).ConfigureAwait(false);

        if (reply is null)
        {
            await onOutput($"No reply from TeamSpeak ServerQuery on port {port} (connection failed or timed out)", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        await onOutput(reply, TaskLogLevel.Info).ConfigureAwait(false);

        // TS3 ServerQuery answers every line of the script (login, use, the command, quit) with an
        // `error id=N msg=…` line; id=0 is success. A failed login or a rejected command returns a
        // non-zero id, and the quit that follows still answers id=0, so every answer is checked, not
        // just the last one: a failed action is marked failed, never a fake green (anti-fake rule).
        return ServerQueryReportsSuccess(reply)
            ? new ExecutorResult(0, false)
            : new ExecutorResult(1, false);
    }

    internal static bool ServerQueryReportsSuccess(string reply)
    {
        var answers = reply
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("error id=", StringComparison.Ordinal))
            .ToList();
        return answers.Count > 0 && answers.All(IsSuccessAnswer);
    }

    private static bool IsSuccessAnswer(string answer) =>
        answer is "error id=0" || answer.StartsWith("error id=0 ", StringComparison.Ordinal);
}
