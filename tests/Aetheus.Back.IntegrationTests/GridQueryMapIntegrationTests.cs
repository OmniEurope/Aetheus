// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Executes grid-filter expressions through Npgsql on PostgreSQL to verify provider
/// translation and results beyond LINQ-to-objects tests.</summary>
[Collection(PostgresCollection.Name)]
public sealed class GridQueryMapIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly GridQueryMap<Server> Columns = new GridQueryMap<Server>()
        .Number("id", server => server.Id)
        .Number("protocol", server => server.AgentProtocolVersion)
        .Text("name", server => server.Name)
        .Text("session", server => server.AgentSessionId)
        .Date("installedAt", server => server.AgentInstalledAt)
        .Enum("status", server => server.Status)
        .Boolean("docker", server => server.DockerAvailable);

    [Fact]
    public async Task EveryOperator_IsTranslatedByNpgsql_AndMatchesTheExpectedRows()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int organizationId;
        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = "Grid filters", Slug = $"grid-{Guid.NewGuid():N}" };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            organizationId = organization.Id;
            seed.Servers.AddRange(
                Server(organizationId, "Web-Front-01", "s-1", 3, ServerStatus.Online, true, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)),
                Server(organizationId, "db-main", null, 2, ServerStatus.Offline, false, new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc), null),
                Server(organizationId, "web-back-02", "", null, ServerStatus.Online, false, new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync(ct);
        }

        async Task<string[]> Names(params GridFilter[] filters)
        {
            await using var db = NewContext();
            var scoped = db.Servers.AsNoTracking().Where(server => server.OrganizationId == organizationId);
            // Ordered here, not in SQL, so the assertion does not depend on the database collation.
            var names = await Columns.ApplyFilters(scoped, filters).Select(server => server.Name).ToArrayAsync(ct);
            return [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }

        static GridFilter F(string field, GridFilterOperator op, string? value = null) => new() { Field = field, Operator = op, Value = value };

        Assert.Equal(["web-back-02", "Web-Front-01"], await Names(F("name", GridFilterOperator.Contains, "WEB")));
        Assert.Equal(["db-main"], await Names(F("name", GridFilterOperator.DoesNotContain, "web")));
        Assert.Equal(["web-back-02", "Web-Front-01"], await Names(F("name", GridFilterOperator.StartsWith, "web-")));
        Assert.Equal(["db-main"], await Names(F("name", GridFilterOperator.EndsWith, "MAIN")));
        Assert.Equal(["Web-Front-01"], await Names(F("name", GridFilterOperator.Equals, "web-front-01")));
        Assert.Equal(["db-main", "web-back-02"], await Names(F("name", GridFilterOperator.NotEquals, "WEB-FRONT-01")));
        Assert.Equal(["db-main"], await Names(F("session", GridFilterOperator.IsNull)));
        Assert.Equal(["db-main", "web-back-02"], await Names(F("session", GridFilterOperator.IsEmpty)));
        Assert.Equal(["Web-Front-01"], await Names(F("session", GridFilterOperator.IsNotEmpty)));
        Assert.Equal(["Web-Front-01"], await Names(F("protocol", GridFilterOperator.GreaterThan, "2")));
        Assert.Equal(["db-main", "Web-Front-01"], await Names(F("protocol", GridFilterOperator.GreaterThanOrEqual, "2")));
        Assert.Equal(["db-main"], await Names(F("protocol", GridFilterOperator.LessThan, "3")));
        Assert.Equal(["db-main", "Web-Front-01"], await Names(F("protocol", GridFilterOperator.LessThanOrEqual, "3")));
        Assert.Equal(["web-back-02"], await Names(F("protocol", GridFilterOperator.IsNull)));
        // CreatedAt is stamped by the context on save, so the range is proven on a date the test controls.
        Assert.Equal(["web-back-02"], await Names(F("installedAt", GridFilterOperator.GreaterThanOrEqual, "2026-09-02T00:00:00Z")));
        Assert.Equal(["Web-Front-01"], await Names(F("installedAt", GridFilterOperator.LessThan, "2026-09-02T00:00:00Z")));
        Assert.Equal(["web-back-02", "Web-Front-01"], await Names(F("installedAt", GridFilterOperator.IsNotNull)));
        Assert.Equal(["web-back-02", "Web-Front-01"], await Names(F("status", GridFilterOperator.Equals, "Online")));
        Assert.Equal(["db-main"], await Names(F("status", GridFilterOperator.NotEquals, "online")));
        Assert.Equal(["Web-Front-01"], await Names(F("docker", GridFilterOperator.Equals, "true")));
        Assert.Equal(["db-main", "Web-Front-01"], await Names(new GridFilter
        {
            Field = "name",
            Operator = GridFilterOperator.StartsWith,
            Value = "db",
            SecondOperator = GridFilterOperator.EndsWith,
            SecondValue = "01",
            Logic = GridFilterLogic.Or
        }));
        Assert.Equal(["web-back-02"], await Names(F("name", GridFilterOperator.Contains, "web"), F("docker", GridFilterOperator.Equals, "false")));
        // Checkable lists, translated to one array parameter.
        Assert.Equal(["db-main", "web-back-02", "Web-Front-01"], await Names(F("status", GridFilterOperator.In, "Online\u001FOffline")));
        Assert.Equal(["db-main"], await Names(F("status", GridFilterOperator.NotIn, "Online")));
        Assert.Equal(["db-main", "Web-Front-01"], await Names(F("name", GridFilterOperator.In, "WEB-FRONT-01\u001Fdb-main")));
        Assert.Equal(["db-main", "Web-Front-01"], await Names(F("protocol", GridFilterOperator.In, "2\u001F3")));
    }

    [Fact]
    public async Task Sorts_AreTranslatedByNpgsql()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var db = NewContext();
        var organization = new Organization { Name = "Grid sorts", Slug = $"sort-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        db.Servers.AddRange(
            Server(organization.Id, "b", null, 1, ServerStatus.Online, true, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), null),
            Server(organization.Id, "a", null, 2, ServerStatus.Online, false, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), null),
            Server(organization.Id, "c", null, 2, ServerStatus.Offline, true, new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), null));
        await db.SaveChangesAsync(ct);

        var sorted = Columns.ApplySorts(
            db.Servers.AsNoTracking().Where(server => server.OrganizationId == organization.Id),
            [new GridSort { Field = "protocol", Descending = true }, new GridSort { Field = "name" }]);

        Assert.NotNull(sorted);
        Assert.Equal(["a", "c", "b"], await sorted!.Select(server => server.Name).ToArrayAsync(ct));
    }

    private static Server Server(int organizationId, string name, string? session, int? protocol, ServerStatus status, bool docker, DateTime createdAt, DateTime? installedAt) => new()
    {
        Name = name,
        Hostname = name,
        OrganizationId = organizationId,
        AgentSessionId = session,
        AgentProtocolVersion = protocol,
        Status = status,
        DockerAvailable = docker,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        LastHeartbeat = createdAt,
        AgentInstalledAt = installedAt
    };
}
