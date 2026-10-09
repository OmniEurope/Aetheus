// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-005 end to end over real HTTP, as the bootstrap admin, against the real controller → service →
/// repository → PostgreSQL stack. This is the automatable half of the plan's per-lot field controls:
/// what a person would do on the Ports page - list, add, check, allocate, release - done through the
/// same API the page calls, so a break is caught here rather than on somebody's screen.
/// </summary>
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
    public async Task Check_AnswersFreeAndTaken_WithTheObservationAlongside()
    {
        using var client = fixture.CreateAdminClient();
        var taken = FreePort();
        // INT-003: derived from the taken port, never drawn again, so the two can never be the same
        // port; still inside the QA band.
        var free = taken + 1 < 39000 ? taken + 1 : taken - 1;
        var holder = $"check-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports",
            new CreatePortReservationRequest { Port = taken, OwnerLabel = holder },
            cancellationToken: Ct);
        var reservation = await created.Content.ReadFromJsonAsync<PortReservationDto>(
            IntegrationJsonOptions.Default, cancellationToken: Ct);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/api/servers/{fixture.ServerId}/ports/check",
                new PortCheckRequest { Ports = [taken, free] },
                cancellationToken: Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<PortCheckResultDto>(
                IntegrationJsonOptions.Default, cancellationToken: Ct);

            var takenEntry = result!.Entries.Single(entry => entry.Port == taken);
            Assert.False(takenEntry.IsFree);
            Assert.Equal(holder, takenEntry.OwnerLabel);
            Assert.Equal(PortCheckVerdict.TakenByAnotherOwner, takenEntry.Verdict);

            var freeEntry = result.Entries.Single(entry => entry.Port == free);
            Assert.True(freeEntry.IsFree);
            // The seeded server has never been scanned, so the honest answer is "unknown", not "free".
            Assert.Equal(PortObservationState.NeverScanned, freeEntry.Observation);
            Assert.Equal(PortCheckVerdict.Free, freeEntry.Verdict);
        }
        finally
        {
            await client.DeleteAsync($"/api/servers/{fixture.ServerId}/ports/{reservation!.Id}", Ct);
        }
    }

    [Fact]
    public async Task Observe_OnAnAgentThatCannotScan_IsRefusedByNamingTheCapability()
    {
        using var client = fixture.CreateAdminClient();

        var response = await client.PostAsync(
            $"/api/servers/{fixture.ServerId}/ports/observe", content: null, Ct);

        // The seeded server's agent publishes no ports.observe, so the scan is refused up front rather
        // than queued as a task no agent would ever run.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "ports.observe", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllocationWindow_IsReadAsTheFleetDefault_ThenSetOnTheServer()
    {
        using var client = fixture.CreateAdminClient();

        var initial = await client.GetFromJsonAsync<PortRangeDto>(
            $"/api/servers/{fixture.ServerId}/ports/range", IntegrationJsonOptions.Default, Ct);
        var hadOwnWindow = initial!.IsExplicit;

        var saved = await client.PutAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports/range",
            new PortRangeDto { From = 40000, To = 40010 },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var stored = await client.GetFromJsonAsync<PortRangeDto>(
            $"/api/servers/{fixture.ServerId}/ports/range", IntegrationJsonOptions.Default, Ct);
        Assert.Equal(40000, stored!.From);
        Assert.Equal(40010, stored.To);
        Assert.True(stored.IsExplicit);

        // An inverted window is refused rather than stored.
        var inverted = await client.PutAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports/range",
            new PortRangeDto { From = 40010, To = 40000 },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);

        if (!hadOwnWindow)
        {
            // Restore the fleet default for the suites sharing this server: leaving a narrow window
            // behind would make an unrelated allocation fail for want of free ports.
            await client.PutAsJsonAsync(
                $"/api/servers/{fixture.ServerId}/ports/range",
                new PortRangeDto
                {
                    From = PortRegistryLimits.DefaultRangeFrom,
                    To = PortRegistryLimits.DefaultRangeTo
                },
                cancellationToken: Ct);
        }
    }

    [Fact]
    public async Task Allocate_HandsOutFreePorts_AndTheyComeBackAsReservations()
    {
        using var client = fixture.CreateAdminClient();
        var holder = $"alloc-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            $"/api/servers/{fixture.ServerId}/ports/allocate",
            new AllocatePortsRequest { Count = 2, OwnerLabel = holder },
            cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ports = await response.Content.ReadFromJsonAsync<List<int>>(
            IntegrationJsonOptions.Default, cancellationToken: Ct);

        Assert.Equal(2, ports!.Count);
        Assert.Equal(ports.Count, ports.Distinct().Count());

        var list = await client.GetFromJsonAsync<List<PortReservationDto>>(
            $"/api/servers/{fixture.ServerId}/ports", IntegrationJsonOptions.Default, Ct);
        var mine = list!.Where(row => row.OwnerLabel == holder).ToList();
        Assert.Equal(2, mine.Count);
        // Reserved as it hands them out: a caller must treat the answer as already taken.
        Assert.Equal(ports.Order(), mine.Select(row => row.Port).Order());

        foreach (var reservation in mine)
            await client.DeleteAsync($"/api/servers/{fixture.ServerId}/ports/{reservation.Id}", Ct);
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
