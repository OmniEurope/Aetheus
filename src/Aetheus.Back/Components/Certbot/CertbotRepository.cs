// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Certbot;

public class CertbotRepository(AppDbContext db) : ICertbotRepository
{
    public async Task<List<CertbotCertificate>> GetCertificatesAsync(int serverId, CancellationToken ct = default)
    {
        return await db.CertbotCertificates
            .AsNoTracking()
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetRenewalCheckFailedNamesAsync(int serverId, CancellationToken ct = default)
    {
        var json = await db.CertbotStates
            .AsNoTracking()
            .Where(s => s.ServerId == serverId)
            .Select(s => s.RenewalCheckFailuresJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<CertbotRenewalFailureDto>>(json) ?? [])
                .Select(failure => failure.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<int> GetHelperVersionAsync(int serverId, CancellationToken ct = default) =>
        await db.CertbotStates
            .AsNoTracking()
            .Where(s => s.ServerId == serverId)
            .Select(s => s.HelperVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public Task<bool> ServerExistsAsync(int serverId, CancellationToken ct = default) =>
        ServerTaskRepositoryOperations.ServerExistsAsync(db, serverId, ct);

    public Task AddTaskAsync(ServerTask task, CancellationToken ct = default) =>
        ServerTaskRepositoryOperations.AddTaskAsync(db, task, ct);
}
