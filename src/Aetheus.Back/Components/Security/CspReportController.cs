// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Components.Security;

/// <summary>
/// PLAN-003 lot 14: where the browser says which script the policy refused.
///
/// A violation was seen on a run page for a script that is NOT in index.html - it is created at
/// runtime, because Blazor re-creates and executes any &lt;script&gt; found inside a rendered
/// MarkupString. The CSP was right to block it, and the refusal is the proof that some rendered HTML
/// carried a script. Without SSH and without the browser console, the only way to learn which one is
/// to have the browser tell the server.
///
/// Anonymous by necessity (the browser posts the report itself, with no session), and therefore
/// bounded: a small body limit and its own rate-limit partition, so the endpoint cannot be used to
/// flood the logs.
/// </summary>
[ApiController]
[Route("api/security")]
[AllowAnonymous]
[EnableRateLimiting("csp-report")]
[RequestSizeLimit(MaxBodyBytes)]
public sealed class CspReportController(ILogger<CspReportController> logger) : ControllerBase
{
    private const int MaxBodyBytes = 8192;

    /// <summary>
    /// A Reporting API batch holds several reports, so one accepted request could otherwise write as
    /// many log lines as minimal reports fit in the body cap (156 measured). A browser batches a
    /// handful; anything beyond that is flooding, and the surplus is dropped.
    /// </summary>
    private const int MaxReportsPerBatch = 8;

    /// <summary>
    /// Accepts both report shapes browsers send: the legacy <c>application/csp-report</c> body
    /// (<c>{ "csp-report": { ... } }</c>) and the Reporting API's <c>application/reports+json</c>
    /// batch (a JSON array of <c>{ "type": "csp-violation", "body": { ... } }</c>).
    /// <para>
    /// The body is read and parsed here rather than model-bound: the default JSON input formatter
    /// does not accept <c>application/csp-report</c> (it answered 415 to every real report-uri post),
    /// and the two shapes do not bind to a single DTO.
    /// </para>
    /// </summary>
    [HttpPost("csp-report")]
    [Consumes("application/csp-report", "application/reports+json", "application/json")]
    public async Task<IActionResult> ReportAsync(CancellationToken cancellationToken)
    {
        // 204 whatever happens: a browser has nothing to do with an error here, and telling it the
        // report was malformed only invites retries.
        var buffer = new byte[MaxBodyBytes];
        var read = await Request.Body.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        // Empty, or filling the cap exactly: dropped rather than parsed on a possibly truncated body.
        if (read is 0 or MaxBodyBytes) return NoContent();

        foreach (var report in ParseReports(new ReadOnlyMemory<byte>(buffer, 0, read)))
            Log(report);

        return NoContent();
    }

    /// <summary>Logs one already-parsed report. Not routable: the HTTP entry point is above.</summary>
    [NonAction]
    public IActionResult Report(CspReportEnvelope? envelope)
    {
        if (envelope?.CspReport is { } report) Log(report);
        return NoContent();
    }

    /// <summary>
    /// The schemes a browser gives to what its extensions inject. A page cannot stop an extension from
    /// trying, and the policy is right to block it: the report says nothing about Aetheus, so it goes to
    /// Debug instead of drowning the real violations in warnings.
    /// </summary>
    private static readonly string[] ExtensionSchemes =
        ["moz-extension", "chrome-extension", "safari-extension", "safari-web-extension", "ms-browser-extension"];

    private void Log(CspReportBody report)
    {
        const string Message = "CSP violation: {ViolatedDirective} blocked {BlockedUri} on {DocumentUri} "
            + "(source {SourceFile}:{LineNumber}, sample {ScriptSample})";
        var level = FromBrowserExtension(report) ? LogLevel.Debug : LogLevel.Warning;
        logger.Log(level, Message,
            Trim(report.ViolatedDirective), Trim(report.BlockedUri), Trim(report.DocumentUri),
            Trim(report.SourceFile), report.LineNumber, Trim(report.ScriptSample));
    }

