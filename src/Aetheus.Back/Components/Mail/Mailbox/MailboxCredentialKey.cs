// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Auth;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Who holds which mailbox password: one signed-in Aetheus user, one mailbox of one server.</summary>
public readonly record struct MailboxCredentialKey(string UserId, int ServerId, int AccountId)
{
    /// <summary>
    /// The <see cref="UserId"/> of a principal: its authentication scheme, then its identifier. The scheme is
    /// part of it because an agent principal carries its server id as identifier.
    /// </summary>
    public static string Holder(string? authenticationType, string nameIdentifier) =>
        $"{authenticationType}:{nameIdentifier}";

    /// <summary>
    /// Recette R1-37: true when this key belongs to the Aetheus user <paramref name="userId"/>, whatever scheme
    /// they signed in with. An agent principal, whose identifier is a server id, never matches.
    /// </summary>
    public bool IsHeldBy(int userId)
    {
        var separator = UserId.LastIndexOf(':');
        if (separator < 0) return false;
        if (string.Equals(UserId[..separator], AgentTokenAuthenticationHandler.SchemeName, StringComparison.Ordinal))
            return false;
        return string.Equals(UserId[(separator + 1)..], userId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
