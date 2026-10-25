// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>reconcile-env-urls</c> function, port of <c>reconcile-env-urls.sh</c> (the
/// <c>RECONCILE_ENV_URLS_PROGRAM</c> heredoc of the production, nightly and demo pipelines): refreshes
/// only the requested public URL lines of an environment file, so a persistent environment follows the
/// library's hosts without rotating a secret, keeps a dated copy, and replaces the file atomically.
///
/// Every pair is validated before anything is touched: the key is upper case, digits and underscores,
/// and the value is <c>https://host[:port]</c> with nothing else (no path, no character outside
/// <c>[a-zA-Z0-9.:-]</c>), because it is written into a file Compose reads. A pair already holding the
/// value is skipped; a key present is rewritten in place (every line that starts with <c>KEY=</c>); a
/// key absent is appended. When nothing changes the file and its directory are left as they are. When
/// something does, a dated copy <c>&lt;file&gt;-YYYYMMDDTHHMMSSZ</c> of the file as it was is kept
/// (mode 600, never overwritten) and the new file is moved into place with mode 600.
///
/// Same log lines as the script. Differences, deliberate: the pairs are the required <c>urls</c>
/// parameter (separated by spaces, a URL holds none) and the file the <c>env_file</c> parameter,
/// defaulting to <c>ENV_FILE</c>; a refusal is the function's failure reason, without the script's
/// <c>FATAL:</c> prefix; a file whose last line has no newline gets the new line on a line of its own
/// (the script glued it to the last line).
/// </summary>
public sealed partial class ReconcileEnvUrlsFunction : IPipelineFunction
{
    [GeneratedRegex("^[A-Z0-9_]+\\z")]
    private static partial Regex EnvironmentKey();

    [GeneratedRegex("^[a-zA-Z0-9.:-]+\\z")]
    private static partial Regex Origin();

    private readonly TimeProvider _time;

    public ReconcileEnvUrlsFunction()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Tests stand a clock in.</summary>
    internal ReconcileEnvUrlsFunction(TimeProvider time) => _time = time;

    public string Name => PipelineFunctionCatalog.ReconcileEnvUrls.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var envFile = ProductionEnvFiles.EnvFileInput(context);
        if (envFile is null)
            return PipelineFunctionResult.Failure("ENV_FILE is required");
        // Required and non-blank: the executor refuses the step as invalid-request otherwise.
        var pairs = context.Parameters["urls"].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!ProductionEnvFiles.IsRegularFile(envFile))
            return PipelineFunctionResult.Failure($"Not a regular environment file: {envFile}");
        if (pairs.Select(Refusal).FirstOrDefault(refusal => refusal is not null) is { } refused)
            return PipelineFunctionResult.Failure(refused);

        var lines = ProductionEnvFiles.Lines(await File.ReadAllTextAsync(envFile, cancellationToken).ConfigureAwait(false));
        var changed = new List<string>();
        foreach (var pair in pairs)
        {
            var (key, url) = Split(pair);
            var current = CurrentValue(lines, key);
            if (current == url)
                continue;
            Set(lines, key, pair);
            await context.Log($">>> {key}: '{current ?? "unset"}' -> '{url}'").ConfigureAwait(false);
            changed.Add(key);
        }

        if (changed.Count == 0)
        {
            await context.Log(">>> Public URLs already match the libraries.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        var copy = ProductionEnvFiles.DatedCopyPath(envFile, _time);
        ProductionEnvFiles.EnsureDatedCopy(envFile, copy);
        await ProductionEnvFiles.ReplaceAsync(
            envFile, $"{envFile}.urls.{Environment.ProcessId}", ProductionEnvFiles.Join(lines), cancellationToken).ConfigureAwait(false);
        await context.Log($">>> Rewrote {string.Join(' ', changed)} in {envFile} (previous copy: {copy}).").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The pair as the script cut it: the key before the first <c>=</c> and the URL after it
    /// (both the whole word when it holds none).</summary>
    private static (string Key, string Url) Split(string pair)
    {
        var equals = pair.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (pair, pair) : (pair[..equals], pair[(equals + 1)..]);
    }

    /// <summary>Why the pair is refused, or null: its key is not an environment key, or its value is
    /// not a bare https origin.</summary>
    private static string? Refusal(string pair)
    {
        var (key, url) = Split(pair);
        if (!EnvironmentKey().IsMatch(key))
            return $"Not an environment key: '{key}'";
        const string scheme = "https://";
        if (!url.StartsWith(scheme, StringComparison.Ordinal))
            return $"{key} must be an https URL: '{url}'";
        // https://*/* and https://*[!a-zA-Z0-9.:-]* are refused; an empty host is no https URL at all.
        var rest = url[scheme.Length..];
        if (rest.Length == 0)
            return $"{key} must be an https URL: '{url}'";
        return Origin().IsMatch(rest) ? null : $"{key} must be https://host[:port] with nothing else: '{url}'";
    }

    /// <summary><c>sed -n "s/^KEY=//p" | tail -n 1</c>: what follows <c>KEY=</c> on the last line that
    /// starts with it, or null when none does.</summary>
    private static string? CurrentValue(List<string> lines, string key)
    {
        var prefix = key + "=";
        var last = lines.FindLast(line => line.StartsWith(prefix, StringComparison.Ordinal));
        return last?[prefix.Length..];
    }

    /// <summary>Every line that starts with <c>KEY=</c> becomes the pair; with none, the pair is
    /// appended.</summary>
    private static void Set(List<string> lines, string key, string pair)
    {
        var prefix = key + "=";
        var found = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith(prefix, StringComparison.Ordinal))
                continue;
            lines[index] = pair;
            found = true;
        }
        if (!found)
            ProductionEnvFiles.Append(lines, pair);
    }
}
