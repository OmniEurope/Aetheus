// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Exercises port allocation over HTTP with all host-wired collaborators, including
/// post-commit audit and change notification.</summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class PortAllocationApiIntegrationTests(ApiSmokeFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllocatingFourKeys_AnswersOk_WithTheEntriesItWrote()
    {
        int libraryId, serverId, projectId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organization = await db.Organizations.FirstAsync(o => o.Slug == "aetheus", Ct);
            var server = new Server
            {
                Name = $"alloc-{Guid.NewGuid():N}",
                Hostname = $"alloc-{Guid.NewGuid():N}.test",
                OrganizationId = organization.Id,
                Status = ServerStatus.Online
            };
            var project = new Project { Name = $"alloc-{Guid.NewGuid():N}", OrganizationId = organization.Id };
            db.Servers.Add(server);
            db.Projects.Add(project);
            await db.SaveChangesAsync(Ct);
            db.ProjectServers.Add(new ProjectServer
            {
                ProjectId = project.Id,
                ServerId = server.Id,
                Type = ProjectServerType.ExternalHost,
                DisplayName = "prod",
                Host = "prod.local"
            });
            var library = new VariableLibrary { Name = $"alloc-{Guid.NewGuid():N}", ProjectId = project.Id };
            db.VariableLibraries.Add(library);
            db.ServerPortRanges.Add(new ServerPortRange { ServerId = server.Id, From = 41000, To = 41010, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
            (libraryId, serverId, projectId) = (library.Id, server.Id, project.Id);
        }

        using var client = fixture.CreateAdminClient();
        string[] keys = ["PORT_FRONT", "PORT_BACK", "PORT_ADMIN", "PORT_METRICS"];
        var response = await client.PostAsJsonAsync(
            $"/api/variable-libraries/{libraryId}/ports/allocate",
            new AllocateLibraryPortsRequest { ServerId = serverId, Keys = [.. keys] },
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<List<VariableEntryDto>>(IntegrationJsonOptions.Default, Ct);
        Assert.Equal(keys, created!.Select(entry => entry.Key));

        using var verifyScope = fixture.Factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await verify.VariableLibraryEntries.AsNoTracking()
            .Where(entry => entry.VariableLibraryId == libraryId).ToListAsync(Ct);
        var reservations = await verify.ServerPortReservations.AsNoTracking()
            .Where(reservation => reservation.ServerId == serverId).ToListAsync(Ct);
        Assert.Equal(4, entries.Count);
        Assert.Equal(
            reservations.Select(reservation => reservation.Port.ToString(CultureInfo.InvariantCulture)).Order(),
            entries.Select(entry => entry.Value).Order());
        Assert.All(reservations, reservation => Assert.Equal($"project:{projectId}", reservation.OwnerKey));
    }
}
