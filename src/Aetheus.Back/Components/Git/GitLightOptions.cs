// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

public sealed record GitLightOptions
{
    public string RepositoriesPath { get; init; } = "./data/git-repos";
    public int MaintenanceIntervalHours { get; init; } = 24;

    /// <summary>
    /// Host names an external repository may resolve to a private or loopback address for (a forge on
    /// the operator's own network). Every other host must resolve to public addresses only, so a
    /// project writer cannot point a mirror at an internal service (SSRF). Empty by default; set by the
    /// operator, for example <c>GitLight__AllowedInternalHosts__0=gitea.lan</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedInternalHosts { get; init; } = [];
}
