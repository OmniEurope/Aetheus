// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Notifications;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The host system CVE in the daily CVE mail on PostgreSQL (recette R-41, point 4): the findings of the servers of a
/// project are told with that project, once, by the same rule as its packages (Critical, exploited or published High; new
/// only; nothing new, no mail), and given back when the send fails. Real digest, schedule, claim and project/server links;
/// the host findings are stored directly (their computation is proved in <c>ServerHostVulnerabilityHttpIntegrationTests</c>)
/// and only the delivery of the event is recorded instead of made, so the recipients and the SMTP delivery are not proved here.
/// </summary>
public sealed class CveWatchHostNightlyMailIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private List<JsonElement> Events() =>
        [.. Sent.Where(sent => sent.EventType == NotificationEventTypes.CveDetected).Select(sent => JsonDocument.Parse(sent.Json).RootElement.Clone())];

    private async Task<int> SeedServerAsync(int organizationId, string name, int? projectId)
    {
        await using var db = NewContext();
        var server = new Server { Name = name, Hostname = $"{name}.local", OrganizationId = organizationId };
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);
        if (projectId is { } id)
        {
            db.ProjectServers.Add(new ProjectServer
            {
                ProjectId = id,
                ServerId = server.Id,
                Type = ProjectServerType.ExternalHost,
                DisplayName = name,
                Host = $"{name}.local"
            });
            await db.SaveChangesAsync(Ct);
        }

        return server.Id;
    }

    private async Task<long> AddFindingAsync(int serverId, string advisoryExternalId, string package, string? fix)
    {
        await using var db = NewContext();
        var advisory = await db.VulnerabilityAdvisories.SingleAsync(row => row.ExternalId == advisoryExternalId, Ct);
        var finding = new ServerVulnerabilityFinding
        {
            ServerId = serverId,
            AdvisoryId = advisory.Id,
            Ecosystem = "Ubuntu:24.04:LTS",
            PackageName = package,
            BinaryPackages = package,
            InstalledVersion = "1.0-1ubuntu1",
            FixedVersion = fix,
            Status = VulnerabilityMatchStatus.Affected,
            FirstMatchedAt = Clock.GetUtcNow().UtcDateTime
        };
        db.ServerVulnerabilityFindings.Add(finding);
        await db.SaveChangesAsync(Ct);
        return finding.Id;
    }

    private async Task<DateTime?> NotifiedAtAsync(long findingId)
    {
        await using var db = NewContext();
        return (await db.ServerVulnerabilityFindings.AsNoTracking().SingleAsync(row => row.Id == findingId, Ct)).NotifiedAt;
    }

    private static JsonElement ProjectOf(JsonElement payload, int projectId) =>
        payload.GetProperty("Projects").EnumerateArray().Single(project => project.GetProperty("ProjectId").GetInt32() == projectId);

    [Fact]
    public async Task AHostFinding_IsToldWithTheProjectOfItsServer_OnceAndOnlyWhenWorthAMail()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var attached = await SeedServerAsync(owner.OrganizationId, "srv-attached", owner.ProjectId);
        var alone = await SeedServerAsync(owner.OrganizationId, "srv-alone", projectId: null);
        await ApplyAsync(
            Advisory("GHSA-1", "openssl", UpToFix, severity: "CRITICAL"),
            Advisory("GHSA-2", "zlib", UpToFix, severity: "MODERATE", cve: "CVE-2099-0002"));
        var worthy = await AddFindingAsync(attached, "GHSA-1", "openssl", "3.0.13-0ubuntu3.4");
        var tooMild = await AddFindingAsync(attached, "GHSA-2", "zlib", "1.3-1");
        var ofNoProject = await AddFindingAsync(alone, "GHSA-1", "openssl", null);
        await SetRecapHourAsync("08:00");

        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        // One event, a section of the server carrying the CVE of its host: the Medium one and the one of a server that
        // belongs to no project are not in it.
        var night = Assert.Single(Events());
        Assert.True(night.GetProperty("Digest").GetBoolean());
        var block = ProjectOf(night, owner.ProjectId);
        Assert.Equal(0, block.GetProperty("VulnerabilityCount").GetInt32());
        Assert.False(block.TryGetProperty("HostVulnerabilityCount", out _), "the project block holds no host vulnerability");
        var section = Assert.Single(night.GetProperty("Servers").EnumerateArray());
        Assert.Equal(1, section.GetProperty("VulnerabilityCount").GetInt32());
        Assert.Equal(attached, section.GetProperty("ServerId").GetInt32());
        Assert.Equal("srv-attached", section.GetProperty("ServerName").GetString());
        Assert.Equal(owner.ProjectId, Assert.Single(section.GetProperty("ProjectIds").EnumerateArray()).GetInt32());
        Assert.Equal(1, section.GetProperty("NewCount").GetInt32());
        Assert.Equal("Critical", section.GetProperty("Severity").GetString());
        Assert.NotNull(await NotifiedAtAsync(worthy));
        Assert.Null(await NotifiedAtAsync(tooMild));
        Assert.Null(await NotifiedAtAsync(ofNoProject));

        // The next day it is still open and nothing is new: no second mail.
        AdvanceClock(TimeSpan.FromDays(1));
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        Assert.Single(Events());
    }

    [Fact]
    public async Task ANightThatFailsToSend_GivesTheHostFindingsBack_SoTheNextNightTellsThem()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var server = await SeedServerAsync(owner.OrganizationId, "srv-attached", owner.ProjectId);
        await ApplyAsync(Advisory("GHSA-1", "openssl", UpToFix, severity: "CRITICAL"));
        var finding = await AddFindingAsync(server, "GHSA-1", "openssl", "3.0.13-0ubuntu3.4");
        await SetRecapHourAsync("08:00");
        FailNextSend = new InvalidOperationException("the notification service is down");

        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        Assert.Empty(Events());
        Assert.Null(await NotifiedAtAsync(finding));

        AdvanceClock(TimeSpan.FromDays(1));
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        Assert.Equal(1, Assert.Single(Assert.Single(Events()).GetProperty("Servers").EnumerateArray()).GetProperty("VulnerabilityCount").GetInt32());
    }
}
