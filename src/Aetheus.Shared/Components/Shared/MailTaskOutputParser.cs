// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aetheus.Shared.Components.Shared;

/// <summary>Parses machine-readable mail-helper output consistently across agent, backend and
/// frontend. Ignore unrecognized log chatter rather than rejecting the complete output.</summary>
public static class MailTaskOutputParser
{
    /// <summary>Parses <c>postqueue -j</c> output (one JSON object per line).</summary>
    public static List<MailQueueItemDto> ParseQueue(string? output)
    {
        var items = new List<MailQueueItemDto>();
        foreach (var line in Lines(output))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                items.Add(ToQueueItem(doc.RootElement));
            }
            catch (JsonException)
            {
                // Not a postqueue record (helper chatter or a truncated line): skip it.
            }
        }
        return items;
    }

    /// <summary>Recette R3-23: parses the <c>LEFTOVER kind name detail</c> lines and the closing
    /// <c>CLEAN yes|no</c> line of a mail leftovers check.</summary>
    public static MailLeftoversReportDto ParseLeftovers(string? output)
    {
        var items = TabFields(output, "LEFTOVER")
            .Where(fields => fields.Length >= 3)
            .Select(fields => new MailLeftoverDto
            {
                Kind = fields[1],
                Name = fields[2],
                Detail = fields.Length >= 4 ? fields[3] : string.Empty
            })
            .ToList();
        var complete = TabFields(output, "CLEAN").Any(fields => fields.Length >= 2 && fields[1] is "yes" or "no");
        return new MailLeftoversReportDto { IsComplete = complete, Items = items };
    }

    /// <summary>Parses <c>QUOTA &lt;email&gt; &lt;KiB&gt;</c> lines into a case-insensitive map.</summary>
    public static Dictionary<string, long> ParseQuotaReport(string? output)
    {
        var usage = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var fields in TabFields(output, "QUOTA"))
        {
            if (fields.Length >= 3 && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kib))
                usage[fields[1]] = kib;
        }
        return usage;
    }

    /// <summary>Parses the last <c>DELIVERY status qid token detail</c> line, or null when absent.</summary>
    public static MailDeliveryResultDto? ParseDelivery(string? output)
    {
        MailDeliveryResultDto? result = null;
        foreach (var fields in TabFields(output, "DELIVERY"))
        {
            if (fields.Length < 4) continue;
            result = new MailDeliveryResultDto
            {
                Status = fields[1],
                QueueId = fields[2],
                MessageMarker = fields[3],
                Detail = fields.Length > 4 ? fields[4] : string.Empty
            };
        }
        return result;
    }

    /// <summary>Parses <c>CHECK component ok|fail detail</c> lines.</summary>
    public static List<MailComponentCheckDto> ParseChecks(string? output) =>
        TabFields(output, "CHECK")
            .Where(fields => fields.Length >= 3)
            .Select(fields => new MailComponentCheckDto
            {
                Component = fields[1],
                IsOk = fields[2] == "ok",
                Detail = fields.Length > 3 ? fields[3].Trim() : string.Empty
            })
            .ToList();

    /// <summary>
    /// Extracts the TXT value from an OpenDKIM <c>&lt;selector&gt;.txt</c> file: the concatenation of every
    /// double-quoted chunk (<c>"v=DKIM1; k=rsa; " "p=MIIB..."</c>). Empty when no quoted chunk exists.
    /// </summary>
    public static string ParseDkimTxtRecord(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var value = new StringBuilder();
        var inside = false;
        foreach (var c in content)
        {
            if (c == '"') { inside = !inside; continue; }
            if (inside) value.Append(c);
        }
        return value.ToString().Trim();
    }

    /// <summary>Returns the <c>p=</c> public key of a DKIM TXT value, whitespace removed.</summary>
    public static string DkimPublicKeyOf(string? txtValue)
    {
        if (string.IsNullOrEmpty(txtValue)) return string.Empty;
        foreach (var tag in txtValue.Split(';'))
        {
            var trimmed = tag.Trim();
            if (trimmed.StartsWith("p=", StringComparison.Ordinal))
                return string.Concat(trimmed[2..].Where(c => !char.IsWhiteSpace(c)));
        }
        return string.Empty;
    }

    private static MailQueueItemDto ToQueueItem(JsonElement root)
    {
        var recipients = root.TryGetProperty("recipients", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : [];
        var reasons = recipients
            .Select(r => r.TryGetProperty("delay_reason", out var reason) ? reason.GetString() : null)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct()
            .ToList();
        var queue = String(root, "queue_name");
        return new MailQueueItemDto
        {
            Id = String(root, "queue_id"),
            Sender = String(root, "sender"),
            Recipient = string.Join(", ", recipients.Select(r => String(r, "address")).Where(a => a.Length > 0)),
            SizeBytes = root.TryGetProperty("message_size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            ArrivalTime = root.TryGetProperty("arrival_time", out var arrival) && arrival.TryGetInt64(out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                : default,
            Status = reasons.Count == 0 ? queue : $"{queue}: {string.Join(" | ", reasons)}"
        };
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> Lines(string? output) =>
        (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string[]> TabFields(string? output, string prefix) =>
        (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(fields => fields[0].Trim() == prefix);
}
