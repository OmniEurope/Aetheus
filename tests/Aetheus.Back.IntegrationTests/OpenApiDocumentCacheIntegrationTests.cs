// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-28: the OpenAPI document feeding Administration, API reference is built once per process
/// (reflection over every controller), never again on each opening of the page.
/// </summary>
public sealed class OpenApiDocumentCacheIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task GetOpenApiDocument_RepeatedCalls_AreServedFromTheProcessCache()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var servedFromCache = new List<bool>();
        string? first = null;

        // Act
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync("/openapi/v1.json", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            first ??= body;
            Assert.Equal(first, body);
            // The output cache stamps an Age header on a response it serves from its store, and only then.
            servedFromCache.Add(response.Headers.Age is not null);
        }

        // Assert - the first call pays the generation, the following ones are answered by the cache. The cache itself
        // says so: no wall-clock threshold, which a loaded machine can make meaningless.
        Assert.False(servedFromCache[0], "The first call cannot come from a cache that was still empty.");
        Assert.True(servedFromCache.Skip(1).All(served => served),
            $"Repeated calls were not served from cache (Age header present per call: {string.Join(", ", servedFromCache)})");
    }
}
