// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using Aetheus.Back.Components.Git.Events;

namespace Aetheus.Back.Components.Git;

/// <summary>Filters requested updates against receive-pack's report-status response. Missing or
/// malformed status data fails closed: it never produces a pipeline trigger.</summary>
internal static class GitReceivePackStatusParser
{
    internal static IReadOnlyList<GitRefUpdate> GetAcceptedUpdates(
        MemoryStream response, IReadOnlyList<GitRefUpdate> requestedUpdates)
    {
        if (requestedUpdates.Count == 0) return [];

        var acceptedRefs = new HashSet<string>(StringComparer.Ordinal);
        var unpackSucceeded = false;
        CollectAcceptedRefs(response.ToArray(), acceptedRefs, ref unpackSucceeded, depth: 0);
        return unpackSucceeded
            ? requestedUpdates.Where(update => acceptedRefs.Contains(update.Reference)).ToList()
            : [];
    }

    private static void CollectAcceptedRefs(
        ReadOnlySpan<byte> data, HashSet<string> acceptedRefs, ref bool unpackSucceeded, int depth)
    {
        if (depth > 2) return;

        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            if (!int.TryParse(Encoding.ASCII.GetString(data.Slice(offset, 4)),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var packetLength))
                return;
            offset += 4;
            if (packetLength == 0) continue;
            if (packetLength < 4 || offset + packetLength - 4 > data.Length) return;

            var payload = data.Slice(offset, packetLength - 4);
            offset += packetLength - 4;
            if (!payload.IsEmpty && payload[0] is 1 or 2 or 3) payload = payload[1..];

            if (payload.Length >= 4 && IsHexHeader(payload[..4]))
            {
                CollectAcceptedRefs(payload, acceptedRefs, ref unpackSucceeded, depth + 1);
                continue;
            }

            foreach (var line in Encoding.UTF8.GetString(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Equals("unpack ok", StringComparison.Ordinal))
                    unpackSucceeded = true;
                else if (line.StartsWith("ok refs/heads/", StringComparison.Ordinal))
                    acceptedRefs.Add(line[3..].Trim());
            }
        }
    }

    private static bool IsHexHeader(ReadOnlySpan<byte> value)
        => value.Length == 4 && value.ToArray().All(character => char.IsAsciiHexDigit((char)character));
}
