// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R3-59: lists, without privilege, every package installed on the host and prints one
/// <c>PACKAGE name version</c> line each, then <c>TOTAL n</c> (<see cref="InstalledPackages"/>). It reads the
/// package database only: <c>dpkg-query -W</c> on a Debian-family host, <c>rpm -qa</c> where dpkg is absent.
/// No sudo, no helper, nothing installed or removed. A database that cannot be read fails the task without
/// the <c>TOTAL</c> line, so a partial read is never shown as the full list.
/// </summary>
public sealed partial class PackageInventoryOperationExecutor(ILogger<PackageInventoryOperationExecutor> logger) : IOperationExecutor
{
    private const int QueryTimeoutSeconds = 60;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Runs one read-only query (file name, argv) and hands back its result and its output lines.</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<(ExecutorResult Result, IReadOnlyList<string> Lines)>> RunQueryAsync { get; init; }
        = (fileName, argv, ct) => ProcessRunner.CaptureAsync(fileName, argv, QueryTimeoutSeconds, logger, ct);

    public bool CanHandle(OperationKind kind) => kind == OperationKind.PackageInventory;

    public async Task<ExecutorResult> ExecuteAsync(OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!CanHandle(kind) || !OperationTargetValidator.IsValid(kind, target))
        {
            await onOutput("Invalid package inventory target", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!IsLinux)
        {
            await onOutput("The installed package list is read on Linux only", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var packages = await ReadDpkgAsync(cancellationToken).ConfigureAwait(false)
            ?? await ReadRpmAsync(cancellationToken).ConfigureAwait(false);
        if (packages is null)
        {
            await onOutput("No package database could be read (dpkg-query, rpm)", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        foreach (var (name, version) in packages)
            await onOutput(InstalledPackages.PackageLine(name, version), TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(InstalledPackages.TotalLine(packages.Count), TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    // ${binary:Package} carries the architecture of a foreign-architecture package (libc6:i386), so a
    // multiarch host lists each installed copy once.
    internal static IReadOnlyList<string> DpkgArgv() => ["-W", "-f=${db:Status-Abbrev}\t${binary:Package}\t${Version}\n"];

    internal static IReadOnlyList<string> RpmArgv() => ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\n"];

    private async Task<List<(string Name, string Version)>?> ReadDpkgAsync(CancellationToken ct)
    {
        var (result, lines) = await RunQueryAsync("dpkg-query", DpkgArgv(), ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? Usable(ParseDpkg(lines)) : null;
    }

    private async Task<List<(string Name, string Version)>?> ReadRpmAsync(CancellationToken ct)
    {
        var (result, lines) = await RunQueryAsync("rpm", RpmArgv(), ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? Usable(ParseRpm(lines)) : null;
    }

    // A database that answered with no package at all is not a host with nothing installed: it is a read
    // that went wrong, and an empty list would be shown as the truth.
    private static List<(string Name, string Version)>? Usable(List<(string Name, string Version)> packages) =>
        packages.Count == 0 ? null : [.. packages.DistinctBy(p => p.Name, StringComparer.Ordinal).OrderBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>The installed packages of a dpkg answer. The second status letter is the current state:
    /// <c>n</c> (not installed) and <c>c</c> (configuration files only, the <c>rc</c> left by a remove) are
    /// not installed; every other state still has files on disk, like <c>DpkgInstalledPackages</c>.</summary>
    internal static List<(string Name, string Version)> ParseDpkg(IEnumerable<string> lines) =>
        [.. lines.Select(line => DpkgLine().Match(line))
            .Where(match => match.Success && match.Groups["state"].Value[1] is not ('n' or 'c'))
            .Select(match => (match.Groups["name"].Value, match.Groups["version"].Value.Trim()))];

    internal static List<(string Name, string Version)> ParseRpm(IEnumerable<string> lines) =>
        [.. lines.Select(line => RpmLine().Match(line))
            .Where(match => match.Success)
            .Select(match => (match.Groups["name"].Value, match.Groups["version"].Value.Trim()))];

    // "ii \tcurl\t7.88.1-10", "rc \topendkim\t2.11.0", "iF \tlibc6:i386\t2.36-9": the status abbreviation
    // (desired action, current status, which dpkg writes in capitals for H, U, F and W, then the error flag
    // R or a blank), the package, the version.
    [GeneratedRegex(@"^(?<state>[a-z][a-zA-Z])[R ]?\s*\t(?<name>[a-z0-9][a-z0-9+.:_-]*)\t(?<version>[^\t]*)$")]
    private static partial Regex DpkgLine();

    // "curl\t7.76.1-26.el9": the name, the version and release.
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][A-Za-z0-9+._-]*)\t(?<version>[^\t]+)$")]
    private static partial Regex RpmLine();
}
