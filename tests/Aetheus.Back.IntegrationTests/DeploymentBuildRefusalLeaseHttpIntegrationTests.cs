// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-004: <c>POST api/tasks/{id}/deployment-build-refusal</c> closes the task, so it passes the same
/// session ownership check as <c>POST api/tasks/{id}/complete</c>. Real HTTP with a real agent token, on real
/// PostgreSQL: a report without a lease is refused (400), a report under a superseded lease is refused (409)
/// and leaves the task as it was, a report under the live lease fails the task, and a replay stays idempotent.
/// The lease is seeded on the rows directly, as the claim would leave it; the claim itself has its own tests.
/// </summary>
public sealed class DeploymentBuildRefusalLeaseHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string LiveSession = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StaleSession = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const long LiveToken = 7;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refusal_WithoutLease_IsRefused_AndTheTaskStaysAssigned()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory, "nolease");
        using var agent = AgentClient(factory, seeded.Token);

        using var response = await ReportAsync(agent, seeded.TaskId, sessionId: null, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TaskExecutionStatus.Assigned, (await ReadTaskAsync(factory, seeded.TaskId)).Status);
    }

    [Fact]
    public async Task Refusal_UnderASupersededLease_IsRefused_AndTheTaskStaysAssigned()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory, "stale");
        using var agent = AgentClient(factory, seeded.Token);

        using var staleSession = await ReportAsync(agent, seeded.TaskId, StaleSession, LiveToken);
        using var staleToken = await ReportAsync(agent, seeded.TaskId, LiveSession, LiveToken - 1);

        Assert.Equal(HttpStatusCode.Conflict, staleSession.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleToken.StatusCode);
        var task = await ReadTaskAsync(factory, seeded.TaskId);
        Assert.Equal(TaskExecutionStatus.Assigned, task.Status);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public async Task Refusal_UnderTheLiveLease_FailsTheTask_AndAReplayIsIdempotent()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory, "live");
        using var agent = AgentClient(factory, seeded.Token);

        using var first = await ReportAsync(agent, seeded.TaskId, LiveSession, LiveToken);
        using var replay = await ReportAsync(agent, seeded.TaskId, LiveSession, LiveToken);

        Assert.True(first.StatusCode == HttpStatusCode.OK, $"Refusal gave {first.StatusCode}: {await first.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var task = await ReadTaskAsync(factory, seeded.TaskId);
        Assert.Equal(TaskExecutionStatus.Failed, task.Status);
        Assert.Equal(-1, task.ExitCode);
        Assert.NotNull(task.CompletedAt);
    }

    private sealed record Seeded(int ServerId, int TaskId, string Token);

    private static HttpClient AgentClient(AetheusWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient agent, int taskId, string? sessionId, long? token) =>
        agent.PostAsJsonAsync($"/api/tasks/{taskId}/deployment-build-refusal", new DeploymentBuildRefusalReport
        {
            IncidentId = Guid.NewGuid(),
            TaskId = taskId,
            OccurredAtUtc = DateTime.UtcNow,
            Reason = "Pipeline task refused: a deployment-only agent accepts only stages explicitly classified as deploy.",
            AgentSessionId = sessionId,
            AgentSessionFencingToken = token
        }, IntegrationJsonOptions.Default, Ct);

    private static async Task<ServerTask> ReadTaskAsync(AetheusWebApplicationFactory factory, int taskId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking()
            .SingleAsync(task => task.Id == taskId, Ct);
    }

    private static async Task<Seeded> SeedAsync(AetheusWebApplicationFactory factory, string label)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"refusal-{label}-{Guid.NewGuid():N}", Slug = $"f{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);

        // A current agent: the temporary unfenced window (1.0.395 to 1.0.761) does not apply to it.
        var server = new Server
        {
            Name = $"refusal-{label}",
            Hostname = $"refusal-{label}.local",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online,
            AgentVersion = "1.0.900",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentSessionId = LiveSession,
            AgentSessionFencingToken = LiveToken,
            AgentSessionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);

        var task = new ServerTask
        {
            ServerId = server.Id,
            Name = $"build-on-deploy-target-{label}",
            Command = "dotnet build",
            Status = TaskExecutionStatus.Assigned,
            AssignedAgentSessionId = LiveSession,
            AssignedAgentSessionFencingToken = LiveToken
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(Ct);

        var token = (await scope.ServiceProvider.GetRequiredService<IAuthService>().RotateAgentTokenAsync(server.Id, ct: Ct))!.BearerToken;
        return new Seeded(server.Id, task.Id, token);
    }
}
