// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-28: the OpenAPI document feeding Administration, API reference is built once per process
/// (reflection over every controller), never again on each opening of the page.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenApiDocumentCacheIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetOpenApiDocument_RepeatedCalls_AreServedFromTheProcessCache()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        var durations = new List<long>();
        string? first = null;

        // Act
        for (var i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/openapi/v1.json", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            first ??= body;
            Assert.Equal(first, body);
            durations.Add(sw.ElapsedMilliseconds);
        }

        // Assert - the first call pays the generation, the following ones must be much cheaper.
        TestContext.Current.SendDiagnosticMessage($"R28 openapi durations ms: {string.Join(", ", durations)}");
        var cached = durations.Skip(1).Max();
        Assert.True(cached * 4 <= durations[0] || cached <= 15,
            $"Repeated calls were not served from cache: {string.Join(", ", durations)} ms");
    }
}
