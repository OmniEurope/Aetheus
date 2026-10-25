// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Apache;

public enum ApacheAction
{
    Start,
    Stop,
    Restart,
    Reload,
    EnableSite,
    DisableSite,
    EnableModule,
    DisableModule,
    TestConfig,
    /// <summary>Recette R-12: deletes a disabled site file from sites-available.</summary>
    DeleteSite
}
