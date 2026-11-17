// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R3-7 against real PostgreSQL: the nightly selection query over the migrated model list columns,
/// and the admin-only access of the "read now" endpoint (the rest of the feature is covered by unit tests).
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class AiModelListIntegrationTests(ApiSmokeFixture fixture)
{
    private const string FetchedJson = """["claude-haiku-5","claude-opus-5-5"]""";
    private static readonly DateTime FetchedAt = new(2026, 10, 12, 1, 5, 0, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2100, 1, 1, 1, 30, 0, DateTimeKind.Utc);
    // Refused by the HTTP stack (SSRF guard or closed port): a real failure, never a real provider.
    private const string Unreachable = "https://127.0.0.1:9";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R3_7_DueQuery_KeepsToApiProfilesWithAKey_NotReadSinceTheCutoff()
    {
        // Far in the future, so the copy of the service running in this host, on the real clock, never
        // touches what these tests seed: every seeded row points at a loopback URL and is checked after it.
        var now = Future;
        var due = await SeedAsync("anthropic", checkedAt: null);
        var dueAgain = await SeedAsync("openai", checkedAt: now.AddHours(-13));
        var readTonight = await SeedAsync("anthropic", checkedAt: now.AddHours(-1));
        var cliProfile = await SeedAsync(string.Empty, checkedAt: null);
        var withoutKey = await SeedAsync("anthropic", checkedAt: null, withKey: false);

        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiTaskRepository>();
        var ids = (await repo.GetProfilesDueForModelListAsync(now.AddHours(-12), Ct)).Select(profile => profile.Id).ToHashSet();

        Assert.Contains(due, ids);
        Assert.Contains(dueAgain, ids);
        Assert.DoesNotContain(readTonight, ids);
        Assert.DoesNotContain(cliProfile, ids);
        Assert.DoesNotContain(withoutKey, ids);
    }

    [Fact]
    public async Task R3_7_ReadNow_IsRefusedToANonAdmin_AndUnknownProfileIs404()
    {
        var id = await SeedAsync("anthropic", checkedAt: null);
        using var reader = await fixture.CreateReaderClientAsync();
        using var admin = fixture.CreateAdminClient();

        using (var forbidden = await reader.PostAsync($"/api/ai/profiles/{id}/model-list/refresh", null, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null((await ReadAsync(id)).ModelListCheckedAt);
        using (var missing = await admin.PostAsync("/api/ai/profiles/987654/model-list/refresh", null, Ct))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<AiRunnerProfile> ReadAsync(int id)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AiRunnerProfiles.AsNoTracking()
            .FirstAsync(profile => profile.Id == id, Ct);
    }

    private async Task<int> SeedAsync(string provider, DateTime? checkedAt, bool withKey = true, string baseUrl = Unreachable)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var organization = await db.Organizations.FirstAsync(o => o.Slug == "aetheus", Ct);
        var profile = new AiRunnerProfile
        {
            OrganizationId = organization.Id,
            Name = $"r37-{Guid.NewGuid():N}",
            Provider = provider,
            BaseUrl = baseUrl,
            Model = "claude-sonnet-5-5",
            ApiKeyEncrypted = withKey ? encryption.EncryptValue("sk-integration-0123456789") : string.Empty,
            EnvironmentJsonEncrypted = encryption.EncryptValue("{}"),
            MaxOutputTokens = 16_000,
            AvailableModelsJson = FetchedJson,
            AvailableModelsFetchedAt = FetchedAt,
            ModelListCheckedAt = checkedAt
        };
        db.AiRunnerProfiles.Add(profile);
        await db.SaveChangesAsync(Ct);
        return profile.Id;
    }
}
