// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>Pure parsing of the outbound delivery facts the mail page shows: the deferred entries of
/// <c>postqueue -j</c> (or, on an old Postfix, <c>postqueue -p</c>) with their deferral reason, bounded in count
/// and size, and the global IPv6 addresses of the host.</summary>
internal static partial class MailDeliveryParser
{
    // Bounds the work on a huge queue: the entries kept are capped, and so is what is scanned.
    private const int MaxScannedLines = 5000;

    /// <summary>Deferred recipients of <c>postqueue -j</c> output (one JSON object per line); non-JSON lines
    /// (for example "Mail queue is empty") are skipped.</summary>
    public static List<MailDeferredMessageDto> ParseQueueJson(string output)
    {
        var result = new List<MailDeferredMessageDto>();
        var scanned = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (++scanned > MaxScannedLines || result.Count >= MailInventoryLimits.MaxDeferredMessages) break;
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                AddDeferredRecipients(doc.RootElement, result);
            }
            catch (JsonException)
            {
                // One unreadable line must not hide the others.
            }
        }
        return result;
    }

    private static void AddDeferredRecipients(JsonElement entry, List<MailDeferredMessageDto> result)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("queue_name", out var queue) || queue.GetString() != "deferred"
            || !entry.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array)
            return;
        var id = Truncate(StringOf(entry, "queue_id"), 32);
        var arrival = entry.TryGetProperty("arrival_time", out var time) && time.TryGetInt64(out var epoch) && epoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
            : (DateTime?)null;
        foreach (var recipient in recipients.EnumerateArray())
        {
            if (result.Count >= MailInventoryLimits.MaxDeferredMessages) return;
            result.Add(new MailDeferredMessageDto
            {
                QueueId = id,
                Recipient = Truncate(StringOf(recipient, "address"), 254),
                Reason = Truncate(StringOf(recipient, "delay_reason"), MailInventoryLimits.MaxDeferralReasonLength),
                ArrivalTime = arrival
            });
        }
    }

    private static string StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Deferred recipients of the human <c>postqueue -p</c> listing: an id line (no <c>*</c> active or
    /// <c>!</c> held mark), the parenthesised deferral reason, then one line per recipient.</summary>
    public static List<MailDeferredMessageDto> ParseQueueText(string output)
    {
        var result = new List<MailDeferredMessageDto>();
        string? id = null;
        var deferred = false;
        var reason = new List<string>();
        var scanned = 0;
        foreach (var raw in output.Split('\n'))
        {
            if (++scanned > MaxScannedLines || result.Count >= MailInventoryLimits.MaxDeferredMessages) break;
            var line = raw.TrimEnd('\r');
            var header = QueueHeaderRegex().Match(line);
            if (header.Success)
            {
                id = header.Groups[1].Value;
                deferred = header.Groups[2].Length == 0;
                reason.Clear();
            }
            else if (id is not null && deferred && line.TrimStart().StartsWith('('))
            {
                reason.Add(line.Trim().TrimStart('(').TrimEnd(')'));
            }
            else if (id is not null && deferred && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim() is { Length: > 0 } recipient
                && !recipient.StartsWith("--", StringComparison.Ordinal))
            {
                result.Add(new MailDeferredMessageDto
                {
                    QueueId = Truncate(id, 32),
                    Recipient = Truncate(recipient, 254),
                    Reason = Truncate(string.Join(' ', reason), MailInventoryLimits.MaxDeferralReasonLength)
                });
            }
        }
        return result;
    }

    /// <summary>Global-scope IPv6 addresses, in order of appearance and without duplicates. Loopback,
    /// link-local (fe80::/10), unique-local (fc00::/7), multicast and unspecified addresses are not global.</summary>
    public static List<string> GlobalIpv6(IEnumerable<IPAddress> addresses)
    {
        var result = new List<string>();
        foreach (var address in addresses)
        {
            if (result.Count >= MailInventoryLimits.MaxHostIpv6Addresses) break;
            if (address.AddressFamily != AddressFamily.InterNetworkV6
                || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6None)
                || address.IsIPv4MappedToIPv6)
                continue;
            var text = new IPAddress(address.GetAddressBytes()).ToString();
            if (!result.Contains(text, StringComparer.Ordinal)) result.Add(text);
        }
        return result;
    }

    /// <summary>"-Queue ID-  --Size-- ----Arrival Time---- -Sender/Recipient-------" data row:
    /// "4F1A2B3C4D*   1234 Tue Oct 20 10:00:00  sender@example.com".</summary>
    [GeneratedRegex(@"^([0-9A-Za-z]{6,32})([*!]?)\s+\d+\s+\w{3}\s+\w{3}\s+\d+\s+\d{2}:\d{2}:\d{2}\s+\S")]
    private static partial Regex QueueHeaderRegex();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
