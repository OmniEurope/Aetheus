// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Settings;

/// <summary>
/// How the connection to an SMTP server is secured. Stored by name in the platform settings, so a
/// value may be added but never renamed.
/// </summary>
public enum SmtpTlsMode
{
    /// <summary>Plain connection upgraded with STARTTLS, which the server must offer (port 587).</summary>
    StartTls = 0,

    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect = 1,

    /// <summary>No encryption at all; only for a relay on a trusted network.</summary>
    None = 2
}
