// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-33: the release version of a project onboarded through the setup wizard, on the machinery Aetheus versions
/// itself with (<c>release_number: allocate</c>, <see cref="PipelineReleaseNumbers"/>, per-series counters). Aetheus
/// reads its series from the <c>VersionPrefix</c> of its <c>Directory.Build.props</c>; a generated pipeline declares
/// it instead in the variable <see cref="PrefixVariable"/> (<c>"0.1"</c> by default), so a repository needs no file
/// of Aetheus's own build. A definition that allocates and declares that variable gets one version per run, written
/// as <see cref="VersionVariable"/> among the run's variables: the value of the run parameter
/// <see cref="ParameterName"/> when the launch gives one (a SemVer <c>major.minor.patch[-suffix]</c>), else
/// <c>&lt;series&gt;.&lt;allocated number&gt;</c>. A version a release of the project already carries is refused
/// before the run exists. A run with a requested version allocates no number: its version is its own, never
/// <c>series.number</c>. R-18: a <see cref="PrefixVariable"/> declared empty names no series, so the run counts in
/// the series of the repository's <c>VersionPrefix</c>, as a definition without the variable does, and still gets
/// its <see cref="VersionVariable"/>: a pipeline extending a versioned template keeps its repository as the single
/// source of the series.
/// </summary>
internal static partial class PipelineApplicationVersion
{
    /// <summary>The pipeline variable naming the series (<c>major.minor</c>, an optional <c>.patch</c> ignored).</summary>
    internal const string PrefixVariable = "APPLICATION_VERSION_PREFIX";

    /// <summary>The run variable holding the version of the release this run produces.</summary>
    internal const string VersionVariable = "APPLICATION_VERSION";

    /// <summary>The optional run parameter whose value, when given, is the version.</summary>
    internal const string ParameterName = "applicationVersion";

