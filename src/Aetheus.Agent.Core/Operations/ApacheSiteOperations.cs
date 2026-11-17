// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

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

    /// <summary>One site taken out by <see cref="RemoveManyAsync"/>: what is needed to put it back.</summary>
    private sealed record RemovedSite(string SiteFile, string Available, string Enabled, string? LinkTarget, byte[] Content);

    /// <summary>
    /// Recette R-26: disables and deletes several sites with ONE configuration test and ONE reload, once all of them are
    /// out. Removing them one by one tests Apache after each, so a site whose neighbour (of the same project) already
    /// points to a deleted certificate was put back by the failing test of the neighbour's remaining configuration, and
    /// none could go. Per site, a precondition that does not hold (not a link to its sites-available file, still enabled
    /// under another name, an entry another account owns) leaves that site as it is and reports why; the others go on.
    /// When the single test or the reload fails, every site removed here is put back, so the host is as it was, and the
    /// reason names the other enabled site the test blamed when there is one (it is not one of these sites).
    /// </summary>
    /// <returns>For each site file, null when it is gone, otherwise why it stays.</returns>
    internal async Task<IReadOnlyDictionary<string, string?>> RemoveManyAsync(
        IReadOnlyList<string> siteFiles, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        var removed = new List<RemovedSite>();
        foreach (var siteFile in siteFiles.Distinct(StringComparer.Ordinal))
        {
            if (await TakeOutAsync(siteFile, removed, onOutput, ct).ConfigureAwait(false) is { } refusal)
                results[siteFile] = refusal;
        }
        if (removed.Count == 0)
            return results;

        // The output of the test is kept to read which file Apache blames; it still goes to the task log.
        var testOutput = new List<string>();
        Task Tee(string line, TaskLogLevel level)
        {
            lock (testOutput) testOutput.Add(line);
            return onOutput(line, level);
        }
        string? failure = null;
        var configTest = await runPrivilegedOperation(OperationKind.ApacheTestConfig, timeoutSeconds, Tee, ct).ConfigureAwait(false);
        if (configTest.ExitCode != 0)
        {
            string[] captured;
            lock (testOutput) captured = [.. testOutput];
            failure = ConfigurationFailureReason(captured, removed);
            await PutBackAsync(removed, onOutput).ConfigureAwait(false);
        }
        else
        {
            var reload = await runPrivilegedOperation(OperationKind.ApacheReload, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (reload.ExitCode != 0)
            {
                failure = $"Apache could not reload without these sites (exit code {reload.ExitCode}): they were put back.";
                await PutBackAsync(removed, onOutput).ConfigureAwait(false);
                var restored = await runPrivilegedOperation(OperationKind.ApacheReload, timeoutSeconds, onOutput, CancellationToken.None).ConfigureAwait(false);
                if (restored.ExitCode != 0)
                    await onOutput("Apache could not be reloaded on the previous configuration either: check the Apache service.", TaskLogLevel.Error).ConfigureAwait(false);
            }
        }
        foreach (var site in removed)
            results[site.SiteFile] = failure;
        return results;
    }

    // Takes one site out (link removed, file deleted). Null when it is out, the reason it stays otherwise.
    private async Task<string?> TakeOutAsync(
        string siteFile, List<RemovedSite> removed, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        string? refusal = !isLinux ? "Apache operations are only supported on Linux"
            : !IsAcceptedSiteFile(siteFile) ? $"Invalid Apache site file name '{siteFile}'."
            : !Directory.Exists(sitesAvailablePath) || !Directory.Exists(sitesEnabledPath) ? "Apache sites-available/sites-enabled directories were not found."
            : AvailableFile(siteFile) is null ? $"{Path.Combine(sitesAvailablePath, siteFile)} is not a regular file of sites-available."
            : null;
        if (refusal is not null)
        {
            await onOutput(refusal, TaskLogLevel.Error).ConfigureAwait(false);
            return refusal;
        }

        var available = AvailableFile(siteFile)!;
        var enabled = Path.Combine(sitesEnabledPath, siteFile);
        string? target = null;
        var linkRemoved = false;
        try
        {
            if (EntryExists(enabled))
            {
                target = readLinkTarget(enabled);
                if (target is null || !PointsTo(enabled, available))
                    return await RefuseAsync($"{enabled} is not a link to {available}: it is left as it is.", onOutput).ConfigureAwait(false);
                DeleteEntry(enabled);
                linkRemoved = true;
                await onOutput($"Disabled site: removed {enabled}", TaskLogLevel.Info).ConfigureAwait(false);
            }
            if (EnabledEntriesFor(available).FirstOrDefault() is { } enabledEntry)
            {
                PutLinkBack(enabled, target, linkRemoved);
                return await RefuseAsync($"Apache site {siteFile} is enabled ({enabledEntry}): disable it before deleting it.", onOutput).ConfigureAwait(false);
            }
            var content = await File.ReadAllBytesAsync(available, ct).ConfigureAwait(false);
            File.Delete(available);
            await onOutput($"Deleted {available}", TaskLogLevel.Info).ConfigureAwait(false);
            removed.Add(new RemovedSite(siteFile, available, enabled, linkRemoved ? target : null, content));
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            PutLinkBack(enabled, target, linkRemoved);
            return await RefuseAsync(
                $"Apache site {siteFile}: {ex.Message}. The entry belongs to another account (sticky directory): the agent can only change the sites it created.",
                onOutput).ConfigureAwait(false);
        }
    }

    private static async Task<string> RefuseAsync(string message, Func<string, TaskLogLevel, Task> onOutput)
    {
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return message;
    }

    private void PutLinkBack(string enabled, string? target, bool linkRemoved)
    {
        if (linkRemoved && target is not null)
            createSiteSymbolicLink(enabled, target);
    }

    // The file first, then its link: Apache never sees a link to a file that is not there.
    private async Task PutBackAsync(List<RemovedSite> removed, Func<string, TaskLogLevel, Task> onOutput)
    {
        foreach (var site in removed)
        {
            await File.WriteAllBytesAsync(site.Available, site.Content, CancellationToken.None).ConfigureAwait(false);
            PutLinkBack(site.Enabled, site.LinkTarget, site.LinkTarget is not null);
            await onOutput($"Put back {site.SiteFile}.", TaskLogLevel.Error).ConfigureAwait(false);
        }
    }

    // Apache's own message names the file ("... of /etc/apache2/sites-enabled/<site>:"): a file that is not one of the
    // sites being removed is the one that breaks the configuration.
    private string ConfigurationFailureReason(string[] testOutput, List<RemovedSite> removed)
    {
        var blamedSite = BlamedSiteRegex(sitesEnabledPath);
        var blamed = testOutput
            .Select(line => blamedSite.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault(site => removed.All(removedSite => removedSite.SiteFile != site));
        return blamed is null
            ? "Apache's configuration test fails without these sites, so they were put back. Read the output of apache2ctl configtest in the task log, fix the configuration, then delete again."
            : $"Apache's configuration test fails because of the enabled site {blamed}, which is not one of the sites being removed (often its certificate is gone), so nothing was removed. "
                + $"Fix or disable {blamed} (Apache section of this server), then delete again.";
    }

    private static Regex BlamedSiteRegex(string sitesEnabledPath) =>
        new(Regex.Escape(sitesEnabledPath.TrimEnd('/')) + @"/([a-zA-Z0-9][a-zA-Z0-9_.\-]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

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
