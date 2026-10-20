// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>app-version</c> function, port of <c>deploy/scripts/app-version.sh</c>: composes
/// <c>&lt;major&gt;.&lt;minor&gt;.&lt;release number&gt;&lt;suffix&gt;</c> from the single
/// <c>&lt;VersionPrefix&gt;</c> of the workspace's <c>Directory.Build.props</c>, with the same checks and
/// the same messages. The script printed the version for <c>APP_VERSION="$(sh app-version.sh N)"</c>;
/// the function publishes it as <c>APP_VERSION</c>.
///
/// Differences, deliberate: the script's first argument (else <c>BUILD_RELEASENUMBER</c>) is the
/// <c>run_number</c> parameter (else the same variable), and <c>VERSION_SUFFIX</c> is the
/// <c>suffix</c> parameter (else the same variable). The props file is read in the workspace, where the
/// script fell back to its working directory only when <c>WORKSPACE</c> was unset.
/// </summary>
public sealed partial class AppVersionFunction : IPipelineFunction
{
    // The script's sed expression, applied line by line: the major and minor of a
    // <major>.<minor>[.<patch>] prefix, the last one of the line as the greedy .* picks it.
    [GeneratedRegex("^.*<VersionPrefix>([0-9]+)\\.([0-9]+)(\\.[0-9]+)?</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex("^[0-9]+\\z")]
    private static partial Regex Digits();

    [GeneratedRegex("^[0-9A-Za-z._-]*\\z")]
    private static partial Regex SuffixCharacters();

    public string Name => PipelineFunctionCatalog.AppVersion.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var runNumber = FunctionShell.Input(context, "run_number", "BUILD_RELEASENUMBER") ?? string.Empty;
        var suffix = FunctionShell.Input(context, "suffix", "VERSION_SUFFIX") ?? string.Empty;
        var props = context.Workspace + "/Directory.Build.props";

        if (!File.Exists(props))
            return PipelineFunctionResult.Failure($"Directory.Build.props not found at {props}.");
        if (!Digits().IsMatch(runNumber))
            return PipelineFunctionResult.Failure($"The run number must be digits only, got '{runNumber}'.");
        if (!SuffixCharacters().IsMatch(suffix))
            return PipelineFunctionResult.Failure($"VERSION_SUFFIX contains unexpected characters: '{suffix}'.");

        // Exactly one line holding <VersionPrefix> (grep -c counts lines): a second one, a conditional
        // override say, would make "the" version depend on which of them is read.
        var lines = (await File.ReadAllTextAsync(props, cancellationToken).ConfigureAwait(false)).Split('\n')
            .Where(line => line.Contains("<VersionPrefix>", StringComparison.Ordinal))
            .ToList();
        if (lines.Count != 1)
            return PipelineFunctionResult.Failure($"Directory.Build.props must declare exactly one <VersionPrefix>, found {lines.Count}.");
        if (VersionPrefix().Match(lines[0]) is not { Success: true } match)
            return PipelineFunctionResult.Failure("The <VersionPrefix> of Directory.Build.props is not <major>.<minor>[.<patch>].");

        // The control plane counts the release number within the series of the commit it pinned
        // (BUILD_RELEASESERIES). A workspace declaring another series would compose a version that
        // number was never counted in, so it is refused instead of composed. An explicit run_number
        // parameter is the caller's own number and is not checked.
        var series = $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        var allocatedSeries = context.Parameters.GetValueOrDefault("run_number") is { Length: > 0 }
            ? null
            : FunctionShell.Variable(context, "BUILD_RELEASESERIES");
        if (allocatedSeries is not null && !string.Equals(allocatedSeries, series, StringComparison.Ordinal))
            return PipelineFunctionResult.Failure(
                $"The release number was allocated in series {allocatedSeries}, but the <VersionPrefix> of Directory.Build.props is in series {series}.");

        var version = $"{series}.{runNumber}{suffix}";
        await context.Log($"Application version: {version}").ConfigureAwait(false);
        context.SetOutput("APP_VERSION", version);
        return PipelineFunctionResult.Success();
    }
}
