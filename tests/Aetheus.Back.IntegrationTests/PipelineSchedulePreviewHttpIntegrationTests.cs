// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// <c>GET api/pipelines/{id}/schedule-preview</c> through the real HTTP pipeline, JWT included, on real
/// PostgreSQL: the occurrences and the overlaps of a proposed schedule, with the read authorization of the
/// schedule plan, and nothing written.
/// </summary>
public sealed class PipelineSchedulePreviewHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);

    private static string At(int hour, int minute = 0) => $"0 {minute} {hour} * * *";

    private static string Url(
        int pipelineId, string frequency = "EveryEvening", int hour = 1, int minute = 30, int? day = null,
        DateTime? from = null, DateTime? to = null) =>
        $"/api/pipelines/{pipelineId}/schedule-preview?frequency={frequency}&hour={hour}&minute={minute}"
        + (day is null ? string.Empty : $"&day={day}")
        + $"&from={from ?? Day:yyyy-MM-ddTHH:mm:ssZ}&to={to ?? Day.AddDays(1):yyyy-MM-ddTHH:mm:ssZ}";

    [Fact]
    public async Task Preview_GivesTheProposedOccurrences_AndOnlyTheOverlapsOfOtherEnabledTasks_AndWritesNothing()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "overlap");
        // Today at 03:00; the proposal moves it to 01:30 and replaces that schedule.
        var moved = await SeedPipelineAsync(factory, project, "moved", At(3));
        var busy = await SeedPipelineAsync(factory, project, "busy", At(1));
        var next = await SeedPipelineAsync(factory, project, "next", At(2, 0));
        await SeedPipelineAsync(factory, project, "off", At(1), PipelineTriggerType.Manual);
        await SeedRunsAsync(factory, moved, [30]);
        var before = await ReadStateAsync(factory, moved);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var preview = await ReadPreviewAsync(admin, Url(moved));

        Assert.Equal(moved, preview.PipelineId);
        Assert.Null(preview.YamlError);
        var proposal = preview.Proposal;
        Assert.Equal("moved", proposal.PipelineName);
        Assert.Equal("0 30 1 * * *", proposal.CronExpression);
        Assert.Equal(ScheduleFrequency.EveryEvening, proposal.Frequency);
        Assert.Equal((1, 30), (proposal.Hour, proposal.Minute));
        Assert.Equal(30, proposal.DurationMinutes);
        Assert.False(proposal.IsPlanned);
        var run = Assert.Single(proposal.Occurrences);
        Assert.Equal(Day.AddHours(1).AddMinutes(30), run.StartUtc);
        Assert.Equal(Day.AddHours(2), run.EndUtc);
        // 01:30 to 02:00 sits inside "busy" (01:00 to 02:00); "next" starts at 02:00, edge to edge.
        Assert.Equal(30, run.OverlapMinutes);
        var overlap = Assert.Single(preview.Overlaps);
        Assert.Equal((busy, "busy", 30, 1), (overlap.PipelineId, overlap.PipelineName, overlap.OverlapMinutes, overlap.OverlappingRuns));
        Assert.DoesNotContain(next, preview.Overlaps.Select(o => o.PipelineId));
        Assert.NotNull(proposal.NextRunUtc);

        Assert.Equal(before, await ReadStateAsync(factory, moved));
    }

    // R-39: the preview follows the plan's capacity, so a proposal beside one other run is no overlap with two runners.
    [Fact]
    public async Task Preview_WithRoomForTwoRunners_DoesNotListARunBesideOneOther_ButListsItBesideTwo()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "capacity");
        for (var runner = 0; runner < 2; runner++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var name = $"preview-runner-{Guid.NewGuid():N}";
            db.Servers.Add(new Server { Name = name, Hostname = name, Status = ServerStatus.Online, PipelineRunnerEnabled = true, LastHeartbeat = DateTime.UtcNow, OrganizationId = organization });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var moved = await SeedPipelineAsync(factory, project, "moved", At(3));
        await SeedPipelineAsync(factory, project, "busy-a", At(1));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var beside = await ReadPreviewAsync(admin, Url(moved));
        Assert.Empty(beside.Overlaps);
        Assert.Equal(0, Assert.Single(beside.Proposal.Occurrences).OverlapMinutes);

        await SeedPipelineAsync(factory, project, "busy-b", At(1));
        var crowded = await ReadPreviewAsync(admin, Url(moved));
        Assert.Equal(["busy-a", "busy-b"], crowded.Overlaps.Select(overlap => overlap.PipelineName).Order(StringComparer.Ordinal));
        Assert.Equal(30, Assert.Single(crowded.Proposal.Occurrences).OverlapMinutes);
    }

    [Fact]
    public async Task UserOfAnotherOrganization_IsRefused_AndLearnsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organizationA, projectA) = await SeedOrganizationAsync(factory, "a");
        var (_, projectB) = await SeedOrganizationAsync(factory, "b");
        var ownA = await SeedPipelineAsync(factory, projectA, "own-a", At(1));
        var inB = await SeedPipelineAsync(factory, projectB, "secret-b", At(1));
        var userA = await IntegrationUsers.CreateAsync(factory.Services, "prev-a",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organizationA);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userA.Username);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(ownA), ct)).StatusCode);
        var foreign = await client.GetAsync(Url(inB), ct);

        // The read permission is a wildcard here: only the organization of the pipeline refuses.
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain("secret-b", await foreign.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserWithoutReadOnThePipeline_IsRefused_AndHiddenPipelinesStayInvisible()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "read");
        var readable = await SeedPipelineAsync(factory, project, "readable", At(3));
        var hidden = await SeedPipelineAsync(factory, project, "hidden-neighbour", At(1));
        var named = await IntegrationUsers.CreateAsync(factory.Services, "prev-named",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, readable, Permission.Read)], organization);
        var wildcard = await IntegrationUsers.CreateAsync(factory.Services, "prev-wild",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organization);
        var none = await IntegrationUsers.CreateAsync(factory.Services, "prev-none", [], organization);
        using var namedClient = await IntegrationUsers.LoginClientAsync(factory, named.Username);
        using var wildcardClient = await IntegrationUsers.LoginClientAsync(factory, wildcard.Username);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, none.Username);

        var limited = await ReadPreviewAsync(namedClient, Url(readable));
        Assert.Empty(limited.Overlaps);
        Assert.Equal(0, Assert.Single(limited.Proposal.Occurrences).OverlapMinutes);
        Assert.Equal(HttpStatusCode.Forbidden, (await namedClient.GetAsync(Url(hidden), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync(Url(readable), ct)).StatusCode);

        // The positive control: for a caller who reads both, the same proposal does collide.
        var both = await ReadPreviewAsync(wildcardClient, Url(readable));
        Assert.Equal(hidden, Assert.Single(both.Overlaps).PipelineId);
        Assert.Equal(30, both.Proposal.Occurrences[0].OverlapMinutes);
    }

    [Fact]
    public async Task PipelineWithNoOwner_IsOnlyForTheSiteAdmin_AndFindsNoOverlap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, _) = await SeedOrganizationAsync(factory, "orphan");
        var orphan = await SeedPipelineAsync(factory, null, "orphan", At(1));
        var user = await IntegrationUsers.CreateAsync(factory.Services, "prev-orphan",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organization);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url(orphan), ct)).StatusCode);
        var preview = await ReadPreviewAsync(admin, Url(orphan));

        Assert.Empty(preview.Overlaps);
        Assert.Single(preview.Proposal.Occurrences);
    }

    [Fact]
    public async Task InvalidRequests_AreRefused_NeverA500()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (_, project) = await SeedOrganizationAsync(factory, "invalid");
        var pipeline = await SeedPipelineAsync(factory, project, "target", At(1));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var window = $"&from={Day:yyyy-MM-ddTHH:mm:ssZ}&to={Day.AddDays(1):yyyy-MM-ddTHH:mm:ssZ}";
        string[] refused =
        [
            Url(pipeline, "Custom"),
            Url(pipeline, "EveryEvening", 24),
            Url(pipeline, "EveryEvening", 1, 60),
            Url(pipeline, "EveryEvening", -1),
            Url(pipeline, "Weekly"),
            Url(pipeline, "Weekly", day: 7),
            Url(pipeline, "Monthly", day: 29),
            Url(pipeline, "Nonsense"),
            Url(pipeline, "99"),
            Url(pipeline, from: Day, to: Day),
            Url(pipeline, from: Day, to: Day.AddDays(32)),
            Url(pipeline, from: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: new DateTime(1, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            $"/api/pipelines/{pipeline}/schedule-preview?hour=1&minute=0{window}",
            $"/api/pipelines/{pipeline}/schedule-preview?frequency=EveryEvening&minute=0{window}",
            $"/api/pipelines/{pipeline}/schedule-preview?frequency=EveryEvening&hour=1&minute=0"
        ];

        foreach (var url in refused)
        {
            var response = await admin.GetAsync(url, ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{url} gave {response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url(pipeline + 100_000), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Url(pipeline), ct)).StatusCode);
    }

    private static async Task<SchedulePreviewDto> ReadPreviewAsync(HttpClient client, string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync(url, ct);
        Assert.True(response.IsSuccessStatusCode, $"{url} gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadFromJsonAsync<SchedulePreviewDto>(IntegrationJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The preview came back empty.");
    }

    private static async Task<(string Yaml, PipelineTriggerType Trigger, DateTime UpdatedAt, int Count)> ReadStateAsync(
        AetheusWebApplicationFactory factory, int pipelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == pipelineId, TestContext.Current.CancellationToken);
        return (row.YamlDefinition, row.TriggerType, row.UpdatedAt, await db.Pipelines.CountAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(int OrganizationId, int ProjectId)> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"prev-{label}-{Guid.NewGuid():N}", Slug = $"v{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"prev-{label}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return (organization.Id, project.Id);
    }

    private static async Task<int> SeedPipelineAsync(
        AetheusWebApplicationFactory factory, int? projectId, string name, string? cron = null,
        PipelineTriggerType trigger = PipelineTriggerType.Schedule, string? yaml = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new Pipeline
        {
            ProjectId = projectId,
            Name = name,
            TriggerType = trigger,
            YamlDefinition = yaml ?? $"name: {name}\ntrigger: {trigger.ToString().ToLowerInvariant()}\nschedule: \"{cron}\"\nstages: []\n"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private static async Task SeedRunsAsync(AetheusWebApplicationFactory factory, int pipelineId, int[] minutes)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < minutes.Length; i++)
        {
            var start = Day.AddDays(-10 + i);
            db.PipelineRuns.Add(new PipelineRun
            {
                PipelineId = pipelineId,
                Status = PipelineStatus.Success,
                StartedAt = start,
                CompletedAt = start.AddMinutes(minutes[i])
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
