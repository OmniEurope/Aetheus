// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Aetheus.Back.Configuration;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Result of a credited git invocation. <see cref="Output"/> and <see cref="Error"/> are already
/// credential-masked.
/// </summary>
public readonly record struct CreditedGitResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs <c>git</c> against an external remote with credentials injected via environment only - never
/// in the URL, never in <c>.git/config</c>, never logged. HTTPS uses an ephemeral
/// <c>http.extraHeader</c> Authorization; SSH uses an ephemeral private-key file (0600) with a pinned
/// <c>known_hosts</c> and <c>StrictHostKeyChecking=yes</c> (we never disable host verification).
/// </summary>
/// <remarks>
/// <c>tempRoot</c> overrides the directory holding the ephemeral credential files (default: the system
/// temp directory); tests pass a private directory so they never observe other processes.
/// </remarks>
public sealed class CreditedGitRunner(ILogger<CreditedGitRunner> logger, string? tempRoot = null)
{
    private static readonly TimeSpan DefaultTimeout = BackendRuntimeDefaults.GitLongRunningTimeout;

    /// <summary>
    /// Runs a git command crediting <paramref name="credential"/> for <paramref name="remoteUrl"/>.
    /// Pass the remote URL credential-free; secrets are wired through env + temp files that are
    /// shredded in a finally block. Both <see cref="CreditedGitResult.Output"/> and
    /// <see cref="CreditedGitResult.Error"/> are credential-masked.
    /// </summary>
    public async Task<CreditedGitResult> RunAsync(
        string workDir,
        IReadOnlyList<string> args,
        string? remoteUrl,
        GitCredentialPayload? credential,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = GitProcessStartInfoFactory.Create(workDir, args);

        // Defence in depth: a credential prompt must never block the process waiting on stdin.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        var tempPaths = new List<string>();
        var secretsToMask = new List<string>();
        try
        {
            ApplyCredential(psi, credential, tempPaths, secretsToMask);

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? DefaultTimeout);

            string output, error;
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
                var waitTask = process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(outputTask, errorTask, waitTask).ConfigureAwait(false);
                output = await outputTask.ConfigureAwait(false);
                error = await errorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                SafeKill(process);
                logger.LogWarning("credited git {Args} timed out", string.Join(' ', args));
                return new CreditedGitResult(-1, string.Empty, "Timeout");
            }

            return new CreditedGitResult(process.ExitCode, Mask(output, secretsToMask), Mask(error, secretsToMask));
        }
        finally
        {
            foreach (var path in tempPaths)
            {
                ShredFile(path);
            }
        }
    }

    private void ApplyCredential(
        ProcessStartInfo psi, GitCredentialPayload? credential,
        List<string> tempPaths, List<string> secretsToMask)
    {
        if (credential is null)
        {
            return;
        }

        if (credential.AuthType == GitAuthType.HttpsToken && !string.IsNullOrEmpty(credential.Token))
        {
            // user:token -> Authorization: Basic. The header value carries the secret, so it goes
            // through GIT_CONFIG_* env (process-local), never the URL or on-disk config.
            var user = string.IsNullOrEmpty(credential.Username) ? "x-access-token" : credential.Username;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{credential.Token}"));
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";
            secretsToMask.Add(credential.Token);
            secretsToMask.Add(basic);
            return;
        }

        if (credential.AuthType == GitAuthType.Ssh && !string.IsNullOrEmpty(credential.PrivateKeyPem))
        {
            var keyPath = WriteTempFile(credential.PrivateKeyPem, tempPaths, restrictPermissions: true);
            // Invariant: keyPath / knownHostsPath are internally-generated temp paths ("prom-git-{Guid:N}",
            // see WriteTempFile) with no spaces or shell metacharacters, so interpolating them into
            // GIT_SSH_COMMAND is safe. If the temp naming ever changes, this assembly must stay argv-safe.
            var sshCommand = new StringBuilder($"ssh -i \"{keyPath}\" -o IdentitiesOnly=yes");

            // Pinned known_hosts - we require it and keep StrictHostKeyChecking on. No blind TOFU.
            if (!string.IsNullOrEmpty(credential.KnownHosts))
            {
                var knownHostsPath = WriteTempFile(credential.KnownHosts, tempPaths, restrictPermissions: false);
                sshCommand.Append($" -o UserKnownHostsFile=\"{knownHostsPath}\" -o StrictHostKeyChecking=yes");
            }

            psi.Environment["GIT_SSH_COMMAND"] = sshCommand.ToString();
            if (!string.IsNullOrEmpty(credential.Passphrase))
            {
                secretsToMask.Add(credential.Passphrase);
            }
        }
    }

    private string WriteTempFile(string content, List<string> tempPaths, bool restrictPermissions)
    {
        var path = Path.Combine(tempRoot ?? Path.GetTempPath(), $"prom-git-{Guid.NewGuid():N}");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        tempPaths.Add(path);

        if (restrictPermissions && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 0600 - ssh refuses world-readable private keys.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static string Mask(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static void ShredFile(string path)
    {
        if (!File.Exists(path))
            return;

        // Honour the name: overwrite the bytes before unlinking so the private key / known_hosts content
        // can't be recovered from the freed blocks. Best-effort (tmpfs on Linux servers makes this moot,
        // but on a persistent tmp it matters). A single zero pass is enough against casual recovery.
        try
        {
            var length = new FileInfo(path).Length;
            if (length > 0)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var zeros = new byte[Math.Min(length, 64 * 1024)];
                long written = 0;
                while (written < length)
                {
                    var chunk = (int)Math.Min(zeros.Length, length - written);
                    stream.Write(zeros, 0, chunk);
                    written += chunk;
                }
                stream.Flush();
            }
        }
        catch (IOException)
        {
            // Overwrite is best-effort; fall through to delete regardless.
        }

        File.Delete(path);
    }

    private void SafeKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogWarning(ex, "[ExternalRepos] git kill failed"); }
    }
}
