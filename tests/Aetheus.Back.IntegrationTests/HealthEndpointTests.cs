// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// HTTP smoke test through the real Kestrel pipeline backed by the PostgreSQL container.
/// Building the factory boots the app end-to-end: real EF <c>MigrateAsync()</c> +
/// <c>DbInitializer.SeedAsync()</c> against the container, then serves HTTP. If the real
/// migrations did not apply against real PostgreSQL the host would not start and these
/// tests would fail - which is exactly the signal the InMemory unit suite cannot give.
/// </summary>
public sealed class HealthEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    [Trait("Category", "RollbackRelevant")]
    public async Task GetHealthLive_Anonymous_Returns200()
    {
        // Arrange
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // Act - "/health/live" is the anonymous lightweight liveness probe (F-18).
        // A 200 here proves the host booted: building the factory ran the real
        // MigrateAsync()/SeedAsync() against the Postgres container first.
        using var response = await client.GetAsync("/health/live", cancellationToken: TestContext.Current.CancellationToken);

        // Assert (status only - the lightweight probe customises its response body)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_Anonymous_RequiresAuth_Returns401()
    {
        // Arrange
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        // Act - F-18: "/health" runs full checks (incl. DB) and is auth-gated so
        // anonymous callers cannot probe service internals.
        using var response = await client.GetAsync("/health", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
