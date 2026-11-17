// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-28 over the real HTTP pipeline and PostgreSQL: an organization's exclusion rules are stored per
/// organization, a Reader reads them and a Contributor edits them (least privilege, the built-in roles' default
/// keys), and the logs endpoint splits the log into the two tabs by the enabled rules without losing a line.
/// </summary>
public sealed class SystemLogExclusionRulesIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Marker = "ZZ-R28-MARKER";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RulesAreScopedToTheirOrganization_AReaderReadsAndAContributorEdits()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var organizationId = await DefaultOrganizationIdAsync(factory);
        var otherOrganizationId = await CreateOrganizationAsync(factory);
        using var reader = await ClientWithRoleAsync(factory, admin, "Reader");
        using var contributor = await ClientWithRoleAsync(factory, admin, "Contributor");

        // The Contributor edits: creates, switches off, deletes.
        var created = await CreateRuleAsync(contributor, organizationId, $"Slow request POST /api/tasks/claim {Marker}");
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var rule = created.Rule!;
        Assert.True(rule.Enabled);
        Assert.Equal(organizationId, rule.OrganizationId);

        // The Reader reads the organization's rules but edits none of them.
        var readerList = await reader.GetFromJsonAsync<List<SystemLogExclusionRuleDto>>(
            $"/api/system-log-rules?organizationId={organizationId}", IntegrationJsonOptions.Default, Ct);
        Assert.Contains(readerList!, listed => listed.Id == rule.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateRuleAsync(reader, organizationId, "forbidden pattern")).Status);
        using (var denied = await reader.PutAsJsonAsync(
            $"/api/system-log-rules/{rule.Id}/enabled?organizationId={organizationId}", new SetSystemLogExclusionRuleEnabledRequest(false), Ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.DeleteAsync($"/api/system-log-rules/{rule.Id}?organizationId={organizationId}", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // A rule belongs to one organization: another organization has none, and a member of the first cannot read it.
        var otherList = await admin.GetFromJsonAsync<List<SystemLogExclusionRuleDto>>(
            $"/api/system-log-rules?organizationId={otherOrganizationId}", IntegrationJsonOptions.Default, Ct);
        Assert.Empty(otherList!);
        using (var foreign = await reader.GetAsync($"/api/system-log-rules?organizationId={otherOrganizationId}", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var crossDelete = await admin.DeleteAsync($"/api/system-log-rules/{rule.Id}?organizationId={otherOrganizationId}", Ct))
            Assert.Equal(HttpStatusCode.NotFound, crossDelete.StatusCode);

        // The same rule twice is refused, the Contributor switches it off, then deletes it.
        Assert.Equal(HttpStatusCode.Conflict, (await CreateRuleAsync(contributor, organizationId, rule.Pattern)).Status);
        using (var off = await contributor.PutAsJsonAsync(
            $"/api/system-log-rules/{rule.Id}/enabled?organizationId={organizationId}", new SetSystemLogExclusionRuleEnabledRequest(false), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.False((await off.Content.ReadFromJsonAsync<SystemLogExclusionRuleDto>(IntegrationJsonOptions.Default, Ct))!.Enabled);
        }

        using (var removed = await contributor.DeleteAsync($"/api/system-log-rules/{rule.Id}?organizationId={organizationId}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var afterDelete = await admin.GetFromJsonAsync<List<SystemLogExclusionRuleDto>>(
            $"/api/system-log-rules?organizationId={organizationId}", IntegrationJsonOptions.Default, Ct);
        Assert.DoesNotContain(afterDelete!, listed => listed.Id == rule.Id);
    }

    [Fact]
    public async Task TheLogIsSplitInTwoTabsByTheEnabledRules_AndNothingIsLost()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"aetheus_r28_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(logDirectory, "app.json"),
            [
                Line("Warning", "Aetheus.Back.Middleware.SlowRequestDiagnosticsMiddleware", $"Slow request POST /api/tasks/claim: 1628 ms {Marker}"),
                Line("Warning", "Aetheus.Back.Middleware.SlowRequestDiagnosticsMiddleware", $"Slow request POST /api/logs/batch: 1526 ms {Marker}"),
                Line("Error", "Aetheus.Back.Components.SystemLogs.ClientErrorsController", $"Client error at /pipelines/setup {Marker}"),
                Line("Information", "Aetheus.Back.Services.PostgresLeaderLease", $"Acquired leader lease {Marker}")
            ], Ct);

            await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
            await using var factory = baseFactory.WithWebHostBuilder(builder =>
                builder.UseSetting("Logging:FileLog:Directory", logDirectory));
            using var admin = await IntegrationUsers.AdminClientAsync(factory);
            var organizationId = await DefaultOrganizationIdAsync(factory);

            // No rule: the first tab is the whole log and the second is empty.
            Assert.Equal(4, (await EntriesAsync(admin, organizationId, excluded: false)).Count);
            Assert.Empty(await EntriesAsync(admin, organizationId, excluded: true));

            var rule = (await CreateRuleAsync(admin, organizationId, "Slow request")).Rule!;
            var kept = await EntriesAsync(admin, organizationId, excluded: false);
            var moved = await EntriesAsync(admin, organizationId, excluded: true);
            Assert.Equal(2, moved.Count);
            Assert.All(moved, entry => Assert.StartsWith("Slow request", entry.Message, StringComparison.Ordinal));
            Assert.Equal(2, kept.Count);
            Assert.DoesNotContain(kept, entry => entry.Message.StartsWith("Slow request", StringComparison.Ordinal));
            // Together the tabs are the whole log: no line is lost, none is in both.
            var everything = await EntriesAsync(admin, organizationId: null, excluded: false);
            Assert.Equal(everything.Select(entry => entry.Message).Order(), kept.Concat(moved).Select(entry => entry.Message).Order());

            // A rule limited to a category moves only the lines of that category.
            await DeleteRuleAsync(admin, organizationId, rule.Id);
            await CreateRuleAsync(admin, organizationId, Marker, category: "ClientErrorsController");
            Assert.Single(await EntriesAsync(admin, organizationId, excluded: true));

            // A disabled rule moves nothing: the whole log is back on the first tab.
            var categoryRule = (await RulesAsync(admin, organizationId)).Single();
            using (var off = await admin.PutAsJsonAsync(
                $"/api/system-log-rules/{categoryRule.Id}/enabled?organizationId={organizationId}",
                new SetSystemLogExclusionRuleEnabledRequest(false), Ct))
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.Equal(4, (await EntriesAsync(admin, organizationId, excluded: false)).Count);
            Assert.Empty(await EntriesAsync(admin, organizationId, excluded: true));
        }
        finally
        {
            Directory.Delete(logDirectory, true);
        }
    }

    private static string Line(string level, string category, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["Level"] = level,
            ["SourceContext"] = category,
            ["RenderedMessage"] = message
        });

    private static async Task<List<SystemLogEntryDto>> EntriesAsync(HttpClient admin, int? organizationId, bool excluded)
    {
        var scope = organizationId is { } organization ? $"&organizationId={organization}&excluded={excluded.ToString().ToLowerInvariant()}" : string.Empty;
        var page = await admin.GetFromJsonAsync<PaginatedResult<SystemLogEntryDto>>(
            $"/api/system-logs/entries?search={Marker}&pageSize=200{scope}", IntegrationJsonOptions.Default, Ct);
        return page!.Items;
    }

    private static async Task<List<SystemLogExclusionRuleDto>> RulesAsync(HttpClient client, int organizationId) =>
        (await client.GetFromJsonAsync<List<SystemLogExclusionRuleDto>>(
            $"/api/system-log-rules?organizationId={organizationId}", IntegrationJsonOptions.Default, Ct))!;

    private static async Task DeleteRuleAsync(HttpClient client, int organizationId, int ruleId)
    {
        using var response = await client.DeleteAsync($"/api/system-log-rules/{ruleId}?organizationId={organizationId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, SystemLogExclusionRuleDto? Rule)> CreateRuleAsync(
        HttpClient client, int organizationId, string pattern, string category = "")
    {
        using var response = await client.PostAsJsonAsync($"/api/system-log-rules?organizationId={organizationId}",
            new SaveSystemLogExclusionRuleRequest { Pattern = pattern, Category = category }, Ct);
        return (response.StatusCode, response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SystemLogExclusionRuleDto>(IntegrationJsonOptions.Default, Ct)
            : null);
    }

    [Fact]
    public async Task TheSameRuleWrittenTwice_IsAConflict_FromTheUniqueIndex()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var organizationId = await CreateOrganizationAsync(factory);
        SystemLogExclusionRule Rule() => new() { OrganizationId = organizationId, Pattern = Marker, Category = string.Empty };

        using var first = factory.Services.CreateScope();
        await first.ServiceProvider.GetRequiredService<ISystemLogExclusionRepository>().AddAsync(Rule(), Ct);
        using var second = factory.Services.CreateScope();

        // The service's check before the insert passed for both: only the index can tell, as with two administrators at once.
        await Assert.ThrowsAsync<ConflictException>(
            () => second.ServiceProvider.GetRequiredService<ISystemLogExclusionRepository>().AddAsync(Rule(), Ct));
    }

    private static async Task<int> DefaultOrganizationIdAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Organizations.Where(organization => organization.Slug == IntegrationUsers.DefaultOrganizationSlug)
            .Select(organization => organization.Id).SingleAsync(Ct);
    }

    private static async Task<int> CreateOrganizationAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"r28-{Guid.NewGuid():N}", Slug = $"r28-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        return organization.Id;
    }

    /// <summary>A client signed in as a new account holding the built-in <paramref name="role"/> for the default
    /// organization, created through the users API as the administrator does.</summary>
    private static async Task<HttpClient> ClientWithRoleAsync(
        WebApplicationFactory<Program> factory, HttpClient admin, string role)
    {
        var username = $"r28-{role.ToLowerInvariant()}-{Guid.NewGuid():N}";
        using var response = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Email = $"{username}@example.com",
            OrganizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(admin, role)
        }, Ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("User creation returned an empty body.");
        await IntegrationUsers.SetPasswordAsync(factory.Services, created.Id, IntegrationUsers.Password);
        return await IntegrationUsers.LoginClientAsync(factory, username);
    }
}
