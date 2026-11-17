// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class ApacheVirtualHost
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public int Port { get; set; }
    public string DocumentRoot { get; set; } = string.Empty;
    public string ConfigFile { get; set; } = string.Empty;

    /// <summary>Recette R-14: the account that owns the site file when it is not the agent's own (root, certbot): the
    /// agent cannot delete that file. Empty when the agent owns it, and for an agent that does not report it.</summary>
    public string ConfigFileForeignOwner { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
