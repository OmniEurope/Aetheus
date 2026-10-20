// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.PortRegistry;

public sealed record PortReservationDto
{
    public int Id { get; init; }
    public int ServerId { get; init; }
    public int Port { get; init; }

    /// <summary><c>tcp</c> or <c>udp</c>. Part of the port's identity: the two stacks bind
    /// independently, so 53/udp and 53/tcp are two different ports.</summary>
    public string Protocol { get; init; } = PortRegistryLimits.TcpProtocol;

    public int? ProjectId { get; init; }

    /// <summary>The linked project's name when the row was read from the list query, which joins it.
    /// Null when no project is linked, and also right after a creation, whose response has not joined
    /// it - the list is reloaded rather than guessing a name the server never sent.</summary>
    public string? ProjectName { get; init; }

    public string OwnerLabel { get; init; } = string.Empty;
    public PortReservationSource Source { get; init; }
    public DateTime DeclaredAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>When the agent last saw this port listening. Null on a declared or manual row that no
    /// scan has confirmed, and on every row until the server has been scanned once.</summary>
    public DateTime? ObservedAt { get; init; }
}

/// <summary>The ports the user wants an answer about, on the server named by the route.</summary>
public sealed record PortCheckRequest
{
    [Required]
    [MaxLength(PortRegistryLimits.MaxPortsPerCheck)]
    public List<int> Ports { get; init; } = [];
}

/// <summary>What the last scan says about one port. Distinct from "free in the registry": the two
/// answers are crossed, never merged, so an operator can see which of them is wrong.</summary>
public enum PortObservationState
{
    /// <summary>The host has never been scanned; "not listening" would be a guess.</summary>
    NeverScanned = 0,
    Listening = 1,
    NotListening = 2
}

public sealed record PortCheckEntryDto
{
    public int Port { get; init; }

    /// <summary>No project or operator claims this port in the registry. Says nothing about whether
    /// something is listening on it - read <see cref="Observation"/> for that.</summary>
    public bool IsFree { get; init; }

    public string? OwnerLabel { get; init; }
    public int? OwnerProjectId { get; init; }
    public PortReservationSource? Source { get; init; }
    public DateTime? DeclaredAt { get; init; }

    /// <summary>What the last scan saw, independently of the registry.</summary>
    public PortObservationState Observation { get; init; }

    /// <summary>Container or process seen holding the port, when one is listening.</summary>
    public string? ObservedHolder { get; init; }

    /// <summary>The project whose port ownership is evaluated, or null for a server-scoped check.
    /// This distinguishes its declared listening ports from ports held by other projects.</summary>
    public int? ContextProjectId { get; init; }

    /// <summary>
    /// One sentence per port, relative to <see cref="ContextProjectId"/>. This replaces the old
    /// <c>IsUsable</c> (free AND not listening), which read "UNUSABLE" for the most ordinary
    /// situation there is: the project's own deployed app answering on its own declared port.
    /// </summary>
    public PortCheckVerdict Verdict =>
        !IsFree && OwnerProjectId is { } owner && ContextProjectId == owner
            ? PortCheckVerdict.UsedByThisProject
            : !IsFree
                ? PortCheckVerdict.TakenByAnotherOwner
                : Observation == PortObservationState.Listening
                    ? PortCheckVerdict.ListeningUndeclared
                    : PortCheckVerdict.Free;
}

/// <summary>One-sentence explanation of the port availability verdict.</summary>
public enum PortCheckVerdict
{
    /// <summary>Declared by the project being checked, listening or not. Nothing to do.</summary>
    UsedByThisProject = 0,

    /// <summary>Claimed by nobody and nothing is listening on it.</summary>
    Free = 1,

    /// <summary>Another project, or an operator, holds this port in the registry.</summary>
    TakenByAnotherOwner = 2,

    /// <summary>Nobody claims it, but the last scan saw something answering on it.</summary>
    ListeningUndeclared = 3
}

public sealed record PortCheckResultDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public List<PortCheckEntryDto> Entries { get; init; } = [];

    /// <summary>When this host's ports were last observed, null when never.</summary>
    public DateTime? LastScanAt { get; init; }

    /// <summary>The agent can scan, so the caller may offer to refresh before trusting the answer.</summary>
    public bool ObservationAvailable { get; init; }
}

/// <summary>
/// One port a caller wants, already claimed on that server by somebody else. Always about TCP, which
/// is what a deployment binds; a UDP sighting is never a conflict, so the protocol is not carried
/// here rather than shipped as a field that could only ever hold one value.
/// </summary>
public sealed record PortConflictDto
{
    public int Port { get; init; }
    public string OwnerLabel { get; init; } = string.Empty;
    public PortReservationSource Source { get; init; }
    public DateTime DeclaredAt { get; init; }
}

public sealed record CreatePortReservationRequest
{
    [Range(1, 65535)]
    public int Port { get; init; }

    [Required]
    [StringLength(PortRegistryLimits.MaxOwnerLabelLength, MinimumLength = 1)]
    public string OwnerLabel { get; init; } = string.Empty;

    public int? ProjectId { get; init; }
}

/// <summary>A TCP or UDP listener observed by the agent and recorded as an <c>Observed</c>
/// reservation. Differences from declared ports do not trigger automatic correction
/// (ADR-045).</summary>
public sealed record ObservedPortDto
{
    [Range(1, 65535)]
    public int Port { get; init; }

