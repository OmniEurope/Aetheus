// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R-12: enables, disables and deletes an Apache site without a shell. The <c>a2ensite</c> /
/// <c>a2dissite</c> strings the backend used to send are refused by the agent's CommandValidator, and they are
/// not granted through sudo either. These operations only touch <c>sites-enabled/&lt;site&gt;</c> (a link) and
/// <c>sites-available/&lt;site&gt;</c> through the directory ACL that <c>install-agent-linux.sh
/// --enable-apache-manage</c> grants. Both directories are sticky, so the agent can unlink only the entries it
/// owns: an entry another account created is refused by the kernel, and the operation reports it.
/// Apache is then checked with the fixed configtest helper and reloaded through the argv-exact systemctl
/// grant; a failed check or reload puts the previous state back.
/// </summary>
internal sealed class ApacheSiteOperations(
    string sitesAvailablePath,
    string sitesEnabledPath,
    Func<string, string, FileSystemInfo> createSiteSymbolicLink,
    Func<OperationKind, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> runPrivilegedOperation,
    Func<string, string?> readLinkTarget,
    bool isLinux)
{
    internal Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string siteFile, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct) => kind switch
        {
            OperationKind.ApacheEnableSite => RunAsync(siteFile, onOutput, () => EnableAsync(siteFile, timeoutSeconds, onOutput, ct)),
            OperationKind.ApacheDisableSite => RunAsync(siteFile, onOutput, () => DisableAsync(siteFile, timeoutSeconds, onOutput, ct)),
            OperationKind.ApacheDeleteSite => RunAsync(siteFile, onOutput, () => DeleteAsync(siteFile, timeoutSeconds, onOutput, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    /// <summary>A site file name these operations accept: the shared contract (a <c>.conf</c> name starting with a
    /// letter or digit, no path separator), checked again here because the task is not a trust boundary.</summary>
    internal static bool IsAcceptedSiteFile(string? siteFile) =>
        siteFile is not null
        && OperationTargetValidator.ApacheSiteFileRegex().IsMatch(siteFile)
        && siteFile.IndexOfAny(['/', '\\']) < 0
        && string.Equals(Path.GetFileName(siteFile), siteFile, StringComparison.Ordinal);

    private async Task<ExecutorResult> RunAsync(
        string siteFile, Func<string, TaskLogLevel, Task> onOutput, Func<Task<ExecutorResult>> operation)
    {
        if (!isLinux)
            return await FailAsync(onOutput, "Apache operations are only supported on Linux").ConfigureAwait(false);
        if (!IsAcceptedSiteFile(siteFile))
            return await FailAsync(onOutput, $"Invalid Apache site file name '{siteFile}'.").ConfigureAwait(false);
        if (!Directory.Exists(sitesAvailablePath) || !Directory.Exists(sitesEnabledPath))
            return await FailAsync(onOutput, "Apache sites-available/sites-enabled directories were not found.").ConfigureAwait(false);
        if (AvailableFile(siteFile) is null)
            return await FailAsync(onOutput, $"{Path.Combine(sitesAvailablePath, siteFile)} is not a regular file of sites-available.").ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The sticky directories refuse to unlink an entry another account owns (a site created by hand or
            // by a2ensite as root): nothing was changed, and the message says why.
            return await FailAsync(onOutput,
                $"Apache site {siteFile}: {ex.Message}. The entry belongs to another account (sticky directory): the agent can only change the sites it created.")
                .ConfigureAwait(false);
        }
    }

    private async Task<ExecutorResult> EnableAsync(
        string siteFile, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var available = AvailableFile(siteFile)!;
        var enabled = Path.Combine(sitesEnabledPath, siteFile);
        if (EntryExists(enabled))
        {
            if (PointsTo(enabled, available))
            {
                await onOutput($"Apache site {siteFile} is already enabled.", TaskLogLevel.Info).ConfigureAwait(false);
                return new ExecutorResult(0, false);
            }
            return await FailAsync(onOutput, $"{enabled} exists and is not a link to {available}: it is left as it is.").ConfigureAwait(false);
        }

        createSiteSymbolicLink(enabled, available);
        await onOutput($"Enabled site: {enabled} -> {available}", TaskLogLevel.Info).ConfigureAwait(false);
        return await CheckAndReloadAsync(siteFile, timeoutSeconds, onOutput, ct,
            undo: () => DeleteEntry(enabled), undoText: $"removed {enabled}").ConfigureAwait(false);
    }

    private async Task<ExecutorResult> DisableAsync(
        string siteFile, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var available = AvailableFile(siteFile)!;
        var enabled = Path.Combine(sitesEnabledPath, siteFile);
        if (!EntryExists(enabled))
        {
            await onOutput($"Apache site {siteFile} is already disabled.", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        // Only the link a2ensite (or EnableAsync) makes is removed; a regular file or a link elsewhere in
        // sites-enabled is someone's own configuration and stays.
        var target = readLinkTarget(enabled);
        if (target is null || !PointsTo(enabled, available))
            return await FailAsync(onOutput, $"{enabled} is not a link to {available}: it is left as it is.").ConfigureAwait(false);

        DeleteEntry(enabled);
        await onOutput($"Disabled site: removed {enabled}", TaskLogLevel.Info).ConfigureAwait(false);
        return await CheckAndReloadAsync(siteFile, timeoutSeconds, onOutput, ct,
            undo: () => createSiteSymbolicLink(enabled, target), undoText: $"put back {enabled} -> {target}").ConfigureAwait(false);
    }

    private async Task<ExecutorResult> DeleteAsync(
        string siteFile, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var available = AvailableFile(siteFile)!;
        if (EnabledEntriesFor(available).FirstOrDefault() is { } enabledEntry)
            return await FailAsync(onOutput, $"Apache site {siteFile} is enabled ({enabledEntry}): disable it before deleting it.").ConfigureAwait(false);

        var content = await File.ReadAllBytesAsync(available, ct).ConfigureAwait(false);
        File.Delete(available);
        await onOutput($"Deleted {available}", TaskLogLevel.Info).ConfigureAwait(false);

        // A disabled site is not loaded, but another file may still Include it: Apache must accept the result.
        var configTest = await runPrivilegedOperation(OperationKind.ApacheTestConfig, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (configTest.ExitCode == 0)
            return configTest;
        await File.WriteAllBytesAsync(available, content, CancellationToken.None).ConfigureAwait(false);
        await onOutput($"Apache config test failed without {siteFile}: the file was written back to {available}.", TaskLogLevel.Error).ConfigureAwait(false);
        return configTest;
    }

    // Configtest, then reload. Either failing undoes the change; after a failed reload, Apache is reloaded
    // again on the previous configuration so the server does not stay on a half-applied state.
    private async Task<ExecutorResult> CheckAndReloadAsync(
        string siteFile, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct,
        Action undo, string undoText)
    {
        var configTest = await runPrivilegedOperation(OperationKind.ApacheTestConfig, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (configTest.ExitCode != 0)
        {
            undo();
            await onOutput($"Apache config test failed for {siteFile}: {undoText}, nothing was reloaded.", TaskLogLevel.Error).ConfigureAwait(false);
            return configTest;
        }

        var reload = await runPrivilegedOperation(OperationKind.ApacheReload, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (reload.ExitCode == 0)
            return reload;
        undo();
        await onOutput($"Apache reload failed for {siteFile}: {undoText}.", TaskLogLevel.Error).ConfigureAwait(false);
        var restored = await runPrivilegedOperation(OperationKind.ApacheReload, timeoutSeconds, onOutput, CancellationToken.None).ConfigureAwait(false);
        if (restored.ExitCode != 0)
            await onOutput("Apache could not be reloaded on the previous configuration either: check the Apache service.", TaskLogLevel.Error).ConfigureAwait(false);
        return reload;
    }

    // The sites-available file of that name when it is a regular file (not a link, not a directory) that
    // resolves inside sites-available.
    private string? AvailableFile(string siteFile)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sitesAvailablePath));
        var path = Path.GetFullPath(Path.Combine(sitesAvailablePath, siteFile));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        var info = new FileInfo(path);
        return info.Exists && readLinkTarget(path) is null ? path : null;
    }

    // The sites-enabled entries that resolve to that sites-available file, whatever their own name.
    private IEnumerable<string> EnabledEntriesFor(string available) =>
        Directory.EnumerateFileSystemEntries(sitesEnabledPath).Where(entry => PointsTo(entry, available));

    private bool PointsTo(string link, string available)
    {
        var target = readLinkTarget(link);
        if (target is null)
            return false;
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(link)!);
        return string.Equals(resolved, available, StringComparison.Ordinal);
    }

    // A link counts even when its target is gone (File.Exists follows it).
    private bool EntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || readLinkTarget(path) is not null;

    private static void DeleteEntry(string path) => File.Delete(path);

    private static async Task<ExecutorResult> FailAsync(Func<string, TaskLogLevel, Task> onOutput, string message)
    {
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }
}
