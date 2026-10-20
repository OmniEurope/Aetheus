// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers;

/// <summary>Result of one TCP connect attempt made by the external probe.</summary>
public enum TcpProbeOutcome
{
    /// <summary>The connection was accepted: something listens on the port.</summary>
    Open,

    /// <summary>The connection was actively refused: the host answered, the port is closed.</summary>
    Refused,

    /// <summary>No answer within the timeout, or no route to the host.</summary>
    NoAnswer
}
