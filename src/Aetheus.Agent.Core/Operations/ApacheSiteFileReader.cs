// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>What reading an Apache site file gave: its text, or why it could not be read.</summary>
internal readonly record struct SiteFileRead(string? Text, string? Failure)
{
    public static SiteFileRead Of(string text) => new(text, null);
    public static SiteFileRead Failed(string reason) => new(null, reason);
}

/// <summary>
/// Reads one Apache site file the agent account cannot open itself: certbot writes <c>&lt;site&gt;-le-ssl.conf</c> as
/// root with mode 0600. The file is read through <c>sudo -n aetheus-apache-read-site read</c>, the root-owned
/// read-only helper of the <c>aetheus-apache</c> grant: its argv is the one fixed word <c>read</c>, the path travels on
/// stdin, and the helper accepts only a plain file name of <c>sites-enabled</c> or <c>sites-available</c> (a link
/// followed must stay directly in those two directories; regular file, at most 1 MiB). The name is checked here
/// first, so a name that could leave those directories never reaches the helper.
/// </summary>
internal sealed partial class ApacheSiteFileReader(ILogger logger)
{
    /// <summary>The helper, rendered by <c>install-agent-linux.sh --enable-apache-manage</c> from
    /// <c>deploy/agent-host-config/apache/aetheus-apache-read-site</c>.</summary>
    internal const string HelperPath = "/usr/local/lib/aetheus/aetheus-apache-read-site";

    internal const int TimeoutSeconds = 30;
    private const string ProcessLinePrefix = "[process] ";

    /// <summary>Runs the helper with the given sudo argv and stdin, handing back its result and output lines;
    /// replaceable so the argv and the input are asserted without sudo.</summary>
    internal Func<IReadOnlyList<string>, string, int, CancellationToken, Task<(ExecutorResult Result, IReadOnlyList<string> Lines)>>? RunHelperAsync { get; init; }

    /// <summary>The exact <c>sudo</c> argv, word for word the line of the aetheus-apache grant. The path is never part of it.</summary>
    internal static IReadOnlyList<string> HelperArgv { get; } = ["-n", HelperPath, "read"];

    /// <summary>Whether <paramref name="siteName"/> is a plain file name (no separator, no leading dash, no
    /// control character): the rule the helper applies again.</summary>
    internal static bool IsPlainSiteName(string siteName) => SiteNameRegex().IsMatch(siteName);

    public async Task<SiteFileRead> ReadAsync(string directory, string siteName, CancellationToken ct)
    {
        if (!IsPlainSiteName(siteName))
            return SiteFileRead.Failed($"'{siteName}' is not a plain site file name: not read through the helper");

        var run = RunHelperAsync ?? RunWithSudoAsync;
        (ExecutorResult Result, IReadOnlyList<string> Lines) outcome;
        try
        {
            outcome = await run(HelperArgv, $"{directory.TrimEnd('/')}/{siteName}\n", TimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The Apache site read helper could not run for {Site}", siteName);
            return SiteFileRead.Failed($"the read helper could not run: {ex.Message}");
        }

        if (outcome.Result.TimedOut)
            return SiteFileRead.Failed($"the read helper did not answer within {TimeoutSeconds}s");
        if (outcome.Result.ExitCode != 0)
            return SiteFileRead.Failed($"the read helper refused or failed (exit code {outcome.Result.ExitCode}){Detail(outcome.Lines)}");
        // Every line but the runner's own exit-code line is the file: stderr lines are kept too (a helper that
        // succeeded prints none), so nothing that was printed is ever left out of the check.
        return SiteFileRead.Of(string.Join('\n', outcome.Lines.Where(line => !line.StartsWith(ProcessLinePrefix, StringComparison.Ordinal))));
    }

    private Task<(ExecutorResult Result, IReadOnlyList<string> Lines)> RunWithSudoAsync(
        IReadOnlyList<string> argv, string standardInput, int timeoutSeconds, CancellationToken ct) =>
        CaptureAsync(argv, standardInput, timeoutSeconds, ct);

    private async Task<(ExecutorResult Result, IReadOnlyList<string> Lines)> CaptureAsync(
        IReadOnlyList<string> argv, string standardInput, int timeoutSeconds, CancellationToken ct)
    {
        var psi = SudoProcessStartInfo.Create();
        foreach (var argument in argv) psi.ArgumentList.Add(argument);
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(psi, timeoutSeconds, (line, _) =>
        {
            lock (lines) lines.Add(line);
            return Task.CompletedTask;
        }, logger, ct, standardInput).ConfigureAwait(false);
        return (result, lines);
    }

    /// <summary>The last line the helper said on stderr (it says why it refused), as a suffix; empty when it said nothing.</summary>
    private static string Detail(IReadOnlyList<string> lines)
    {
        var reason = lines.LastOrDefault(line => line.StartsWith("[stderr] ", StringComparison.Ordinal) && line.Length > "[stderr] ".Length);
        return reason is null ? string.Empty : ": " + reason["[stderr] ".Length..].Trim();
    }

    // \z, not $: $ also matches before a final line feed. Same rule as the helper's own check.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.\-]{0,127}\z")]
    private static partial Regex SiteNameRegex();
}
