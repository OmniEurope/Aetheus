// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-2: the system log held 20 <c>Microsoft.EntityFrameworkCore.Query</c> warnings (17 "First without OrderBy", 3
/// "Include of several collections without a split"), and the line names no query. Every integration host now throws these
/// warnings (see <see cref="QueryShapeWarningLog"/>) and fails when it is disposed if one was raised and swallowed; these two
/// tests walk the paths that raised them (the launch of a run, the administration reads) on the real host and PostgreSQL.
/// </summary>
public sealed class PipelineRunLaunchQueryWarningsIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The whole life of a run over HTTP, with a real repository on disk: the launch dialog's parameters, the
    /// preflight, the launch, the runner completing every task, the completion handlers (the refused follow-up, the
    /// result recorded for a subscriber).</summary>
    [Fact]
    public async Task LaunchingAndCompletingARun_RaisesNoQueryShapeWarning()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, Ct));
        var seeded = await PipelineLaunchScenario.SeedAsync(factory, client, followUp: true);
        var subscriberId = await PipelineLaunchScenario.SubscribeAdministratorAsync(factory, seeded.ProjectId);

        var since = DateTime.UtcNow;
        var request = new PipelineRunRequest { Parameters = new Dictionary<string, string> { ["target"] = "staging" } };
        var parameters = await client.GetAsync($"/api/pipelines/{seeded.PipelineId}/parameters", Ct);
        var preflight = await client.PostAsJsonAsync($"/api/pipelines/{seeded.PipelineId}/preflight", request, IntegrationJsonOptions.Default, Ct);
        var run = await client.PostAsJsonAsync($"/api/pipelines/{seeded.PipelineId}/run", request, IntegrationJsonOptions.Default, Ct);

        Assert.Equal(HttpStatusCode.OK, parameters.StatusCode);
        Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        var runId = (await run.Content.ReadFromJsonAsync<PipelineRunDto>(IntegrationJsonOptions.Default, Ct))!.Id;
        await PipelineLaunchScenario.CompleteRunAsync(factory, runId);
        await PipelineLaunchScenario.WaitForTheRunResultNotificationAsync(factory, subscriberId, since);

    }

    /// <summary>The administration reads the system log pointed at: the user list, the storage page of the data
    /// that can be reclaimed, and the copy of an environment.</summary>
    [Fact]
    public async Task AdministrationReads_RaiseNoQueryShapeWarning()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, Ct));
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var environment = await PipelineLaunchScenario.PostAsync<EnvironmentDto>(client, "/api/environments",
            new CreateEnvironmentRequest { Name = $"copied-{suffix}" });

        var users = await client.GetAsync("/api/users?page=1&pageSize=20", Ct);
        var reclaim = await client.GetAsync("/api/data-storage/reclaim", Ct);
        var duplicate = await client.PostAsJsonAsync($"/api/environments/{environment.Id}/duplicate",
            new DuplicateEnvironmentRequest(), IntegrationJsonOptions.Default, Ct);

        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
    }
}
