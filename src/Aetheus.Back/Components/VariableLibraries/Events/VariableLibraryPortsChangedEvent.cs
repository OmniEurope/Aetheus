// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.VariableLibraries.Events;

/// <summary>The owner triple of a library, as it was or will be (exactly one is set, or none for a global library).</summary>
public sealed record LibraryPortScope(int? ProjectId, int? EnvironmentId, int? ProjectServerId);

/// <summary>A <c>PORT_*</c> entry whose value is a valid port number.</summary>
public sealed record LibraryPortEntry(string Key, int Port)
{
    /// <summary>The port a <c>PORT_*</c> entry holds, when it holds a valid one. An empty or malformed value claims nothing.</summary>
    public static bool TryRead(string? key, string? value, out LibraryPortEntry entry)
    {
        entry = null!;
        if (key is null
            || !key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal)
            || !int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            return false;
        }

        entry = new LibraryPortEntry(key, port);
        return true;
    }
}

/// <summary>
/// The ports a library's <c>PORT_*</c> entries claim changed (an entry written, changed or deleted, entries
/// imported, the library deleted or moved to another owner). Announced rather than written here: the port
/// registry lives above this module, so the side that owns "a port belongs to somebody" listens from
/// above, and it is dispatched strictly so a port another project already holds refuses the write instead
/// of leaving a library naming a port the registry says is taken (ADR-039).
/// </summary>
public sealed record VariableLibraryPortsChangedEvent(
    int LibraryId,
    string LibraryName,
    LibraryPortScope OldScope,
    IReadOnlyList<LibraryPortEntry> OldEntries,
    LibraryPortScope NewScope,
    IReadOnlyList<LibraryPortEntry> NewEntries) : IDomainEvent;
