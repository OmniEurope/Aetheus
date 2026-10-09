// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

internal sealed class ApacheConfigSetApplier(
    string sitesAvailablePath,
    string sitesEnabledPath,
    Func<string, string, FileSystemInfo> createSiteSymbolicLink,
    Func<OperationKind, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> runPrivilegedOperation,
    bool platformOverride)
{
    internal async Task<ExecutorResult> ApplyAsync(
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !platformOverride)
        {
            await onOutput("Apache operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!Directory.Exists(sitesAvailablePath) || !Directory.Exists(sitesEnabledPath))
        {
            await onOutput("Apache sites-available/sites-enabled directories were not found.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!TryDecodeConfigSet(envVars, out var files, out var error))
        {
            await onOutput(error, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        Dictionary<string, ApacheConfigSnapshot> snapshots;
        try
        {
            snapshots = files.Keys.ToDictionary(name => name, CaptureSnapshot, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await onOutput($"Could not snapshot the current Apache configuration: {ex.Message}", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        try
        {
            foreach (var (name, content) in files)
                await WriteAndEnableAsync(name, content, ct).ConfigureAwait(false);

            var configTest = await runPrivilegedOperation(
                OperationKind.ApacheTestConfig, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (configTest.ExitCode != 0)
            {
                await onOutput("Apache config test failed; restoring the previous configuration set.", TaskLogLevel.Error)
                    .ConfigureAwait(false);
                await RestoreAndReloadAsync(snapshots, timeoutSeconds, onOutput).ConfigureAwait(false);
                return configTest;
            }

            var reload = await runPrivilegedOperation(
                OperationKind.ApacheReload, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (reload.ExitCode != 0)
            {
                await onOutput("Apache reload failed; restoring the previous configuration set.", TaskLogLevel.Error)
                    .ConfigureAwait(false);
                await RestoreAndReloadAsync(snapshots, timeoutSeconds, onOutput).ConfigureAwait(false);
                return reload;
            }

            await onOutput($"Applied {files.Count} Apache configuration file(s) transactionally.", TaskLogLevel.Info)
                .ConfigureAwait(false);
            return reload;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await onOutput($"Apache configuration apply failed: {ex.Message}. Restoring the previous set.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            await RestoreAndReloadAsync(snapshots, timeoutSeconds, onOutput).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        catch
        {
            // Cancellation (or any other failure) after the first write would otherwise leave a
            // partial, untested set on disk for the next Apache restart to load.
            await onOutput("Apache configuration apply was interrupted; restoring the previous set.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            await RestoreAndReloadAsync(snapshots, timeoutSeconds, onOutput).ConfigureAwait(false);
            throw;
        }
    }

    private static bool TryDecodeConfigSet(
        IReadOnlyDictionary<string, string> envVars,
        out Dictionary<string, byte[]> files,
        out string error)
    {
        files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        error = string.Empty;
        if (!envVars.TryGetValue("AETHEUS_APACHE_CONFIG_SET_B64", out var encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            error = "Missing AETHEUS_APACHE_CONFIG_SET_B64 environment variable.";
            return false;
        }
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (manifest is null || manifest.Count is < 1 or > 32)
            {
                error = "Apache configuration set must contain between 1 and 32 files.";
                return false;
            }
            var total = 0;
            foreach (var (name, content) in manifest)
            {
                if (!OperationTargetValidator.ApacheSiteFileRegex().IsMatch(name))
                {
                    error = $"Invalid Apache destination filename '{name}'.";
                    return false;
                }
                var bytes = Convert.FromBase64String(content);
                total += bytes.Length;
                if (bytes.Length == 0 || total > 1024 * 1024)
                {
                    error = "Apache configuration set is empty or exceeds the 1 MiB safety limit.";
                    return false;
                }
                files[name] = bytes;
            }
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = $"Invalid Apache configuration-set payload: {ex.Message}";
            return false;
        }
    }

    private ApacheConfigSnapshot CaptureSnapshot(string name)
    {
        var available = Path.Combine(sitesAvailablePath, name);
        var enabled = Path.Combine(sitesEnabledPath, name);
        byte[]? availableContent = File.Exists(available) ? File.ReadAllBytes(available) : null;
        byte[]? enabledContent = null;
        string? enabledLinkTarget = null;
        if (File.Exists(enabled) || Directory.Exists(enabled))
        {
            enabledLinkTarget = new FileInfo(enabled).LinkTarget;
            if (enabledLinkTarget is null && File.Exists(enabled)) enabledContent = File.ReadAllBytes(enabled);
        }
        return new ApacheConfigSnapshot(availableContent, enabledLinkTarget, enabledContent);
    }

    private async Task WriteAndEnableAsync(string name, byte[] content, CancellationToken ct)
    {
        var available = Path.Combine(sitesAvailablePath, name);
        var enabled = Path.Combine(sitesEnabledPath, name);
        var temporary = Path.Combine(sitesAvailablePath, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct).ConfigureAwait(false);
            File.Move(temporary, available, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        if (File.Exists(enabled) || Directory.Exists(enabled)) File.Delete(enabled);
        createSiteSymbolicLink(enabled, available);
    }

    private async Task<bool> RestoreAndReloadAsync(
        IReadOnlyDictionary<string, ApacheConfigSnapshot> snapshots,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        // The restore runs on its own bounded budget: the caller's token may already be cancelled,
        // and a restore that stops half-way is as bad as the interrupted apply it compensates.
        using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(2L * timeoutSeconds + 30));
        var ct = restoreCts.Token;
        try
        {
            foreach (var (name, snapshot) in snapshots)
            {
                var available = Path.Combine(sitesAvailablePath, name);
                var enabled = Path.Combine(sitesEnabledPath, name);
                if (File.Exists(enabled) || Directory.Exists(enabled)) File.Delete(enabled);
                if (File.Exists(available)) File.Delete(available);
                if (snapshot.AvailableContent is not null)
                    await File.WriteAllBytesAsync(available, snapshot.AvailableContent, ct).ConfigureAwait(false);
                if (snapshot.EnabledLinkTarget is not null)
                    createSiteSymbolicLink(enabled, snapshot.EnabledLinkTarget);
                else if (snapshot.EnabledContent is not null)
                    await File.WriteAllBytesAsync(enabled, snapshot.EnabledContent, ct).ConfigureAwait(false);
            }

            var restoredTest = await runPrivilegedOperation(
                OperationKind.ApacheTestConfig, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (restoredTest.ExitCode != 0)
            {
                await onOutput(
                    "Previous Apache configuration was restored on disk but did not pass configtest; manual intervention is required.",
                    TaskLogLevel.Error).ConfigureAwait(false);
                return false;
            }

            var restoredReload = await runPrivilegedOperation(
                OperationKind.ApacheReload, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (restoredReload.ExitCode == 0) return true;
            await onOutput(
                "Previous Apache configuration passed configtest but could not be reloaded; manual intervention is required.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            await onOutput(
                $"Could not fully restore the previous Apache configuration: {ex.Message}. Manual intervention is required.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
    }

    private sealed record ApacheConfigSnapshot(
        byte[]? AvailableContent,
        string? EnabledLinkTarget,
        byte[]? EnabledContent);
}
