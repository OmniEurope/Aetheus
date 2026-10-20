// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R3-7 against real PostgreSQL: the migrated model list columns, the nightly selection query,
/// a real failed read recorded by the registered reader and by the "read now" endpoint (a loopback
/// base URL the HTTP stack refuses, no fake handler and no call to a real provider), its one-minute
/// limit and admin-only access, the nightly pass switched off in this test host, and the list exposed
/// and kept or dropped through the AI profile API.
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
    public async Task R3_7_AFailedRead_IsStoredInPostgres_KeepsTheList_AndTheApiShowsBoth()
    {
        var id = await SeedAsync("anthropic", checkedAt: Future);
        var reader = fixture.Factory.Services.GetRequiredService<AiModelListReader>();
        var now = Future.AddHours(1);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAiTaskRepository>();
            var profile = (await repo.FindProfileAsync(id, Ct))!;
            var read = await reader.ReadIntoAsync(
                profile, AiProvider.Anthropic, scope.ServiceProvider.GetRequiredService<IEncryptionService>(), now, Ct);
            await repo.SaveChangesAsync(Ct);
            Assert.False(read);
        }

        using var client = fixture.CreateAdminClient();
        var dto = await client.GetFromJsonAsync<AiRunnerProfileDto>($"/api/ai/profiles/{id}", IntegrationJsonOptions.Default, Ct);
        Assert.NotNull(dto);
        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], dto.AvailableModels);
        Assert.Equal(FetchedAt, dto.AvailableModelsFetchedAt!.Value.ToUniversalTime());
        Assert.Equal(now, dto.ModelListCheckedAt!.Value.ToUniversalTime());
        Assert.False(string.IsNullOrWhiteSpace(dto.ModelListError));
        Assert.DoesNotContain("sk-integration", dto.ModelListError, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_7_TheIntegrationHost_DoesNotRunTheNightlyPass_ButKeepsTheReader()
    {
        Assert.Empty(fixture.Factory.Services.GetServices<IHostedService>().OfType<AiModelListRefreshService>());
        Assert.NotNull(fixture.Factory.Services.GetService<AiModelListReader>());
    }

    [Fact]
    public async Task R3_7_ReadNow_ReadsAtTheProviderThroughTheApi_StoresTheOutcome_ThenRefusesWithinAMinute()
    {
        // checkedAt null: never read, so the first call reaches the provider (a loopback URL the HTTP
        // stack refuses: a real failure, recorded as the nightly pass would).
        var id = await SeedAsync("anthropic", checkedAt: null);
        using var client = fixture.CreateAdminClient();

        using (var first = await client.PostAsync($"/api/ai/profiles/{id}/model-list/refresh", null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var dto = await first.Content.ReadFromJsonAsync<AiRunnerProfileDto>(IntegrationJsonOptions.Default, Ct);
            Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], dto!.AvailableModels);
            Assert.False(string.IsNullOrWhiteSpace(dto.ModelListError));
            Assert.DoesNotContain("sk-integration", dto.ModelListError, StringComparison.Ordinal);
        }
        var stored = await ReadAsync(id);
        Assert.NotNull(stored.ModelListCheckedAt);
        Assert.Equal(FetchedJson, stored.AvailableModelsJson);
        Assert.False(string.IsNullOrWhiteSpace(stored.ModelListError));

        using (var second = await client.PostAsync($"/api/ai/profiles/{id}/model-list/refresh", null, Ct))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.True(second.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 and <= 60 });
            Assert.Contains("less than a minute ago", await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        Assert.Equal(stored.ModelListCheckedAt, (await ReadAsync(id)).ModelListCheckedAt);
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

    [Fact]
    public async Task R3_7_SavingTheSameTarget_KeepsTheList_AnotherProviderDropsIt()
    {
        var id = await SeedAsync("anthropic", checkedAt: Future);
        using var client = fixture.CreateAdminClient();

        using (var same = await client.PutAsJsonAsync($"/api/ai/profiles/{id}", Update(AiProvider.Anthropic, "claude-opus-5-5"), Ct))
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(FetchedJson, (await ReadAsync(id)).AvailableModelsJson);

        using (var other = await client.PutAsJsonAsync($"/api/ai/profiles/{id}", Update(AiProvider.OpenAi, "gpt-5.5"), Ct))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var dropped = await ReadAsync(id);
        Assert.Null(dropped.AvailableModelsJson);
        Assert.Null(dropped.AvailableModelsFetchedAt);
        Assert.Null(dropped.ModelListCheckedAt);
        var dto = await client.GetFromJsonAsync<AiRunnerProfileDto>($"/api/ai/profiles/{id}", IntegrationJsonOptions.Default, Ct);
        Assert.Empty(dto!.AvailableModels);
    }

    private static UpdateAiRunnerProfileRequest Update(AiProvider provider, string model) => new()
    {
        Name = $"r37-{Guid.NewGuid():N}"[..20],
        Provider = provider,
        Model = model,
        BaseUrl = Unreachable,
        ReasoningEffort = AiReasoningEffort.Low,
        MaxOutputTokens = 16_000,
        TimeoutSeconds = 600,
        MaxOutputBytes = 200_000
    };

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
