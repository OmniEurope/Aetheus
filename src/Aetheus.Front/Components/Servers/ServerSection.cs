// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers;

/// <summary>
/// Centralised section-key constants used by <c>ServerDetail</c> sidebar navigation
/// and <c>@if (_activeSection == ...)</c> guards. Keeping them as constants prevents
/// typos while preserving the URL-slug shape required by the <c>Section</c> route param.
/// </summary>
public static class ServerSection
{
    public const string Overview = "overview";
    public const string Projects = "projects";
    public const string Pipelines = "pipelines";
    public const string Libraries = "libraries";
    public const string Vaults = "vaults";
    public const string Releases = "releases";
    public const string Docker = "docker";
    public const string Apache = "apache";
    public const string Certbot = "certbot";
    public const string Cron = "cron";
    public const string Mail = "mail";
    public const string Teamspeak = "teamspeak";
    public const string Portsentry = "portsentry";

    /// <summary>The port registry, separate from the Portsentry intrusion module.</summary>
    public const string Ports = "ports";

    public const string Rkhunter = "rkhunter";
    public const string Updates = "updates";
    public const string Firewall = "firewall";
    public const string Modules = "modules";
    public const string Apps = "apps";
    public const string Services = "services";
    public const string Tasks = "tasks";
    public const string Logs = "logs";
    public const string Configuration = "configuration";
    public const string Properties = "properties";
}
