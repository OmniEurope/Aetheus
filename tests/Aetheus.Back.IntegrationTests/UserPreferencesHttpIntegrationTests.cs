// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-66: the per-user preferences over the real HTTP pipeline and PostgreSQL. A value written by
/// one user round-trips with its date and tombstone, an older write never replaces a later one, and a
/// second user can neither read nor change the first user's preferences under the same key.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserPreferencesHttpIntegrationTests(PostgresFixture fixture)
{
    private const string Root = "/api/users/me/preferences";
    private const string Key = "aetheus.appearance";

    [Fact]
    public async Task Preference_RoundTripsWithItsDate_KeepsTheLaterWrite_AndLeavesATombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var user = await IntegrationUsers.CreateAsync(factory.Services, "prefs-roundtrip", []);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        // In the past (a future date is brought back to the server's now) and on a whole second
        // (PostgreSQL keeps microseconds).
        var now = DateTimeOffset.UtcNow;
        var written = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddMinutes(-10);
        var saved = await PutAsync(client, Key, """{"mode":"light"}""", written);
        Assert.Equal("""{"mode":"light"}""", saved.Value);
        Assert.Equal(written, saved.UpdatedAt);

        var read = await GetAsync(client, Key);
        Assert.NotNull(read);
        Assert.Equal("""{"mode":"light"}""", read.Value);
        Assert.Equal(written, read.UpdatedAt);

        // The server wins: a write dated before the stored one is ignored and the stored value returned.
        var stale = await PutAsync(client, Key, """{"mode":"dark"}""", written.AddMinutes(-5));
        Assert.Equal("""{"mode":"light"}""", stale.Value);
        Assert.Equal("""{"mode":"light"}""", (await GetAsync(client, Key))!.Value);

        var removed = await client.DeleteAsync($"{Root}/{Key}", ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var tombstone = await GetAsync(client, Key);
        Assert.NotNull(tombstone);
        Assert.Null(tombstone.Value);
        Assert.True(tombstone.UpdatedAt > written);

        var changed = await client.GetFromJsonAsync<List<UserPreferenceDto>>(
            $"{Root}?since={Uri.EscapeDataString(written.AddSeconds(1).ToString("O"))}", IntegrationJsonOptions.Default, ct);
        var only = Assert.Single(changed!);
        Assert.Equal(Key, only.Key);
        Assert.Null(only.Value);
    }

    [Fact]
    public async Task SecondUser_NeitherReadsNorChanges_TheFirstUsersPreference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var userA = await IntegrationUsers.CreateAsync(factory.Services, "prefs-a", []);
        var userB = await IntegrationUsers.CreateAsync(factory.Services, "prefs-b", []);
        using var clientA = await IntegrationUsers.LoginClientAsync(factory, userA.Username);
        using var clientB = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        await PutAsync(clientA, Key, """{"mode":"light"}""", DateTimeOffset.UtcNow.AddMinutes(-1));

        var readByB = await clientB.GetAsync($"{Root}/{Key}", ct);
        Assert.Equal(HttpStatusCode.NoContent, readByB.StatusCode);
        var listedByB = await clientB.GetFromJsonAsync<List<UserPreferenceDto>>(Root, IntegrationJsonOptions.Default, ct);
        Assert.DoesNotContain(listedByB!, preference => preference.Key == Key);

        // B writing then removing the same key works on B's own row only.
        var savedByB = await PutAsync(clientB, Key, """{"mode":"dark"}""", DateTimeOffset.UtcNow);
        Assert.Equal("""{"mode":"dark"}""", savedByB.Value);
        Assert.Equal(HttpStatusCode.NoContent, (await clientB.DeleteAsync($"{Root}/{Key}", ct)).StatusCode);

        var stillA = await GetAsync(clientA, Key);
        Assert.NotNull(stillA);
        Assert.Equal("""{"mode":"light"}""", stillA.Value);
        var listedByA = await clientA.GetFromJsonAsync<List<UserPreferenceDto>>(Root, IntegrationJsonOptions.Default, ct);
        Assert.Equal("""{"mode":"light"}""", Assert.Single(listedByA!).Value);
    }

    [Theory]
    [InlineData("-starts-with-dash", "{}")]
    [InlineData("has space", "{}")]
    [InlineData("aetheus.valid", "not json")]
    public async Task InvalidKeyOrValue_IsRefusedWith400(string key, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var user = await IntegrationUsers.CreateAsync(factory.Services, "prefs-invalid", []);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var response = await client.PutAsJsonAsync($"{Root}/{Uri.EscapeDataString(key)}",
            new SaveUserPreferenceRequest { Value = value, UpdatedAt = DateTimeOffset.UtcNow }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ValueOverThePerKeyLimit_IsRefusedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var user = await IntegrationUsers.CreateAsync(factory.Services, "prefs-size", []);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
        var tooLong = "\"" + new string('x', UserPreferenceRules.MaxValueLength) + "\"";

        var response = await client.PutAsJsonAsync($"{Root}/{Key}",
            new SaveUserPreferenceRequest { Value = tooLong, UpdatedAt = DateTimeOffset.UtcNow }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<UserPreferenceDto> PutAsync(HttpClient client, string key, string value, DateTimeOffset updatedAt)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PutAsJsonAsync($"{Root}/{key}",
            new SaveUserPreferenceRequest { Value = value, UpdatedAt = updatedAt }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserPreferenceDto>(IntegrationJsonOptions.Default, ct))!;
    }

    private static async Task<UserPreferenceDto?> GetAsync(HttpClient client, string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync($"{Root}/{key}", ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<UserPreferenceDto>(IntegrationJsonOptions.Default, ct);
    }
}
