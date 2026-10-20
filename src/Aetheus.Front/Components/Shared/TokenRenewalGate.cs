// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The one token renewal of a browser tab, shared by every <see cref="AuthDelegatingHandler"/> built on the
/// same <see cref="AuthStateProvider"/>. The handler is transient: the API client, the error reporter and
/// each realtime hub (whose negotiate runs through its own handler) hold separate instances, so a
/// single-flight kept on the handler let four hubs coming back from one outage send four refreshes of the
/// same token at once (recette R1-89). Kept on the tab's auth state, every caller of the tab waits on the
/// same renewal and the realtime connections reconnect after it.
/// </summary>
internal sealed class TokenRenewalGate
{
    /// <summary>Guards <see cref="InFlight"/>.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>The renewal under way, null when none is.</summary>
    public Task<TokenRenewalOutcome>? InFlight { get; set; }

    /// <summary>Why the last definitive rejection happened (a <c>SessionEndReasons</c> code), read by
    /// every caller that waited on that renewal, not only the one that sent it.</summary>
    public string? LastRejectionReason { get; set; }
}
