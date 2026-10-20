// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Certbot;

/// <summary>Compares certificate renewal with the ACME web-root convention in
/// <c>/etc/letsencrypt/cli.ini</c>. Defaults to <see cref="Unknown"/> when the renewal information
/// is absent or unreadable.</summary>
public enum CertbotRenewalConvention
{
    Unknown,
    Conforming,
    NotWebroot,
    WrongWebroot
}
