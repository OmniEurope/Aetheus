// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aetheus.Back.Components.Analysis;

public static class SarifAnalysisParser
{
    private const int MaxFindings = 100_000;
    private const int MaximumMessageLength = 4000;

    public static IReadOnlyList<ParsedAnalysisFinding> Parse(string content, AnalysisCategory category)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new BadRequestException($"Invalid SARIF report: {ex.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                throw new BadRequestException("Invalid SARIF report: runs must be an array.");

            var parsed = new List<ParsedAnalysisFinding>();
            foreach (var run in runs.EnumerateArray())
            {
                var tool = ReadTool(run);
                var rules = ReadRules(run);
                if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var result in results.EnumerateArray())
                {
                    // A suppressed result is one somebody already decided about, in the source, with
                    // a written justification. Reporting it again as a finding turns every accepted
                    // exception back into an open one at the next run, and a gate that keeps raising
                    // what has already been answered is a gate people stop reading.
                    if (IsSuppressed(result)) continue;

                    if (parsed.Count >= MaxFindings)
                        throw new BadRequestException($"SARIF report exceeds the maximum of {MaxFindings} findings.");

                    parsed.Add(ParseFinding(result, tool, rules, category));
                }
            }

            return parsed;
        }
    }

    /// <summary>
    /// SARIF 2.1.0 §3.27.23: a result carrying a non-empty <c>suppressions</c> array has been
    /// suppressed. An EMPTY array means the opposite, that the tool looked and found no suppression,
    /// so it must not be read as one.
    ///
    /// Only an <c>accepted</c> or absent <c>status</c> counts as suppressed (the schema names it
    /// <c>status</c>; a <c>state</c> property is not SARIF). A suppression the tool marked
    /// <c>rejected</c> or <c>underReview</c> is still an open question, and hiding it would answer
    /// that question on the tool's behalf.
    /// </summary>
    private static bool IsSuppressed(JsonElement result)
    {
        if (!result.TryGetProperty("suppressions", out var suppressions)
            || suppressions.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var suppression in suppressions.EnumerateArray())
        {
            if (suppression.ValueKind != JsonValueKind.Object) continue;
            var status = ReadString(suppression, "status");
            if (status is null
                || status.Equals("accepted", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ParsedAnalysisFinding ParseFinding(
        JsonElement result,
        string tool,
        IReadOnlyDictionary<string, RuleMetadata> rules,
        AnalysisCategory category)
    {
        var ruleId = ReadString(result, "ruleId") ?? "unknown";
        rules.TryGetValue(ruleId, out var rule);
        var rawMessage = ReadNestedString(result, "message", "text")
            ?? ReadNestedString(result, "message", "markdown")
            ?? rule?.Title
            ?? ruleId;
        var message = category == AnalysisCategory.Secrets
            ? $"Potential secret detected by rule {ruleId}."
            : Truncate(rawMessage, MaximumMessageLength);
        var location = ReadLocation(result);
        var cwe = ReadCwe(result) ?? rule?.Cwe;
        var severity = ReadSeverity(result, rule);
        var confidence = ReadConfidence(result);
        var title = Truncate(rule?.Title ?? message, 500);
        var suppliedFingerprint = ReadSuppliedFingerprint(result, rawMessage);
        var fingerprint = ComputeHash(string.Join('|',
            "v2",
            ((int)category).ToString(CultureInfo.InvariantCulture),
            (cwe ?? ruleId).Trim().ToLowerInvariant(),
            (location.Symbol ?? string.Empty).Trim().ToLowerInvariant(),
            suppliedFingerprint ?? NormalizeIdentityText(rawMessage)));
        var locationHash = ComputeHash(string.Join('|',
            fingerprint,
            tool.Trim().ToLowerInvariant(),
            NormalizePath(location.FilePath) ?? string.Empty,
            (location.StartLine ?? 0).ToString(CultureInfo.InvariantCulture),
            (location.EndLine ?? 0).ToString(CultureInfo.InvariantCulture)));

        return new ParsedAnalysisFinding
        {
            Fingerprint = fingerprint,
            LocationHash = locationHash,
            ToolName = Truncate(tool, 200),
            RuleId = Truncate(ruleId, 300),
            Category = category,
            Severity = severity,
            Confidence = confidence,
            Cwe = TruncateNullable(cwe, 50),
            Title = title,
            Message = message,
            HelpUri = NormalizeHelpUri(rule?.HelpUri),
            FilePath = NormalizePath(location.FilePath),
            StartLine = location.StartLine,
            EndLine = location.EndLine,
            Symbol = TruncateNullable(location.Symbol, 500)
        };
    }

    private static string ReadTool(JsonElement run)
    {
        return ReadNestedString(run, "tool", "driver", "name") ?? "unknown";
    }

    private static Dictionary<string, RuleMetadata> ReadRules(JsonElement run)
    {
        var result = new Dictionary<string, RuleMetadata>(StringComparer.Ordinal);
        if (!TryGetNested(run, out var rules, "tool", "driver", "rules") || rules.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var rule in rules.EnumerateArray())
        {
            var id = ReadString(rule, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            result[id] = new RuleMetadata(
                ReadNestedString(rule, "shortDescription", "text") ?? ReadNestedString(rule, "fullDescription", "text"),
                ReadString(rule, "helpUri"),
                ReadCwe(rule),
                ReadSecuritySeverity(rule));
        }

        return result;
    }

    private static AnalysisSeverity ReadSeverity(JsonElement result, RuleMetadata? rule)
    {
        var explicitSeverity = ReadNestedString(result, "properties", "severity");
        if (Enum.TryParse<AnalysisSeverity>(explicitSeverity, true, out var parsed)) return parsed;

        var numeric = ReadSecuritySeverity(result) ?? rule?.SecuritySeverity;
        if (numeric is >= 9.0) return AnalysisSeverity.Critical;
        if (numeric is >= 7.0) return AnalysisSeverity.High;
        if (numeric is >= 4.0) return AnalysisSeverity.Medium;
        if (numeric is > 0) return AnalysisSeverity.Low;

        return ReadString(result, "level")?.ToLowerInvariant() switch
        {
            "error" => AnalysisSeverity.High,
            "warning" => AnalysisSeverity.Medium,
            "note" => AnalysisSeverity.Low,
            "none" => AnalysisSeverity.Info,
            _ => AnalysisSeverity.Medium
        };
    }

    private static AnalysisConfidence ReadConfidence(JsonElement result)
    {
        var value = ReadNestedString(result, "properties", "confidence");
        return Enum.TryParse<AnalysisConfidence>(value, true, out var confidence)
            ? confidence
            : AnalysisConfidence.Unknown;
    }

    private static double? ReadSecuritySeverity(JsonElement element)
    {
        if (!TryGetNested(element, out var value, "properties", "security-severity")) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static string? ReadCwe(JsonElement element)
    {
        var direct = ReadNestedString(element, "properties", "cwe");
        if (!string.IsNullOrWhiteSpace(direct)) return NormalizeCwe(direct);

        if (!TryGetNested(element, out var tags, "properties", "tags") || tags.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String) continue;
            var value = tag.GetString();
            if (value?.Contains("CWE-", StringComparison.OrdinalIgnoreCase) == true)
                return NormalizeCwe(value);
        }
        return null;
    }

    private static string? NormalizeHelpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return Truncate(uri.AbsoluteUri, 2048);
    }

    private static string NormalizeCwe(string value)
    {
        var index = value.IndexOf("CWE-", StringComparison.OrdinalIgnoreCase);
        var normalized = index >= 0 ? value[index..] : value;
        var separator = normalized.IndexOfAny([' ', ',', ';', ')', ']']);
        return separator > 0 ? normalized[..separator].ToUpperInvariant() : normalized.ToUpperInvariant();
    }

    private static FindingLocation ReadLocation(JsonElement result)
    {
        if (!result.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            return new FindingLocation(null, null, null, null);
        var location = locations.EnumerateArray().FirstOrDefault();
        if (location.ValueKind == JsonValueKind.Undefined)
            return new FindingLocation(null, null, null, null);

        var filePath = ReadNestedString(location, "physicalLocation", "artifactLocation", "uri");
        var startLine = ReadNestedInt(location, "physicalLocation", "region", "startLine");
        var endLine = ReadNestedInt(location, "physicalLocation", "region", "endLine");
        var symbol = ReadNestedString(location, "logicalLocations", "0", "fullyQualifiedName")
            ?? ReadNestedString(location, "logicalLocations", "0", "name");
        return new FindingLocation(filePath, startLine, endLine, symbol);
    }

    /// <summary>Where the scanner containers mount the repository they analyse.</summary>
    private const string ScannerWorkspaceMount = "/src/";

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var decoded = Uri.UnescapeDataString(value).Replace('\\', '/');
        if (decoded.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            decoded = decoded[7..];
        if (decoded.Length >= 3 && char.IsLetter(decoded[0]) && decoded[1] == ':' && decoded[2] == '/')
            decoded = decoded[3..];
        // Recette R-504: a scanner run in a container sees the repository mounted on /src
        // (ScannerContainerProcessBuilder), so the absolute path it reports, /src/tests/x.py, is
        // tests/x.py in the repository. Kept as it was, it read "src/tests/x.py", a file that does not
        // exist, and the link to it fell back on the repository root. A relative path is left alone:
        // "src/Aetheus.Back/X.cs" is a real folder of the repository.
        if (decoded.StartsWith(ScannerWorkspaceMount, StringComparison.Ordinal))
            decoded = decoded[ScannerWorkspaceMount.Length..];
        decoded = decoded.TrimStart('/');
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "." && segment != "..")
            .ToArray();
        return Truncate(string.Join('/', segments), 1000);
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        return TryGetNested(element, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadNestedInt(JsonElement element, params string[] path)
    {
        return TryGetNested(element, out var value, path) && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>Recette R2-055: provenance a history scanner attaches to a result (gitleaks `git` mode
    /// fills author, email, date and commit message). They say who and when, not which finding: read
    /// first in key order, `author` merged every leak of one author into a single finding, so a vendored
    /// Monaco line and a real token in another file shared one identity.</summary>
    private static readonly HashSet<string> ProvenanceFingerprintKeys =
        new(StringComparer.OrdinalIgnoreCase) { "author", "email", "date", "commitMessage" };

    private static string? ReadSuppliedFingerprint(JsonElement result, string rawMessage)
    {
        if (!result.TryGetProperty("partialFingerprints", out var fingerprints)
            || fingerprints.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in fingerprints.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (ProvenanceFingerprintKeys.Contains(property.Name)
                || property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString()))
                continue;
            var supplied = $"{property.Name.ToLowerInvariant()}:{property.Value.GetString()!.Trim().ToLowerInvariant()}";
            // A commit holds several findings: the commit alone is not an identity, the result's own
            // text (which names the file) completes it.
            return string.Equals(property.Name, "commitSha", StringComparison.OrdinalIgnoreCase)
                ? $"{supplied}|{NormalizeIdentityText(rawMessage)}"
                : supplied;
        }
        return null;
    }

    private static string NormalizeIdentityText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();

    private static bool TryGetNested(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= value.GetArrayLength()) return false;
                value = value[index];
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        }
        return true;
    }

    private static string ComputeHash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
    private static string? TruncateNullable(string? value, int maxLength) => value is null ? null : Truncate(value, maxLength);

    private sealed record RuleMetadata(string? Title, string? HelpUri, string? Cwe, double? SecuritySeverity);
    private sealed record FindingLocation(string? FilePath, int? StartLine, int? EndLine, string? Symbol);
}
