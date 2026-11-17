// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises listing, creation, checking, allocation and release through the HTTP
/// controller-service-repository path on PostgreSQL.</summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class PortRegistryApiRoundTripTests(ApiSmokeFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A port in the QA band, away from the default allocation window, so these round-trips
    /// never take a port an allocation test is about to be handed.</summary>
    private static int FreePort() => RandomNumberGenerator.GetInt32(30000, 39000);

    [Fact]
    public async Task ManualReservation_IsListed_ThenRefusesTheSamePort_ThenIsReleased()
    {
        using var client = fixture.CreateAdminClient();
        var port = FreePort();
        var holder = $"round-trip-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports",
            new CreatePortReservationRequest { Port = port, OwnerLabel = holder },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var reservation = await created.Content.ReadFromJsonAsync<PortReservationDto>(
            IntegrationJsonOptions.Default, cancellationToken: Ct);
        Assert.Equal(PortReservationSource.Manual, reservation!.Source);

        try
        {
            // Listed, which is what the page renders.
            var list = await client.GetFromJsonAsync<List<PortReservationDto>>(
                $"/api/servers/{fixture.ServerId}/ports", IntegrationJsonOptions.Default, Ct);
            Assert.Contains(list!, row => row.Port == port && row.OwnerLabel == holder);

            // The refusal names the holder, which is the whole point of the registry.
            var duplicate = await client.PostAsJsonAsync(
                $"/api/servers/{fixture.ServerId}/ports",
                new CreatePortReservationRequest { Port = port, OwnerLabel = "somebody-else" },
                cancellationToken: Ct);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Contains(holder, await duplicate.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            var released = await client.DeleteAsync(
                $"/api/servers/{fixture.ServerId}/ports/{reservation.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }

        var after = await client.GetFromJsonAsync<List<PortReservationDto>>(
            $"/api/servers/{fixture.ServerId}/ports", IntegrationJsonOptions.Default, Ct);
        Assert.DoesNotContain(after!, row => row.Port == port);
    }

    [Fact]
    public async Task Reader_CanCheckButCannotReserve()
    {
        using var reader = await fixture.CreateReaderClientAsync();

        var check = await reader.PostAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports/check",
            new PortCheckRequest { Ports = [FreePort()] },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);

        var write = await reader.PostAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports",
            new CreatePortReservationRequest { Port = FreePort(), OwnerLabel = "reader" },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