    /// <summary><c>tcp</c> or <c>udp</c>, as the host reported it. Both are scanned: a UDP listener
    /// occupies its port just as firmly, and only its own protocol.</summary>
    [Required]
    [StringLength(PortRegistryLimits.MaxProtocolLength, MinimumLength = 1)]
    public string Protocol { get; init; } = "tcp";

    /// <summary>Container name, process name, or the honest fallback when neither is readable.</summary>
    [Required]
    [StringLength(PortRegistryLimits.MaxOwnerLabelLength, MinimumLength = 1)]
    public string Holder { get; init; } = string.Empty;

    /// <summary>The listening address as reported by the host ("0.0.0.0", "127.0.0.1", "[::]"...).
    /// A loopback listener still blocks a container publishing the same port, so it is kept.</summary>
    [Required]
    [StringLength(PortRegistryLimits.MaxInterfaceLength, MinimumLength = 1)]
    public string Interface { get; init; } = string.Empty;
}

/// <summary>
/// What an on-demand scan found, pushed by the agent through its own token. The whole
/// <c>Observed</c> source of that server is replaced by this payload, so a shorter list really means
/// "these ports stopped listening", never "the report was truncated".
/// </summary>
public sealed record ObservedPortsReportDto
{
    [Required]
    [MaxLength(PortRegistryLimits.MaxObservedPorts)]
    public List<ObservedPortDto> Ports { get; init; } = [];

    public DateTime ObservedAt { get; init; }
}

/// <summary>The window of host ports Aetheus may hand out on one server.</summary>
public sealed record PortRangeDto
{
    [Range(PortRegistryLimits.MinAllocatablePort, 65535)]
    public int From { get; init; } = PortRegistryLimits.DefaultRangeFrom;

    [Range(PortRegistryLimits.MinAllocatablePort, 65535)]
    public int To { get; init; } = PortRegistryLimits.DefaultRangeTo;

    /// <summary>False when no row exists and the fleet default is being reported.</summary>
    public bool IsExplicit { get; init; }
}

/// <summary>How many free ports a caller wants, and who will hold them.</summary>
public sealed record AllocatePortsRequest
{
    [Range(1, PortRegistryLimits.MaxPortsPerAllocation)]
    public int Count { get; init; } = 1;

    [Required]
    [StringLength(PortRegistryLimits.MaxOwnerLabelLength, MinimumLength = 1)]
    public string OwnerLabel { get; init; } = string.Empty;

    public int? ProjectId { get; init; }
}

/// <summary>One server a library may allocate ports on.</summary>
public sealed record PortAllocationTargetDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
}

/// <summary>
/// Where a library can allocate. <see cref="PreselectedServerId"/> is set only when the library's own
/// scope names exactly one server; otherwise the operator picks, because guessing would silently
/// reserve ports on the wrong host.
/// </summary>
public sealed record PortAllocationTargetsDto
{
    public List<PortAllocationTargetDto> Servers { get; init; } = [];
    public int? PreselectedServerId { get; init; }

    /// <summary>The project the reservations would be recorded under, null when the library has none.</summary>
    public int? ProjectId { get; init; }

    /// <summary>Why allocation is impossible for this library. The reason is a code, not a sentence,
    /// so the UI renders it in the reader's language instead of showing a server-side English string.</summary>
    public PortAllocationBlocker Blocker { get; init; }
}

/// <summary>Why a library cannot allocate ports.</summary>
public enum PortAllocationBlocker
{
    /// <summary>It can.</summary>
    None = 0,

    /// <summary>No project owns it, so a reservation could name no holder a deployment recognises.</summary>
    NoProject = 1,

    /// <summary>Its scope reaches no server, so there is nowhere to allocate.</summary>
    NoServer = 2
}

/// <summary>The keys to create in a library, one free port each.</summary>
public sealed record AllocateLibraryPortsRequest
{
    public int ServerId { get; init; }

    [Required]
    [MaxLength(PortRegistryLimits.MaxPortsPerAllocation)]
    [MaxItemStringLength(100)]
    public List<string> Keys { get; init; } = [];
}

public static class PortRegistryLimits
{
    /// <summary>
    /// The prefix the pipeline preflight guard reads. A port entry that does not carry it is invisible
    /// to the guard, so allocation refuses to create one rather than produce a variable that looks
    /// protected and is not.
    /// </summary>
    public const string PortKeyPrefix = "PORT_";

    /// <summary>
    /// Below 1024 a port needs privileges to bind, so allocation never reaches there. The default
    /// window stops before 20000, which the QA convention already uses.
    /// </summary>
    public const int MinAllocatablePort = 1024;

    public const int DefaultRangeFrom = 10000;
    public const int DefaultRangeTo = 19999;

    /// <summary>A blue-green front/back pair is four ports; the cap only stops an absurd request.</summary>
    public const int MaxPortsPerAllocation = 16;

    public const int MaxPortsPerCheck = 64;
    public const int MaxOwnerLabelLength = 200;
    public const int MaxOwnerKeyLength = 200;
    public const int MaxProtocolLength = 8;

    /// <summary>What every Aetheus deployment binds, and the default of any reservation it writes.</summary>
    public const string TcpProtocol = "tcp";

    /// <summary>Observed only: nothing Aetheus deploys declares a UDP port today.</summary>
    public const string UdpProtocol = "udp";
    public const int MaxInterfaceLength = 64;

    /// <summary>
    /// Upper bound on one observation report. A busy host listens on a few dozen ports; the cap is
    /// what keeps a malformed or hostile agent payload from turning into an unbounded write.
    /// </summary>
    public const int MaxObservedPorts = 512;
}
