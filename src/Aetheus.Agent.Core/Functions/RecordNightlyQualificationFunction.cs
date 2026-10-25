// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>record-nightly-qualification</c> function, port of
/// the former <c>record-nightly-qualification.mjs</c> script: reads the security, quality, security
/// history and extended dynamic security summaries (<see cref="NightlyQualificationSummaries"/>),
/// refuses one that names another commit, and writes the schema 1 qualification byte for byte as the
/// script does (<see cref="JsJson.StringifyIndented"/> plus a line feed, its directory created), with
/// <c>generatedAt</c> the current instant as <c>new Date().toISOString()</c> writes it and the seal the
/// SHA-256 of <c>JSON.stringify</c> of everything before it. The log line and every refusal are the
/// script's, verbatim.
///
/// Parameters are the script's arguments, paths relative to the workspace; <c>assurance_grade</c>,
/// when not empty, stands for the <c>CANDIDATE_ASSURANCE_GRADE</c> variable the script read. The clock
/// is a <see cref="TimeProvider"/>, the system clock outside tests.
///
/// Differences, deliberate: an empty required parameter is refused by the request validation with its
/// own message (the script printed its usage); a summary nested deeper than the .NET stack allows is
/// refused instead of being read; a FIFO, socket or device named like a summary is counted as a file,
/// which <c>readdirSync</c>'s entry type did not; a directory that cannot be listed or an output that
/// cannot be written fails with the .NET message instead of Node's.
/// </summary>
public sealed partial class RecordNightlyQualificationFunction : IPipelineFunction
{
    private const string Usage =
        "Usage: record-nightly-qualification.mjs <output> <source-sha> <security-directory> [quality-directory] [security-history-directory] [extended-dynamic-security-directory]";
    private const string SecuritySummary = "security-summary.json";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _time;

    public RecordNightlyQualificationFunction()
        : this(TimeProvider.System)
    {
    }

    internal RecordNightlyQualificationFunction(TimeProvider time) => _time = time;

    [GeneratedRegex("^[0-9a-fA-F]{40}\\z|^[0-9a-fA-F]{64}\\z")]
    private static partial Regex SourceSha();

    public string Name => PipelineFunctionCatalog.RecordNightlyQualification.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var outputPath = context.Parameters["output"];
        var sourceSha = context.Parameters["source_sha"];
        var securityDirectory = context.Parameters["security"];
        if (outputPath.Length == 0 || sourceSha.Length == 0 || securityDirectory.Length == 0)
            throw new JsError(Usage);
        if (!SourceSha().IsMatch(sourceSha))
            throw new JsError("The nightly source SHA must be a full hexadecimal commit identifier.");

        var workspace = context.Workspace;
        var security = NightlyQualificationSummaries.Find(workspace, securityDirectory, SecuritySummary);
        var quality = NightlyQualificationSummaries.Find(workspace, context.Parameters.GetValueOrDefault("quality"), "quality-summary.json");
        var securityHistory = NightlyQualificationSummaries.Find(workspace, context.Parameters.GetValueOrDefault("security_history"), SecuritySummary);
        var extendedDynamicSecurity = NightlyQualificationSummaries.Find(
            workspace, context.Parameters.GetValueOrDefault("extended_dynamic_security"), SecuritySummary);

        var sourceCommit = JsValue.AsciiLower(sourceSha);
        foreach (var (label, summary) in new[]
                 {
                     ("security", security), ("quality", quality),
                     ("security history", securityHistory), ("extended dynamic security", extendedDynamicSecurity)
                 })
        {
            if (NightlyQualificationSummaries.BelongsToAnotherCommit(summary, sourceCommit))
                throw new JsError($"The nightly {label} summary belongs to another commit.");
        }

        var qualification = new JsObject();
        qualification.Set("schema", 1d);
        qualification.Set("profile", "full");
        qualification.Set("sourceCommit", sourceCommit);
        qualification.Set("generatedAt", JsText.ToIsoString(_time.GetUtcNow().ToUnixTimeMilliseconds()));
        qualification.Set("security", NightlyQualificationSummaries.DescribeWithCounts(security));
        qualification.Set("quality", NightlyQualificationSummaries.Describe(quality));
        qualification.Set("securityHistory", NightlyQualificationSummaries.DescribeWithCounts(securityHistory));
        qualification.Set("extendedDynamicSecurity", NightlyQualificationSummaries.DescribeWithCounts(extendedDynamicSecurity));
        var assuranceGrade = FunctionShell.Input(context, "assurance_grade", "CANDIDATE_ASSURANCE_GRADE") ?? "";
        qualification.Set("assuranceGrade", NightlyQualificationSummaries.Grade().IsMatch(assuranceGrade) ? assuranceGrade : null);
        qualification.Set("seal", Seal(qualification));

        var output = Path.GetFullPath(Path.Combine(workspace, outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // Written as text with the BOM-less encoding (the same bytes as Utf8.GetBytes): handing File.WriteAllBytesAsync
        // the result of Utf8.GetBytes(JsJson...) made the CA5390/CA5403 data-flow analyzers walk into the JSON
        // serializer and take over two minutes per compilation (measured with -p:ReportAnalyzer=true).
        await File.WriteAllTextAsync(output, JsJson.StringifyIndented(qualification) + "\n", Utf8, cancellationToken).ConfigureAwait(false);
        await context.Log(Summary(qualification)).ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>{ algorithm: "sha256", digest: sha256(JSON.stringify(qualification)) }</c>.</summary>
    private static JsObject Seal(JsObject qualification)
    {
        var seal = new JsObject();
        seal.Set("algorithm", "sha256");
        seal.Set("digest", Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(JsJson.Stringify(qualification)!))));
        return seal;
    }

    /// <summary>The script's <c>console.log</c> line.</summary>
    private static string Summary(JsObject qualification)
    {
        var security = (JsObject)qualification["security"]!;
        var securityHistory = (JsObject)qualification["securityHistory"]!;
        var extendedDynamicSecurity = (JsObject)qualification["extendedDynamicSecurity"]!;
        string Text(object? value, string fallback) => JsValue.ToJsString(JsValue.Coalesce(value, fallback));
        return $"Nightly qualification recorded for {JsValue.ToJsString(qualification["sourceCommit"])}: security {Text(security["status"], "")}"
            + $" grade {Text(security["grade"], "-")}, critical {Text(security["criticalFindings"], "not measured")},"
            + $" high {Text(security["highFindings"], "not measured")}; security history"
            + $" {Text(securityHistory["status"], "")}, extended dynamic security"
            + $" {Text(extendedDynamicSecurity["status"], "")}; assurance grade {Text(qualification["assuranceGrade"], "not sealed")}.";
    }
}
