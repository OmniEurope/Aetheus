// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Why a test run could not be graded at all.</summary>
public sealed record DotnetTestClassificationFailure(string Reason);

/// <summary>
/// Draws the one line that every project writing this in YAML gets wrong: a suite that RAN and
/// reported failures is a finding, graded into the assurance contract; a suite that produced no
/// usable evidence is a technical failure of the run itself.
///
/// The natural shell form (<c>dotnet test || exit 1</c>) collapses both into "the step is red", which
/// makes a crashed test host indistinguishable from an honest regression, and makes a run that
/// executed zero tests indistinguishable from one where everything passed.
///
/// So the exit code is never trusted on its own. It is reconciled against the TRX counters, and any
/// disagreement between them is itself a refusal: an exit code of 0 with failures recorded, or a
/// non-zero exit with nothing failed, means one of the two is lying and neither can be graded.
///
/// The shell twin of this rule (the former <c>classify-dotnet-test-result.mjs</c> script) was deleted with
/// the last pipeline step that ran it; <c>DotnetTestClassifierVerdictTests</c> pins the verdicts both gave
/// on a shared corpus.
/// </summary>
public static class DotnetTestResultClassifier
{
    /// <summary>Refuses an evidence file that is empty or implausibly large before reading it. A TRX
    /// of a few hundred megabytes is a runaway log, not evidence, and reading it would take the agent
    /// down with it.</summary>
    public const long MaxEvidenceBytes = 100L * 1024 * 1024;

    private static readonly Regex AttributePattern = new(
        "([A-Za-z][A-Za-z0-9-]*)=\"([^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex ResultSummaryPattern = new(
        @"<ResultSummary\b([^>]*)>", RegexOptions.Compiled);

    private static readonly Regex CountersPattern = new(
        @"<Counters\b([^>]*)/?\s*>", RegexOptions.Compiled);

    private static readonly Regex CoverageRootPattern = new(
        @"<coverage\b([^>]*)>", RegexOptions.Compiled);

    /// <summary>The counters that mean the test host itself broke, as opposed to a test failing.</summary>
    private static readonly string[] TechnicalFailureCounters =
        ["error", "timeout", "aborted", "disconnected"];

    /// <summary>
    /// 0 when the suite ran clean, 1 when it ran and recorded failures, or a failure describing why
    /// the evidence cannot be graded at all.
    /// </summary>
    /// <param name="rawExitCode">What the test runner actually returned.</param>
    /// <param name="trxPath">The TRX the run produced.</param>
    /// <param name="coverageDirectory">Where to look for Cobertura reports, or null to not require any.</param>
    public static OneOf<int, DotnetTestClassificationFailure> Classify(
        int rawExitCode, string trxPath, string? coverageDirectory, IFileSystemReader files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (rawExitCode is < 0 or > 255)
            return new DotnetTestClassificationFailure("The raw dotnet test exit code is invalid.");

        if (ReadBounded(trxPath, files) is not { } trx)
            return new DotnetTestClassificationFailure(
                $"TRX evidence must contain between 1 and {MaxEvidenceBytes} bytes.");

        var reading = ReadTrx(trx);
        if (!reading.IsValue) return reading.Failure;

        if (coverageDirectory is not null
            && ValidateCoverage(coverageDirectory, files) is { } coverageFailure)
            return coverageFailure;

        return Reconcile(rawExitCode, reading.Value);
    }

    /// <summary>What a TRX has to say, once it has been read and found internally consistent.</summary>
    private readonly record struct TrxEvidence(int Failed, string Outcome);

    /// <summary>
    /// Reads the counters and refuses everything that makes them ungradeable, before any comparison
    /// with the runner's exit code. A counter that is absent is refused rather than defaulted to
    /// zero: defaulting would let a truncated TRX read as a clean run.
    /// </summary>
    private static OneOf<TrxEvidence, DotnetTestClassificationFailure> ReadTrx(string trx)
    {
        var summaries = ResultSummaryPattern.Matches(trx);
        var counterNodes = CountersPattern.Matches(trx);
        if (summaries.Count != 1 || counterNodes.Count != 1)
            return new DotnetTestClassificationFailure(
                "TRX evidence must contain exactly one result summary and one counter set.");

        var summary = Attributes(summaries[0].Groups[1].Value);
        var counters = ReadCounters(Attributes(counterNodes[0].Groups[1].Value));
        if (!counters.IsValue) return counters.Failure;

        var outcome = summary.GetValueOrDefault("outcome");
        if (outcome is not ("Completed" or "Failed"))
            return new DotnetTestClassificationFailure(
                $"TRX outcome '{outcome ?? "missing"}' is incomplete.");

        if (counters.Value.TechnicalFailures > 0)
            return new DotnetTestClassificationFailure("TRX records a technical test-host failure.");

        return new TrxEvidence(counters.Value.Failed, outcome);
    }

    private readonly record struct TrxCounters(int Failed, int TechnicalFailures);

    /// <summary>The failed and technical-failure counts, once every counter is present and the
    /// execution counters prove at least one executed test.</summary>
    private static OneOf<TrxCounters, DotnetTestClassificationFailure> ReadCounters(
        IReadOnlyDictionary<string, string> counters)
    {
        if (Counter(counters, "total") is not { } total
            || Counter(counters, "executed") is not { } executed
            || Counter(counters, "passed") is not { } passed
            || Counter(counters, "failed") is not { } failed
            || SumTechnicalFailures(counters) is not { } technicalFailures)
            return new DotnetTestClassificationFailure("A TRX counter is missing or invalid.");

        if (total <= 0 || executed <= 0 || executed > total || passed + failed > executed)
            return new DotnetTestClassificationFailure(
                "TRX execution counters are inconsistent or prove no executed test.");

        return new TrxCounters(failed, technicalFailures);
    }

