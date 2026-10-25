// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of the former <c>ensure-node-runtime.sh</c> script, which the ported scripts load: resolves the
/// exact Node.js the release toolchain requires. The host <c>node</c> is kept when it reports that
/// exact version; otherwise the official Linux x64 archive, pinned by SHA-256, is installed under
/// <c>$HOME/.aetheus</c>, one installer at a time behind an installation lock, extracted into a staging
/// directory and moved into place only once the extracted runtime answers with the pinned version.
///
/// Differences, deliberate: the archive is downloaded and checked in-process instead of through
/// <c>curl</c> and <c>sha256sum</c> (same URL, same digest, same retry policy), so only <c>tar</c> is
/// required; and the lock is a file created exclusively at the script's lock path rather than a
/// directory, which still excludes a script run holding that path as a directory, and the other way
/// round.
/// </summary>
internal sealed class PinnedNodeRuntime(IPipelineFunctionTools tools, PinnedDownload archive)
{
    internal const string Version = "24.4.1";
    internal const string LinuxX64Sha256 = "063f2eb299ba60e3fc9b424d8e87d0e2f6be84b39bdeadc421ee2865914c498b";
    internal const string ArchiveUrl = "https://nodejs.org/dist/v" + Version + "/node-v" + Version + "-linux-x64.tar.gz";
    private const int LockAttempts = 120;

    /// <summary>The official Linux x64 archive at the digest the script pinned.</summary>
    internal static readonly PinnedDownload OfficialArchive = new(new Uri(ArchiveUrl), LinuxX64Sha256);

    public async Task<ToolchainResolution> ResolveAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        var system = FunctionShell.FindOnPath(context, "node");
        if (await HasRuntimeAsync(context, system, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(system!);

        if (!tools.IsLinuxX64)
            return ToolchainResolution.Failed($"Pinned Node.js {Version} bootstrap supports Linux x86_64 only");
        var home = FunctionShell.Variable(context, "HOME");
        if (home is null)
            return ToolchainResolution.Failed("HOME is required to install pinned Node.js without root");
        var installDirectory = Path.Combine(home, ".aetheus", $"node-v{Version}-linux-x64");
        var local = Path.Combine(installDirectory, "bin", "node");
        if (await HasRuntimeAsync(context, local, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Found(local);

        var tar = FunctionShell.FindOnPath(context, "tar");
        if (tar is null)
            return ToolchainResolution.Failed($"tar is required to install Node.js {Version}");
        return await InstallUnderLockAsync(context, new Install(installDirectory, local, tar), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The script's <c>has_runtime</c>: executable, and <c>--version</c> prints exactly
    /// <c>v&lt;version&gt;</c>.</summary>
    private async Task<bool> HasRuntimeAsync(PipelineFunctionContext context, string? node, CancellationToken cancellationToken)
    {
        if (node is null || !FunctionShell.IsExecutable(node))
            return false;
        var reported = await FunctionShell
            .CaptureAsync(tools, FunctionShell.StartInfo(context, node, Path.GetDirectoryName(node)!, "--version"), cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(reported, "v" + Version, StringComparison.Ordinal);
    }

    private async Task<ToolchainResolution> InstallUnderLockAsync(
        PipelineFunctionContext context, Install install, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(install.Directory)!;
        Directory.CreateDirectory(parent);
        var lockPath = install.Directory + ".lock";
        for (var attempt = 0; !TryTakeLock(lockPath);)
        {
            if (await HasRuntimeAsync(context, install.Node, cancellationToken).ConfigureAwait(false))
                return ToolchainResolution.Found(install.Node);
            attempt++;
            if (attempt >= LockAttempts)
                return ToolchainResolution.Failed($"Timed out waiting for the pinned Node.js {Version} installation lock");
            await tools.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        var archiveFile = Path.Combine(parent, ".node-archive." + Path.GetRandomFileName());
        var staging = Directory.CreateDirectory(Path.Combine(parent, ".node-install." + Path.GetRandomFileName())).FullName;
        try
        {
            if (await HasRuntimeAsync(context, install.Node, cancellationToken).ConfigureAwait(false))
                return ToolchainResolution.Found(install.Node);
            return await InstallAsync(context, install, archiveFile, staging, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(archiveFile);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            File.Delete(lockPath);
        }
    }

    private async Task<ToolchainResolution> InstallAsync(
        PipelineFunctionContext context, Install install, string archiveFile, string staging, CancellationToken cancellationToken)
    {
        await context.Log($"Installing pinned Node.js {Version} in {install.Directory}").ConfigureAwait(false);
        await tools.DownloadAsync(archive.Source, archiveFile, cancellationToken).ConfigureAwait(false);
        if (!await FunctionShell.HasSha256Async(archiveFile, archive.Sha256, cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Failed($"The downloaded Node.js {Version} archive does not match its pinned SHA-256 ({archive.Sha256})");
        var extract = await tools.RunAsync(
            FunctionShell.StartInfo(context, install.Tar, staging, "-xzf", archiveFile, "--strip-components=1", "-C", staging),
            (line, level) => context.Log(line, level),
            cancellationToken).ConfigureAwait(false);
        if (extract.ExitCode != 0)
            return ToolchainResolution.Failed($"tar could not extract the Node.js {Version} archive (exit code {extract.ExitCode})");

        if (!await HasRuntimeAsync(context, Path.Combine(staging, "bin", "node"), cancellationToken).ConfigureAwait(false))
            return ToolchainResolution.Failed($"Pinned Node.js {Version} archive could not be verified after extraction");
        if (Directory.Exists(install.Directory))
            Directory.Delete(install.Directory, recursive: true);
        Directory.Move(staging, install.Directory);

        return await HasRuntimeAsync(context, install.Node, cancellationToken).ConfigureAwait(false)
            ? ToolchainResolution.Found(install.Node)
            : ToolchainResolution.Failed($"Pinned Node.js {Version} installation could not be verified");
    }

    /// <summary>The script's <c>mkdir "$LOCK_DIR"</c>: succeeds for one caller only, whether the other
    /// holder is this port (a file) or the script (a directory).</summary>
    private static bool TryTakeLock(string lockPath)
    {
        try
        {
            using var _ = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Install(string Directory, string Node, string Tar);
}
