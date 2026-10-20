// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>The start of the names the backend gives to mail tasks the pages find again by name.</summary>
public static class MailTaskNames
{
    /// <summary>Recette R3-46: the full setup task; the assistant reattaches to an open one when the page is reopened.</summary>
    public const string SetupPrefix = "Mail - full setup";

    /// <summary>Recette R3-23: the read-only check of what a mail stack left on the host.</summary>
    public const string LeftoversCheck = "Mail - leftovers check";
}
