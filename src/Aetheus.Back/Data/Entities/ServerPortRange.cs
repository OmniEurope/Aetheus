// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>Bounds host-port allocation to avoid privileged ports and ranges reserved for other
/// uses. A server needs a row only when it overrides the default range.</summary>
public class ServerPortRange
{
    public int Id { get; set; }
    public int ServerId { get; set; }

    /// <summary>First port of the window, inclusive.</summary>
    public int From { get; set; }

    /// <summary>Last port of the window, inclusive.</summary>
    public int To { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
