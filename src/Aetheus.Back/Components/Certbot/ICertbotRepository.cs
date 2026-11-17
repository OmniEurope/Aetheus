// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Certbot;

public interface ICertbotRepository
{
    Task<List<CertbotCertificate>> GetCertificatesAsync(int serverId, CancellationToken ct = default);
    /// <summary>Recette R-19: the names of the certificates the last renewal test reported as failed (empty when none).</summary>
    Task<List<string>> GetRenewalCheckFailedNamesAsync(int serverId, CancellationToken ct = default);
    /// <summary>Recette R-19: the version of the certbot helper the agent last reported (0 when absent or unknown).</summary>
    Task<int> GetHelperVersionAsync(int serverId, CancellationToken ct = default);
    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default);
    Task AddTaskAsync(ServerTask task, CancellationToken ct = default);
}
