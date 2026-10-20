// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One port claimed on one server by one holder. The registry that answers "is 10041 free on
/// vps2577917, and if not, who has it?" - a question no part of Aetheus could answer before, which is
/// how a nightly deployment collided six times with another project's live front-end on port 10031.
///
/// <see cref="OwnerKey"/> is the identity the conflict rule compares, not <see cref="OwnerLabel"/>:
/// the label is what a human reads and may be renamed, while the key stays stable so a redeployment
/// of the same project keeps its own ports.
/// </summary>
public class ServerPortReservation
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public int Port { get; set; }

    /// <summary>
    /// <c>tcp</c> or <c>udp</c>. Part of the port's identity, not a label: the two stacks bind
    /// independently, so 53/udp and 53/tcp are different ports and a listener on one must never refuse
    /// a deployment on the other. Everything Aetheus declares is TCP - that is what a deploy step binds
    /// - so the column exists for what the agent OBSERVES.
    /// </summary>
    [MaxLength(Aetheus.Shared.Components.PortRegistry.PortRegistryLimits.MaxProtocolLength)]
    public string Protocol { get; set; } = Aetheus.Shared.Components.PortRegistry.PortRegistryLimits.TcpProtocol;

    /// <summary>Stable holder identity ("project:12", "pipeline:aetheus-nightly").</summary>
    [MaxLength(Aetheus.Shared.Components.PortRegistry.PortRegistryLimits.MaxOwnerKeyLength)]
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>Human-readable holder, shown in the conflict message and in the UI.</summary>
    [MaxLength(Aetheus.Shared.Components.PortRegistry.PortRegistryLimits.MaxOwnerLabelLength)]
    public string OwnerLabel { get; set; } = string.Empty;

    public int? ProjectId { get; set; }
    public Aetheus.Shared.Components.PortRegistry.PortReservationSource Source { get; set; }
    public DateTime DeclaredAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Records the last agent observation of this listening port; null means no scan has
    /// confirmed the claim. Missing observations remain visible discrepancies rather than
    /// triggering automatic corrections.</summary>
    public DateTime? ObservedAt { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
    public Project? Project { get; set; }
}
