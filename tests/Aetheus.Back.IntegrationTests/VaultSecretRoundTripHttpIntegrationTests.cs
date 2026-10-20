// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-002 lot 10, gap 3: a vault secret written over HTTP comes back intact for the right holder, is
/// stored encrypted in PostgreSQL, never appears in a list, detail, version or audit payload, and stays
/// out of reach of a holder of the read right only. The clear value is served by
/// <c>POST /api/vaults/{id}/secrets/{secretId}/reveal</c>, which by design requires Write on the vault
/// (the right to change the value), never Read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VaultSecretRoundTripHttpIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SecretRoundTrip_IsEncryptedAtRest_MaskedInLists_AndRefusedToAReader()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var plaintext = $"s3cr3t-{Guid.NewGuid():N}";
        var key = $"DB_PASSWORD_{Guid.NewGuid():N}"[..24].ToUpperInvariant();

        int projectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organizationId = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).FirstAsync(ct);
            var project = new Project { Name = $"vault-{Guid.NewGuid():N}", OrganizationId = organizationId };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            projectId = project.Id;
        }

        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var created = await admin.PostAsJsonAsync("/api/vaults",
            new CreateVaultRequest { Name = $"vault-{Guid.NewGuid():N}", Description = "round trip", ProjectId = projectId },
            IntegrationJsonOptions.Default, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var vault = await created.Content.ReadFromJsonAsync<VaultDto>(IntegrationJsonOptions.Default, ct);

        var writer = await IntegrationUsers.CreateAsync(factory.Services, "vault-writer",
            [new IntegrationUsers.Grant(ResourceType.Vault, vault!.Id, Permission.Write)]);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "vault-reader",
            [new IntegrationUsers.Grant(ResourceType.Vault, vault.Id, Permission.Read)]);
        using var writerClient = await IntegrationUsers.LoginClientAsync(factory, writer.Username);
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);

        // Write over HTTP as a non-admin holder of Write on this vault only.
        var write = await writerClient.PostAsJsonAsync($"/api/vaults/{vault.Id}/secrets",
            new CreateVaultSecretRequest { Key = key, Value = plaintext }, IntegrationJsonOptions.Default, ct);
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        var writeBody = await write.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(plaintext, writeBody, StringComparison.Ordinal);
        var secret = System.Text.Json.JsonSerializer.Deserialize<VaultSecretDto>(writeBody, IntegrationJsonOptions.Default)!;

        // Read back: the value matches.
        var reveal = await writerClient.PostAsync($"/api/vaults/{vault.Id}/secrets/{secret.Id}/reveal", null, ct);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        Assert.Equal(plaintext, (await reveal.Content.ReadFromJsonAsync<RevealedSecretValueDto>(IntegrationJsonOptions.Default, ct))!.Value);
        Assert.Contains("no-store", reveal.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

        // At rest: the raw column is neither the plaintext nor contains it, and it decrypts to the plaintext.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var raw = await db.Database
                .SqlQuery<string>($"SELECT \"EncryptedValue\" AS \"Value\" FROM \"VaultSecrets\" WHERE \"Id\" = {secret.Id}")
                .SingleAsync(ct);
            Assert.False(string.IsNullOrWhiteSpace(raw));
            Assert.NotEqual(plaintext, raw);
            Assert.DoesNotContain(plaintext, raw, StringComparison.Ordinal);
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
            Assert.Equal(plaintext, encryption.DecryptValue(raw));
        }

        // Lists, detail, versions and key export never carry the value, for the writer nor the reader.
        foreach (var client in new[] { writerClient, readerClient })
        {
            foreach (var url in new[]
                     {
                         $"/api/vaults?projectId={projectId}",
                         $"/api/vaults/{vault.Id}",
                         $"/api/vaults/{vault.Id}/secrets/{secret.Id}/versions",
                         $"/api/vaults/{vault.Id}/export-keys"
                     })
            {
                var response = await client.GetAsync(url, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(ct);
                Assert.DoesNotContain(plaintext, body, StringComparison.Ordinal);
            }
        }
        var detail = await readerClient.GetFromJsonAsync<VaultDetailDto>($"/api/vaults/{vault.Id}", IntegrationJsonOptions.Default, ct);
        Assert.Equal(key, Assert.Single(detail!.Secrets).Key);

        // The read right does not reveal the value.
        var refused = await readerClient.PostAsync($"/api/vaults/{vault.Id}/secrets/{secret.Id}/reveal", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain(plaintext, await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The reveal is audited for the writer, without the value; the refused attempt reveals nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reveals = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "RevealedSecret" && a.EntityId == secret.Id)
                .ToListAsync(ct);
            Assert.Equal(writer.Username, Assert.Single(reveals).Username);
            var leakingAudit = await db.AuditLogs.AsNoTracking()
                .CountAsync(a => a.Details != null && a.Details.Contains(plaintext), ct);
            Assert.Equal(0, leakingAudit);
        }
    }
}
