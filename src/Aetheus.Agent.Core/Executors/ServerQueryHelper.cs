// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Executors;

public static class ServerQueryHelper
{
    private const string CredentialPath = "/opt/aetheus-agent/teamspeak/query-credentials";

    // S-TECH-87: the legacy nc/stdin invocation builders were retired - the read path is now
    // TcpTeamspeakQueryClient and the write path the typed ServerQuery op. Only the credential
    // path + escaping/parsing helpers below remain in use.
    public static string ReadCredentialPath() => CredentialPath;

    public static async Task<int?> GetQueryPortAsync(
        IReadOnlyDictionary<string, string> environmentVariables,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (environmentVariables.TryGetValue(TeamspeakSetupEnv.RuntimeQueryPort, out var value)
            && int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port)
            && port is >= 1 and <= 65535)
            return port;

        await onOutput(
            "Missing or invalid TEAMSPEAK_QUERY_PORT",
            TaskLogLevel.Error).ConfigureAwait(false);
        return null;
    }

    public static Task<string?> ReadCredentialAsync(
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
        => ReadCredentialAsync(CredentialPath, onOutput, ct);

    // The credential file is a parameter so the read can be exercised on a temporary file.
    internal static async Task<string?> ReadCredentialAsync(
        string credentialPath,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        string credential;
        try
        {
            credential = (await File.ReadAllTextAsync(credentialPath, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await onOutput(
                $"Cannot read TeamSpeak query credential at {credentialPath}: {ex.Message}",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrEmpty(credential)) return credential;

        await onOutput(
            "TeamSpeak query credential file is empty",
            TaskLogLevel.Error).ConfigureAwait(false);
        return null;
    }

    public static string GetValue(string line, string key)
    {
        var prefix = key + "=";
        var startIndex = line.IndexOf(prefix, StringComparison.Ordinal);
        if (startIndex < 0) return string.Empty;

        startIndex += prefix.Length;
        var endIndex = line.IndexOf(' ', startIndex);
        return endIndex < 0 ? line[startIndex..] : line[startIndex..endIndex];
    }

    public static string EscapeServerQuery(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("/", "\\/")
            .Replace(" ", "\\s")
            .Replace("|", "\\p")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");

    public static string UnescapeServerQuery(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                result.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            result.Append(escaped switch
            {
                's' => ' ',
                'p' => '|',
                'n' => '\n',
                'r' => '\r',
                '/' => '/',
                '\\' => '\\',
                _ => '\\'
            });
            if (escaped is not ('s' or 'p' or 'n' or 'r' or '/' or '\\'))
                result.Append(escaped);
        }
        return result.ToString();
    }
}