    internal static bool FromBrowserExtension(CspReportBody report) =>
        IsExtension(report.SourceFile) || IsExtension(report.BlockedUri);

    private static bool IsExtension(string? value) =>
        value is not null && ExtensionSchemes.Any(scheme => value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads whichever of the two report shapes the browser sent. Malformed JSON yields nothing:
    /// the caller answers 204 either way.
    /// </summary>
    internal static IReadOnlyList<CspReportBody> ParseReports(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;

            // Legacy report-uri body: { "csp-report": { "document-uri": ..., "blocked-uri": ... } }
            if (root.ValueKind == JsonValueKind.Object)
            {
                return root.TryGetProperty("csp-report", out var legacy)
                       && legacy.ValueKind == JsonValueKind.Object
                    ? [ReadLegacy(legacy)]
                    : [];
            }

            // Reporting API batch: [ { "type": "csp-violation", "body": { "blockedURL": ... } } ]
            if (root.ValueKind != JsonValueKind.Array) return [];

            var reports = new List<CspReportBody>();
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(Text(entry, "type"), "csp-violation", StringComparison.Ordinal))
                    continue;
                if (!entry.TryGetProperty("body", out var reportBody)
                    || reportBody.ValueKind != JsonValueKind.Object)
                    continue;

                reports.Add(ReadReportingApi(reportBody, entry));
                if (reports.Count == MaxReportsPerBatch) break;
            }

            return reports;
        }
    }

    private static CspReportBody ReadLegacy(JsonElement report) => new()
    {
        DocumentUri = Text(report, "document-uri"),
        BlockedUri = Text(report, "blocked-uri"),
        ViolatedDirective = Text(report, "violated-directive") ?? Text(report, "effective-directive"),
        SourceFile = Text(report, "source-file"),
        LineNumber = Number(report, "line-number"),
        ScriptSample = Text(report, "script-sample")
    };

    /// <summary>The Reporting API names the same fields without hyphens.</summary>
    private static CspReportBody ReadReportingApi(JsonElement report, JsonElement entry) => new()
    {
        DocumentUri = Text(report, "documentURL") ?? Text(entry, "url"),
        BlockedUri = Text(report, "blockedURL"),
        ViolatedDirective = Text(report, "effectiveDirective") ?? Text(report, "violatedDirective"),
        SourceFile = Text(report, "sourceFile"),
        LineNumber = Number(report, "lineNumber"),
        ScriptSample = Text(report, "sample")
    };

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>A report is attacker-controlled text: keep it short and on one line in the log.</summary>
    private static string Trim(string? value)
    {
        if (value is null) return "-";

        // Fold first, then cut: folding CRLF pairs shortens the string, so cutting at the ORIGINAL
        // length would run past the end of the folded one.
        var single = value.ReplaceLineEndings(" ");
        return single[..Math.Min(single.Length, 300)];
    }
}

/// <summary>The legacy report body: <c>{ "csp-report": { ... } }</c>.</summary>
public sealed record CspReportEnvelope
{
    [JsonPropertyName("csp-report")]
    public CspReportBody? CspReport { get; init; }
}

public sealed record CspReportBody
{
    [MaxLength(2048)]
    [JsonPropertyName("document-uri")] public string? DocumentUri { get; init; }
    [MaxLength(2048)]
    [JsonPropertyName("blocked-uri")] public string? BlockedUri { get; init; }
    [MaxLength(2048)]
    [JsonPropertyName("violated-directive")] public string? ViolatedDirective { get; init; }
    [MaxLength(2048)]
    [JsonPropertyName("source-file")] public string? SourceFile { get; init; }
    [JsonPropertyName("line-number")] public int? LineNumber { get; init; }
    [MaxLength(2048)]
    [JsonPropertyName("script-sample")] public string? ScriptSample { get; init; }
}
