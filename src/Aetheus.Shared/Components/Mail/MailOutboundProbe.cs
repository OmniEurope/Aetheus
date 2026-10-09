// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>
/// Recette R2-080: the outbound SMTP prerequisite of the mail setup assistant. The backend queues an
/// <see cref="OperationKind.MailOutboundProbe"/> towards <see cref="Target"/>; the agent connects on
/// <see cref="Port"/> and prints one <c>CHECK</c> line named <see cref="CheckComponent"/>, read back with
/// <c>MailTaskOutputParser.ParseChecks</c>.
/// </summary>
public static class MailOutboundProbe
{
    /// <summary>A public mail exchanger that always answers on port 25: reaching it proves the hosting
    /// provider does not block outbound SMTP. It is only greeted and left (QUIT), no mail is sent.</summary>
    public const string Target = "gmail-smtp-in.l.google.com";

    public const int Port = 25;

    public const string CheckComponent = "outbound-25";
}
