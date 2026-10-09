// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Git;

/// <summary>Copies a receive-pack request unchanged while extracting its requested branch updates from
/// the pkt-line command prelude. The pack payload is streamed directly afterwards and is never
/// buffered in memory.</summary>
internal static class GitReceivePackRefParser
{
    private const int HeaderLength = 4;
    private const int MaxPacketLength = 65520;
    // Room for thousands of ref commands; a prelude larger than this is not a push anyone makes.
    private const int MaxPreludeBytes = 1024 * 1024;

    /// <summary>
    /// Reads the command prelude of a receive-pack request (its pkt-lines up to and including the
    /// flush packet, or up to the first malformed packet) and leaves the pack in <paramref name="source"/>.
    /// The bytes are returned unchanged, so they can be forwarded to git ahead of the rest of the body
    /// once the commands have been checked.
    /// </summary>
    public static async Task<byte[]> ReadCommandPreludeAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var prelude = new MemoryStream();
        while (true)
        {
            var header = new byte[HeaderLength];
            var headerRead = await ReadAvailableAsync(source, header, ct).ConfigureAwait(false);
            prelude.Write(header, 0, headerRead);
            if (headerRead != HeaderLength || !TryGetPacketLength(header, out var packetLength))
                return prelude.ToArray();

            var payload = new byte[packetLength - HeaderLength];
            var payloadRead = await ReadAvailableAsync(source, payload, ct).ConfigureAwait(false);
            prelude.Write(payload, 0, payloadRead);
            if (prelude.Length > MaxPreludeBytes)
                throw new BadHttpRequestException(
                    "The Git push command list exceeds the permitted size.", StatusCodes.Status413PayloadTooLarge);
            if (payloadRead != payload.Length)
                return prelude.ToArray();
        }
    }

    /// <summary>Every ref command of a prelude read by <see cref="ReadCommandPreludeAsync"/>, creations
    /// and deletions included (an all-zero object id on the old or the new side).</summary>
    public static IReadOnlyList<GitRefUpdate> ParseCommands(byte[] prelude)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        var commands = new List<GitRefUpdate>();
        var offset = 0;
        while (offset + HeaderLength <= prelude.Length)
        {
            if (!TryGetPacketLength(prelude[offset..(offset + HeaderLength)], out var packetLength)
                || offset + packetLength > prelude.Length)
                break;
            var command = Encoding.UTF8.GetString(prelude, offset + HeaderLength, packetLength - HeaderLength)
                .Split('\0')[0].TrimEnd('\n');
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && IsObjectId(parts[0]) && IsObjectId(parts[1]))
                commands.Add(new GitRefUpdate(parts[2], parts[1], parts[0]));
            offset += packetLength;
        }
        return commands;
    }

    internal static bool IsDeletion(GitRefUpdate command) => IsZeroObjectId(command.NewObjectId);

    public static async Task<IReadOnlyList<GitRefUpdate>> CopyAndExtractUpdatedRefsAsync(
        Stream source, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var updatedRefs = new List<GitRefUpdate>();
        while (true)
        {
            var header = new byte[HeaderLength];
            var headerRead = await ReadAvailableAsync(source, header, ct).ConfigureAwait(false);
            if (headerRead == 0) return updatedRefs;

            await destination.WriteAsync(header.AsMemory(0, headerRead), ct).ConfigureAwait(false);
            if (headerRead != HeaderLength || !TryGetPacketLength(header, out var packetLength))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                return updatedRefs;
            }

            if (packetLength == 0)
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                return updatedRefs;
            }

            var payload = new byte[packetLength - HeaderLength];
            var payloadRead = await ReadAvailableAsync(source, payload, ct).ConfigureAwait(false);
            await destination.WriteAsync(payload.AsMemory(0, payloadRead), ct).ConfigureAwait(false);
            if (payloadRead != payload.Length)
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                return updatedRefs;
            }

            AddUpdatedBranchRef(payload, updatedRefs);
        }
    }

    private static async Task<int> ReadAvailableAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static bool TryGetPacketLength(byte[] header, out int packetLength)
    {
        packetLength = 0;
        var headerText = Encoding.ASCII.GetString(header);
        if (!int.TryParse(headerText, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out packetLength)
            || packetLength < HeaderLength || packetLength > MaxPacketLength)
        {
            return false;
        }
        return true;
    }

    private static void AddUpdatedBranchRef(byte[] payload, List<GitRefUpdate> updatedRefs)
    {
        var command = Encoding.UTF8.GetString(payload).Split('\0')[0].TrimEnd('\n');
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0].Equals(parts[1], StringComparison.Ordinal)
            || !IsObjectId(parts[0]) || !IsObjectId(parts[1]) || IsZeroObjectId(parts[1])
            || !IsSafeBranchRef(parts[2]))
        {
            return;
        }

        updatedRefs.Add(new GitRefUpdate(parts[2], parts[1], parts[0]));
    }

    private static bool IsObjectId(string value)
        => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsZeroObjectId(string value)
        => value.All(character => character == '0');

    private static bool IsSafeBranchRef(string value)
        => value.StartsWith("refs/heads/", StringComparison.Ordinal)
            && value.Length > "refs/heads/".Length
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '/' or '.' or '_' or '-');
}
