// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R3-23 / R3-26: reads, without privilege, what a mail stack left on the host after an uninstall
/// or a reinstall, and prints one <c>LEFTOVER kind name detail</c> line per item then <c>CLEAN yes|no</c>:
/// the mail packages dpkg still knows (any state but not-installed, so <c>rc</c> counts), the configuration
/// and data paths, the ownership marker and the mail ports still listening (<see cref="MailLeftovers"/>).
/// A step that cannot be read fails the task without the <c>CLEAN</c> line, so the host is never called
/// clean on a partial read.
/// </summary>
public sealed partial class MailLeftoversOperationExecutor(
    IListeningPortsCollector ports,
    ILogger<MailLeftoversOperationExecutor> logger) : IOperationExecutor
{
    private const int DpkgTimeoutSeconds = 60;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    internal Func<string, bool> PathExists { get; init; } = static path => Directory.Exists(path) || File.Exists(path);

    /// <summary>Runs <c>dpkg-query</c> with the given argv and hands back its result and its output lines.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<(ExecutorResult Result, IReadOnlyList<string> Lines)>> RunDpkgQueryAsync { get; init; }
        = (argv, ct) => ProcessRunner.CaptureAsync("dpkg-query", argv, DpkgTimeoutSeconds, logger, ct);

    public bool CanHandle(OperationKind kind) => kind == OperationKind.MailLeftoversCheck;

    public async Task<ExecutorResult> ExecuteAsync(OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!CanHandle(kind) || !OperationTargetValidator.IsValid(kind, target))
        {
            await onOutput("Invalid mail leftovers check target", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!IsLinux)
        {
            await onOutput("The mail leftovers check runs on Linux only", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var packages = await ReadPackagesAsync(cancellationToken).ConfigureAwait(false);
        if (packages is null)
        {
            await onOutput("dpkg-query could not list the mail packages", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        List<ObservedPortDto> listening;
        try
        {
            listening = await ports.CollectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Listening ports could not be read for the mail leftovers check");
            await onOutput("The listening ports could not be read", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var lines = LeftoverLines(packages, listening).ToList();
        foreach (var line in lines)
            await onOutput(line, TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"CLEAN\t{(lines.Count == 0 ? "yes" : "no")}", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    /// <summary>The <c>LEFTOVER</c> lines, packages first, then paths, marker and ports.</summary>
    internal IEnumerable<string> LeftoverLines(IReadOnlyList<(string State, string Package)> packages, IReadOnlyList<ObservedPortDto> listening)
    {
        foreach (var (state, package) in packages)
            yield return $"LEFTOVER\t{MailLeftovers.KindPackage}\t{package}\t{state}";
        foreach (var path in MailLeftovers.Paths.Where(PathExists))
            yield return $"LEFTOVER\t{MailLeftovers.KindPath}\t{path}\t";
        if (PathExists(MailLeftovers.Marker))
            yield return $"LEFTOVER\t{MailLeftovers.KindMarker}\t{MailLeftovers.Marker}\t";
        foreach (var port in listening
                     .Where(observed => string.Equals(observed.Protocol, "tcp", StringComparison.OrdinalIgnoreCase) && MailLeftovers.Ports.Contains(observed.Port))
                     .GroupBy(observed => observed.Port)
                     .OrderBy(group => group.Key))
            yield return $"LEFTOVER\t{MailLeftovers.KindPort}\t{port.Key}\t{Clean(port.First().Holder)}";
    }

    /// <summary>The mail packages dpkg knows in a state other than not-installed (<c>un</c>, <c>pn</c>), or null
    /// when dpkg-query gave no usable answer.</summary>
    private async Task<IReadOnlyList<(string State, string Package)>?> ReadPackagesAsync(CancellationToken ct)
    {
        var (result, lines) = await RunDpkgQueryAsync(DpkgArgv(), ct).ConfigureAwait(false);
        var parsed = ParsePackages(lines);
        if (result.TimedOut) return null;
        // dpkg-query exits 1 when a pattern matches nothing and still prints the packages it knows. A failure
        // with neither a package line nor its "no packages found" message (dpkg missing) is unusable.
        if (result.ExitCode != 0 && parsed.Count == 0
            && !lines.Any(line => line.Contains("no packages found", StringComparison.OrdinalIgnoreCase)))
            return null;
        return [.. parsed.Where(item => item.State[1] != 'n')];
    }

    internal static IReadOnlyList<string> DpkgArgv() =>
        ["-W", "-f=${db:Status-Abbrev}\t${Package}\n", .. MailLeftovers.PackagePatterns];

    internal static List<(string State, string Package)> ParsePackages(IEnumerable<string> lines) =>
        [.. lines.Select(line => PackageLine().Match(line))
            .Where(match => match.Success)
            .Select(match => (match.Groups["state"].Value, match.Groups["package"].Value))];

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Trim();

    // "ii \tpostfix", "rc \topendkim", "iF \trspamd", "iUR\tdovecot-core": the status abbreviation (desired
    // action, current status, which dpkg writes in capitals for H, U, F, W and T, then the error flag R or a
    // blank), a tab, the package name. Same status grammar as PackageInventoryOperationExecutor.DpkgLine.
    [GeneratedRegex(@"^(?<state>[a-z][a-zA-Z][R ]?)\s*\t(?<package>[a-z0-9][a-z0-9+.:-]*)\s*$")]
    private static partial Regex PackageLine();
}