    /// <summary>Within <c>Release.Version</c> and readable as a git tag once prefixed with <c>v</c>.</summary>
    internal const int MaxLength = 64;

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8}))?\z")]
    private static partial Regex PrefixShape();

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex VersionShape();

    /// <summary>True for a SemVer <c>major.minor.patch</c> with an optional <c>-suffix</c>, within <see cref="MaxLength"/>.</summary>
    internal static bool IsValidVersion(string? version) =>
        version is { Length: > 0 and <= MaxLength } && VersionShape().IsMatch(version);

    /// <summary>
    /// The series the definition declares through <see cref="PrefixVariable"/>, null when it declares none or
    /// declares it empty (the repository's series then applies). A malformed prefix is refused: a typo must not
    /// quietly count the releases in another series.
    /// </summary>
    internal static string? DeclaredSeries(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Variables.TryGetValue(PrefixVariable, out var raw)) return null;
        var prefix = raw?.Trim() ?? string.Empty;
        if (prefix.Length == 0) return null;
        if (!TryParseSeries(prefix, out var series))
            throw PipelineProblem.Of(
                PipelineProblemCodes.ApplicationVersionPrefixInvalid,
                $"The pipeline variable {PrefixVariable} must be <major>.<minor> (for example 0.1 or 1.0), got '{prefix}'.",
                ("variable", PrefixVariable), ("prefix", prefix)).ToRefusal();
        return series;
    }

    private static bool TryParseSeries(string prefix, out string series)
    {
        var match = PrefixShape().Match(prefix);
        series = match.Success
            ? string.Create(CultureInfo.InvariantCulture, $"{match.Groups[1].Value}.{match.Groups[2].Value}")
            : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// The definition produces a versioned release: it allocates release numbers and declares
    /// <see cref="PrefixVariable"/>, empty included. A malformed prefix is refused here too.
    /// </summary>
    internal static bool Applies(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!PipelineReleaseNumbers.Allocates(definition) || !definition.Variables.ContainsKey(PrefixVariable)) return false;
        _ = DeclaredSeries(definition);
        return true;
    }

    /// <summary>
    /// The version the launch asks for (<see cref="ParameterName"/>), checked; null when it asks for none or the
    /// definition is not versioned. Called before any number is allocated, so a refused version takes none.
    /// </summary>
    internal static async Task<string?> CheckRequestedAsync(
        IPipelineReleaseNumberRepository repo, PipelineYamlDefinition definition, int? projectId,
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(parameters);
        if (projectId is not { } project || !Applies(definition)) return null;
        var requested = parameters
            .Where(entry => string.Equals(entry.Key, ParameterName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (requested is null) return null;
        if (!IsValidVersion(requested))
            throw PipelineProblem.Of(
                PipelineProblemCodes.ApplicationVersionInvalid,
                $"The version '{requested}' is not a version <major>.<minor>.<patch>, with an optional -suffix (for example 1.0.0 or 1.0.0-rc.1).",
                ("version", requested)).ToRefusal();
        await EnsureNotPublishedAsync(repo, project, requested, ct).ConfigureAwait(false);
        return requested;
    }

    /// <summary>
    /// Writes the run's version into <paramref name="variables"/>: <paramref name="requested"/> when the launch
    /// gave one, else the series and number allocated for this run, refused when a release already carries it.
    /// Writes nothing for a definition that is not versioned, and overwrites any value a caller passed in.
    /// </summary>
    internal static async Task ApplyAsync(
        IPipelineReleaseNumberRepository repo, PipelineYamlDefinition definition, int? projectId, string? requested,
        ReleaseNumberAssignment? release, IDictionary<string, string> variables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(variables);
        if (projectId is not { } project || !Applies(definition)) return;
        var version = requested ?? Compose(release);
        if (version is null)
        {
            EnsureSeriesKnown(definition, release);
            return;
        }
        if (requested is null)
            await EnsureNotPublishedAsync(repo, project, version, ct).ConfigureAwait(false);
        variables[VersionVariable] = version;
    }

    /// <summary>
    /// The version a launch of this definition would take without a requested one (<c>series.next</c>), shown in
    /// the launch dialog; null when the definition is not versioned or its prefix is malformed (the launch says so), and
    /// when its prefix is empty: the repository's series is read at the pinned commit of the launch only (R-18).
    /// </summary>
    internal static async Task<string?> ProposeAsync(
        IPipelineReleaseNumberRepository repo, PipelineYamlDefinition definition, int? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(definition);
        if (projectId is not { } project || !PipelineReleaseNumbers.Allocates(definition)) return null;
        // A malformed prefix proposes nothing: the dialog still opens, and the launch refuses it with its own sentence.
        if (!definition.Variables.TryGetValue(PrefixVariable, out var raw)
            || !TryParseSeries(raw?.Trim() ?? string.Empty, out var series)) return null;
        var next = await repo.PeekNextReleaseNumberAsync(project, series, ct).ConfigureAwait(false);
        return next is { } number ? Compose(new ReleaseNumberAssignment(series, number)) : null;
    }

    private static string? Compose(ReleaseNumberAssignment? release) =>
        release is { Series: { Length: > 0 } series, Number: var number }
            ? string.Create(CultureInfo.InvariantCulture, $"{series}.{number}")
            : null;

    /// <summary>
    /// R-18: a prefix declared empty leaves the series to the repository; a repository without a readable
    /// <c>VersionPrefix</c> gives none, and the release would be named with an unset version, so the launch is refused.
    /// </summary>
    private static void EnsureSeriesKnown(PipelineYamlDefinition definition, ReleaseNumberAssignment? release)
    {
        if (release is null || DeclaredSeries(definition) is not null) return;
        throw PipelineProblem.Of(
            PipelineProblemCodes.ApplicationVersionSeriesMissing,
            $"The pipeline variable {PrefixVariable} is empty, so the series is the VersionPrefix of Directory.Build.props, but the repository declares none: set {PrefixVariable} (for example 0.1) or add the VersionPrefix.",
            ("variable", PrefixVariable)).ToRefusal();
    }

    private static async Task EnsureNotPublishedAsync(
        IPipelineReleaseNumberRepository repo, int projectId, string version, CancellationToken ct)
    {
        if (await repo.ReleaseVersionExistsAsync(projectId, version, ct).ConfigureAwait(false))
            throw PipelineProblem.Of(
                PipelineProblemCodes.ApplicationVersionAlreadyPublished,
                $"Version {version} is already published for this project: choose another version, or raise {PrefixVariable}.",
                ("version", version), ("variable", PrefixVariable)).ToRefusal();
    }
}
