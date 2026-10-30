// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-2 (ex-R-97, ADR-058): the server side of a project deletion on the real host and PostgreSQL. The
/// preview lists what the project deployed on its servers, the agent's discovery and removal go through the real
/// endpoints and task rows, the removal summary is read back, and the project goes only once every server is proved.
/// Only the agent is played by the test: it writes the lines a real agent prints into the task log (the removal on a
/// real host is proved on the vpssim stack, not here).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectDeletionServerResourcesHttpIntegrationTests(PostgresFixture fixture)
{
    private const string Capabilities = "{\"software\":[\"project.resources\",\"apache.manage\",\"certbot.manage\"]}";

    private const string ComposeProject = "hebergeo-prod";
    private const string StateDirectory = "/var/lib/aetheus-agent/projects/hebergeo-prod";
    private const string DeploymentDirectory = "/var/lib/aetheus-agent/.deployments/hebergeo-prod";
    private const string ApiHost = "api.hebergeo.test";
    private const string SharedHost = "app.hebergeo.test";

    [Fact]
    public async Task Preview_ListsTheServersOfTheProject_AndKeepsAHostAnotherProjectNamesOutOfTheRemoval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory, ct);
        using var client = await AdminClientAsync(factory, ct);

        var preview = await client.GetFromJsonAsync<ProjectDeletionPreviewDto>(
            $"/api/projects/{seeded.ProjectId}/deletion-preview", IntegrationJsonOptions.Default, ct);

        Assert.NotNull(preview);
        // What Aetheus holds is counted (the project's library) ...
        Assert.Contains(preview.Items, item => item.Kind == ProjectDeletionItemKind.VariableLibraries && item.Count == 1);
        // ... and what the servers hold is named: the online one is ready, the offline one is announced as such.
        Assert.Equal(2, preview.Servers.Count);
        var ready = preview.Servers.Single(server => server.ServerId == seeded.OnlineServerId);
        Assert.Equal(ProjectDeletionServerState.Ready, ready.State);
        var environment = Assert.Single(ready.Environments);
        Assert.Equal("prod", environment.Name);
        Assert.Equal(ComposeProject, environment.ComposeProject);
        Assert.Equal(StateDirectory, environment.StateDirectory);
        // R-85: the repository copy the deployments leave is announced with the path the templates write.
        Assert.Equal(DeploymentDirectory, environment.DeploymentDirectory);
        Assert.Equal([ApiHost], environment.Hosts);
        Assert.Equal(ProjectDeletionServerState.Offline, preview.Servers.Single(server => server.ServerId == seeded.OfflineServerId).State);
        // A host the other project also names is never used, and the user is told.
        var refused = Assert.Single(preview.RefusedValues);
        Assert.Equal(("APP_HOST", SharedHost, ProjectDeletionRefusal.SharedWithAnotherProject), (refused.Key, refused.Value, refused.Reason));
    }

    [Fact]
    public async Task Deletion_FindsRemovesAndReportsServerResources_AndWaitsForAnOfflineServerBeforeDeletingTheProject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory, ct);
        using var client = await AdminClientAsync(factory, ct);
        var project = $"/api/projects/{seeded.ProjectId}";

        // 1. Discovery: only the online server is asked; the agent finds the project's resources, and a container of
        //    another Compose project that is not the project's.
        var discoveries = await PostAsync<List<ProjectServerResourceTaskDto>>(client, $"{project}/deletion/discovery", null, ct);
        var discovery = Assert.Single(discoveries);
        Assert.Equal(seeded.OnlineServerId, discovery.ServerId);
        await AgentPrintsAsync(factory, discovery.TaskId, TaskExecutionStatus.Success,
        [
            Found(ProjectServerResourceKind.Container, "hebergeo-prod-web-1", ComposeProject),
            Found(ProjectServerResourceKind.Volume, "hebergeo-prod_pgdata", ComposeProject),
            Found(ProjectServerResourceKind.Network, "hebergeo-prod_default", ComposeProject),
            Found(ProjectServerResourceKind.VirtualHost, $"{ApiHost}.conf", ApiHost),
            Found(ProjectServerResourceKind.Certificate, ApiHost, ApiHost),
            Found(ProjectServerResourceKind.StateDirectory, StateDirectory, StateDirectory),
            Found(ProjectServerResourceKind.DeploymentDirectory, DeploymentDirectory, ComposeProject),
            Found(ProjectServerResourceKind.Container, "other-cache-1", "other"),
        ], ct);

        var listed = Assert.Single((await client.GetFromJsonAsync<List<ProjectServerResourceTaskDto>>(
            $"{project}/deletion/tasks?taskIds={discovery.TaskId}", IntegrationJsonOptions.Default, ct))!);
        Assert.Equal(ProjectServerTaskState.Succeeded, listed.State);
        Assert.Equal(8, listed.Resources.Count);
        Assert.All(listed.Resources, resource => Assert.Equal(ProjectServerResourceOutcome.Found, resource.Outcome));

        // 2. The volumes hold the project's data: removing without the explicit acknowledgement is refused, nothing queued.
        var refusal = await client.PostAsJsonAsync($"{project}/deletion/cleanup",
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [discovery.TaskId] }, IntegrationJsonOptions.Default, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Equal(0, await CountTasksAsync(factory, OperationKind.ProjectResourcesRemove, ct));

        // 3. With the acknowledgement, the removal list is exactly what the project owns: not the other project's
        //    container, not the shared host.
        var removals = await PostAsync<List<ProjectServerResourceTaskDto>>(client, $"{project}/deletion/cleanup",
            new ProjectServerCleanupRequest { DiscoveryTaskIds = [discovery.TaskId], AcknowledgeVolumeDataLoss = true }, ct);
        var removal = Assert.Single(removals);
        var asked = await RemovalSpecAsync(factory, removal.TaskId, ct);
        Assert.Equal(7, asked.Remove.Count);
        Assert.DoesNotContain(asked.Remove, item => item.Name == "other-cache-1");
        Assert.DoesNotContain(asked.Remove, item => item.Scope == SharedHost || item.Name.Contains(SharedHost, StringComparison.Ordinal));
        Assert.Equal([ApiHost], asked.Hosts);

        // 4. The agent removes everything and says so, resource by resource: the summary reads it back.
        await AgentPrintsAsync(factory, removal.TaskId, TaskExecutionStatus.Success,
            [.. asked.Remove.Select(item => Line(ProjectServerResourceOutcome.Removed, item))], ct);
        var summary = Assert.Single((await client.GetFromJsonAsync<List<ProjectServerResourceTaskDto>>(
            $"{project}/deletion/tasks?taskIds={removal.TaskId}", IntegrationJsonOptions.Default, ct))!);
        Assert.Equal(ProjectServerTaskState.Succeeded, summary.State);
        Assert.Equal(7, summary.Resources.Count);
        Assert.All(summary.Resources, resource => Assert.Equal(ProjectServerResourceOutcome.Removed, resource.Outcome));
        Assert.All(summary.Resources, resource => Assert.Equal("prod", resource.Environment));

        // 5. The offline server cannot be proved clean: nothing is half deleted, the project waits for its cleanup (202).
        var waiting = await client.DeleteAsync($"{project}?serverTaskIds={discovery.TaskId}&serverTaskIds={removal.TaskId}", ct);
        Assert.Equal(HttpStatusCode.Accepted, waiting.StatusCode);
        Assert.True(await ProjectExistsAsync(factory, seeded.ProjectId, ct));
        Assert.True(await LibraryExistsAsync(factory, seeded.LibraryId, ct));
        var pending = await client.GetFromJsonAsync<ProjectDeletionPreviewDto>(
            $"{project}/deletion-preview", IntegrationJsonOptions.Default, ct);
        Assert.NotNull(pending!.DeletionRequestedAt);

        // 6. The server comes back: a relaunched discovery asks both servers, finds nothing left, and the project goes (204).
        await SetOnlineAsync(factory, seeded.OfflineServerId, ct);
        var again = await PostAsync<List<ProjectServerResourceTaskDto>>(client, $"{project}/deletion/discovery", null, ct);
        Assert.Equal(2, again.Count);
        foreach (var task in again)
            await AgentPrintsAsync(factory, task.TaskId, TaskExecutionStatus.Success, [], ct);
        var ids = string.Join("&", again.Select(task => $"serverTaskIds={task.TaskId}"));
        var deleted = await client.DeleteAsync($"{project}?{ids}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.False(await ProjectExistsAsync(factory, seeded.ProjectId, ct));
        Assert.False(await LibraryExistsAsync(factory, seeded.LibraryId, ct));
        // The other project and the shared servers are untouched.
        Assert.True(await ProjectExistsAsync(factory, seeded.OtherProjectId, ct));
        Assert.True(await LibraryExistsAsync(factory, seeded.OtherLibraryId, ct));
        Assert.Equal(2, await CountServersAsync(factory, [seeded.OnlineServerId, seeded.OfflineServerId], ct));
    }

    private static string Line(ProjectServerResourceOutcome outcome, ProjectServerResourceRef item) =>
        ProjectServerResourceOutput.Line(outcome, item);

    private static string Found(ProjectServerResourceKind kind, string name, string scope) =>
        Line(ProjectServerResourceOutcome.Found, new ProjectServerResourceRef(kind, name, scope));

    private static async Task<HttpClient> AdminClientAsync(AetheusWebApplicationFactory factory, CancellationToken ct)
    {
        var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        return client;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object? body, CancellationToken ct)
    {
        var response = body is null
            ? await client.PostAsync(url, null, ct)
            : await client.PostAsJsonAsync(url, body, IntegrationJsonOptions.Default, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(IntegrationJsonOptions.Default, ct))!;
    }

    /// <summary>Plays the agent of a task: its resource lines, its closing line, and its end status.</summary>
    private static async Task AgentPrintsAsync(
        AetheusWebApplicationFactory factory, int taskId, TaskExecutionStatus status, IEnumerable<string> lines, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TaskLogs.AddRange(lines.Append(ProjectServerResourceOutput.CompleteLine)
            .Select(message => new TaskLog { TaskId = taskId, Message = message }));
        (await db.Tasks.SingleAsync(task => task.Id == taskId, ct)).Status = status;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<ProjectServerResourceSpec> RemovalSpecAsync(AetheusWebApplicationFactory factory, int taskId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId, ct);
        Assert.Equal(OperationKind.ProjectResourcesRemove, task.Operation);
        var variables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables)!;
        var spec = ProjectServerResourceSpec.Decode(variables[ProjectServerResourceSpec.EnvironmentVariable], out var refusal);
        Assert.True(spec is not null, refusal);
        return spec;
    }

    private static async Task<int> CountTasksAsync(AetheusWebApplicationFactory factory, OperationKind kind, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.CountAsync(task => task.Operation == kind, ct);
    }

    private static async Task<bool> ProjectExistsAsync(AetheusWebApplicationFactory factory, int projectId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.AnyAsync(project => project.Id == projectId, ct);
    }

    private static async Task<bool> LibraryExistsAsync(AetheusWebApplicationFactory factory, int libraryId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().VariableLibraries.AnyAsync(library => library.Id == libraryId, ct);
    }

    private static async Task<int> CountServersAsync(AetheusWebApplicationFactory factory, int[] ids, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Servers.CountAsync(server => ids.Contains(server.Id), ct);
    }

    private static async Task SetOnlineAsync(AetheusWebApplicationFactory factory, int serverId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await db.Servers.SingleAsync(item => item.Id == serverId, ct);
        server.Status = ServerStatus.Online;
        server.AgentCapabilitiesJson = Capabilities;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The project "Hebergeo" with its production library, an environment on two servers (one online with an
    /// agent able to read and remove, one offline), and another project whose library names the same application host.</summary>
    private static async Task<Seeded> SeedAsync(AetheusWebApplicationFactory factory, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Where(item => item.Slug == IntegrationUsers.DefaultOrganizationSlug)
            .Select(item => item.Id).SingleAsync(ct);
        var online = new Server { Name = "node-online", Hostname = "node-online", OrganizationId = organizationId, Status = ServerStatus.Online, AgentCapabilitiesJson = Capabilities };
        var offline = new Server { Name = "node-offline", Hostname = "node-offline", OrganizationId = organizationId, Status = ServerStatus.Offline };
        var project = new Project { Name = "Hebergeo", Description = "d", OrganizationId = organizationId };
        var other = new Project { Name = "Autre", Description = "d", OrganizationId = organizationId };
        db.AddRange(online, offline, project, other);
        await db.SaveChangesAsync(ct);

        var environment = new Environment { Name = "prod", ProjectId = project.Id };
        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
        db.EnvironmentServers.AddRange(
            new EnvironmentServer { EnvironmentId = environment.Id, ServerId = online.Id },
            new EnvironmentServer { EnvironmentId = environment.Id, ServerId = offline.Id });

        var library = Library("hebergeo.prod", project.Id,
            ("COMPOSE_PROJECT", ComposeProject), ("STATE_DIR", StateDirectory), ("APP_HOST", SharedHost), ("API_HOST", ApiHost));
        var otherLibrary = Library("autre.prod", other.Id, ("APP_HOST", SharedHost));
        db.VariableLibraries.AddRange(library, otherLibrary);
        await db.SaveChangesAsync(ct);
        return new Seeded(project.Id, other.Id, online.Id, offline.Id, library.Id, otherLibrary.Id);
    }

    private static VariableLibrary Library(string name, int projectId, params (string Key, string Value)[] entries) => new()
    {
        Name = name,
        Description = "d",
        ProjectId = projectId,
        Entries = [.. entries.Select(entry => new VariableLibraryEntry { Key = entry.Key, Value = entry.Value })],
    };

    private sealed record Seeded(int ProjectId, int OtherProjectId, int OnlineServerId, int OfflineServerId, int LibraryId, int OtherLibraryId);
}