    /// <summary>Null when any of the technical counters is absent or malformed.</summary>
    private static int? SumTechnicalFailures(IReadOnlyDictionary<string, string> counters)
    {
        var total = 0;
        foreach (var name in TechnicalFailureCounters)
        {
            if (Counter(counters, name) is not { } value) return null;
            total += value;
        }
        return total;
    }

    /// <summary>
    /// The last step, and the one this class exists for: the runner's exit code is only believed when
    /// the TRX agrees with it. A zero exit with recorded failures, or a non-zero exit with nothing
    /// failed, means one of the two is lying and neither can be graded.
    /// </summary>
    private static OneOf<int, DotnetTestClassificationFailure> Reconcile(
        int rawExitCode, TrxEvidence evidence)
    {
        if (rawExitCode == 0 && evidence.Failed == 0) return 0;
        if (rawExitCode != 0 && evidence.Failed > 0 && evidence.Outcome == "Failed") return 1;
        return new DotnetTestClassificationFailure(
            "dotnet test exit code and TRX counters are inconsistent.");
    }

    /// <summary>
    /// A coverage run that produced no report, or a report whose line counters are absent or
    /// impossible, is not partial evidence: it is a gate that would report a clean score for a
    /// measurement nobody made.
    /// </summary>
    private static DotnetTestClassificationFailure? ValidateCoverage(
        string directory, IFileSystemReader files)
    {
        var reports = files.FindFiles(directory, "coverage.cobertura.xml");
        if (reports.Count == 0)
            return new DotnetTestClassificationFailure("Expected at least one Cobertura report, found 0.");

        foreach (var report in reports)
        {
            if (ReadBounded(report, files) is not { } xml)
                return new DotnetTestClassificationFailure(
                    $"Cobertura evidence must contain between 1 and {MaxEvidenceBytes} bytes.");

            var root = CoverageRootPattern.Match(xml);
            if (!root.Success)
                return new DotnetTestClassificationFailure("Cobertura evidence has no coverage root.");

            var values = Attributes(root.Groups[1].Value);
            if (!TryInt(values, "lines-valid", out var valid)
                || !TryInt(values, "lines-covered", out var covered)
                || !TryDouble(values, "line-rate", out var rate)
                || valid <= 0 || covered < 0 || covered > valid || rate < 0 || rate > 1)
                return new DotnetTestClassificationFailure(
                    "Cobertura line counters are missing or invalid.");
        }

        return null;
    }

    private static string? ReadBounded(string path, IFileSystemReader files)
    {
        var size = files.FileSize(path);
        return size is <= 0 or > MaxEvidenceBytes ? null : files.ReadText(path);
    }

    private static Dictionary<string, string> Attributes(string fragment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AttributePattern.Matches(fragment))
            values[match.Groups[1].Value] = match.Groups[2].Value;
        return values;
    }

    /// <summary>A counter must be a plain non-negative integer. A missing one is refused rather than
    /// defaulted to zero: defaulting would let a truncated TRX read as a clean run.</summary>
    private static int? Counter(IReadOnlyDictionary<string, string> counters, string name)
    {
        if (!counters.TryGetValue(name, out var raw) || raw.Length == 0) return null;
        foreach (var character in raw)
            if (character is < '0' or > '9') return null;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> values, string name, out int result)
    {
        result = 0;
        return values.TryGetValue(name, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryDouble(IReadOnlyDictionary<string, string> values, string name, out double result)
    {
        result = 0;
        return values.TryGetValue(name, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && !double.IsNaN(result) && !double.IsInfinity(result);
    }
}

/// <summary>The file access the classifier needs, so its rules can be tested without a disk.</summary>
public interface IFileSystemReader
{
    /// <summary>The file's size in bytes, or -1 when it does not exist.</summary>
    long FileSize(string path);

    string ReadText(string path);

    /// <summary>Every file named <paramref name="fileName"/> under <paramref name="directory"/>,
    /// recursively. Empty when the directory does not exist.</summary>
    IReadOnlyList<string> FindFiles(string directory, string fileName);
}

/// <summary>The real disk.</summary>
public sealed class PhysicalFileSystemReader : IFileSystemReader
{
    public long FileSize(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    public string ReadText(string path) => File.ReadAllText(path);

    public IReadOnlyList<string> FindFiles(string directory, string fileName) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, fileName, SearchOption.AllDirectories)
            : [];
}

/// <summary>A result that is either a value or a failure, without throwing for the expected case.</summary>
public readonly struct OneOf<TValue, TFailure>
{
    private readonly TValue _value;
    private readonly TFailure? _failure;

    private OneOf(TValue value, TFailure? failure, bool isValue)
    {
        _value = value;
        _failure = failure;
        IsValue = isValue;
    }

    public bool IsValue { get; }

    public TValue Value => IsValue
        ? _value
        : throw new InvalidOperationException("This result holds a failure, not a value.");

    public TFailure Failure => IsValue
        ? throw new InvalidOperationException("This result holds a value, not a failure.")
        : _failure!;

    public static implicit operator OneOf<TValue, TFailure>(TValue value) => new(value, default, true);

    public static implicit operator OneOf<TValue, TFailure>(TFailure failure) =>
        new(default!, failure, false);
}
