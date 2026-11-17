// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// A pipeline as production has it, for the suites that launch a run over HTTP: a project of the default organization
/// with its own internal repository on disk (the pipeline is saved through the API, which commits its definition), and
/// one online runner. With a follow-up, the pipeline names an <c>on_success</c> pipeline that needs a parameter nothing
/// supplies, so its launch is refused after the upstream run succeeded (the refusal is recorded as a warning of the
/// upstream run and notified, as in production).
/// </summary>
internal static class PipelineLaunchScenario
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed record Seeded(int OrganizationId, int ProjectId, int PipelineId);

    internal static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory, HttpClient client, bool followUp)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        int organizationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            organizationId = await db.Organizations.Where(o => o.Slug == IntegrationUsers.DefaultOrganizationSlug)
                .Select(o => o.Id).FirstAsync(Ct);
            db.Servers.Add(new Server
            {
                Name = $"launch-runner-{suffix}",
                Hostname = $"launch-runner-{suffix}",
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                LastHeartbeat = DateTime.UtcNow,
                OrganizationId = organizationId,
                AgentProtocolVersion = AgentProtocol.CurrentVersion,
                AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
            });
            await db.SaveChangesAsync(Ct);
        }

        var project = await PostAsync<ProjectDto>(client, "/api/projects", new CreateProjectRequest
        {
            Name = $"launch-{suffix}",
            Description = "a pipeline launched over HTTP",
            OrganizationId = organizationId
        });
        await PostAsync<GitLightRepoDto>(client, "/api/git/repos", new CreateGitLightRepoRequest
        {
            ProjectId = project.Id,
            Name = $"launch-repo-{suffix}",
            DefaultBranch = "main"
        });
        var followUpName = $"launch-follow-up-{suffix}";
        if (followUp)
            await SavePipelineAsync(client, project.Id, followUpName, $$"""
                name: {{followUpName}}
                trigger: manual
                parameters:
                  - name: deployServer
                    type: string
                    required: true
                stages:
                  - name: Deploy
                    agent: launch-runner-{{suffix}}
                    steps:
                      - name: Ship
                        shell: echo ship $(deployServer)
                """);
        var name = $"launch-ci-{suffix}";
        var pipeline = await SavePipelineAsync(client, project.Id, name, $$"""
            name: {{name}}
            trigger: manual
            parameters:
              - name: target
                type: string
                required: true
            {{(followUp ? $"on_success:\n  - pipeline: {followUpName}" : string.Empty)}}
            stages:
              - name: Build
                agent: launch-runner-{{suffix}}
                steps:
                  - name: Compile
                    shell: echo build
              - name: Deploy
                agent: launch-runner-{{suffix}}
                steps:
                  - name: Ship
                    shell: echo ship $(target)
            """);
        return new Seeded(organizationId, project.Id, pipeline.Id);
    }

    private static Task<PipelineDto> SavePipelineAsync(HttpClient client, int projectId, string name, string yaml) =>
        PostAsync<PipelineDto>(client, "/api/pipelines", new CreatePipelineRequest
        {
            Name = name,
            Description = "saved through the API, committed to the project's repository",
            ProjectId = projectId,
            SourceBranch = "main",
            YamlDefinition = yaml
        });

    internal static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(IntegrationJsonOptions.Default, Ct));
    }

    /// <summary>Makes the user a subscriber to the project's outcomes, and a recipient of a run's success, which is
    /// recorded only for a user who asked for it (the refused follow-up is recorded by default).</summary>
    internal static async Task<int> SubscribeAdministratorAsync(WebApplicationFactory<Program> factory, int projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(user => user.Email == IntegrationAuth.AdminEmail).Select(user => user.Id).SingleAsync(Ct);
        // The creator of a project is already subscribed to it.
        if (!await db.ProjectSubscriptions.AnyAsync(subscription => subscription.UserId == userId && subscription.ProjectId == projectId, Ct))
            db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = userId, ProjectId = projectId, CreatedAt = DateTime.UtcNow });
        var preference = await db.UserNotificationPreferences.SingleOrDefaultAsync(
            existing => existing.UserId == userId && existing.EventType == NotificationEventTypes.PipelineSucceeded, Ct);
        if (preference is null)
            db.UserNotificationPreferences.Add(new UserNotificationPreference
            {
                UserId = userId,
                EventType = NotificationEventTypes.PipelineSucceeded,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        else
        {
            preference.IsEnabled = true;
            preference.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(Ct);
        return userId;
    }

    /// <summary>Plays the runner: starts and completes, as successes, the tasks the run has dispatched, until the run
    /// ends (each completed stage dispatches the next through the real engine).</summary>
    internal static async Task CompleteRunAsync(WebApplicationFactory<Program> factory, int runId)
    {
        for (var round = 0; round < 40; round++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await db.PipelineRuns.AsNoTracking().Where(run => run.Id == runId).Select(run => run.Status).SingleAsync(Ct);
            if (status is PipelineStatus.Success or PipelineStatus.Failed) break;
            var taskIds = await db.Tasks.AsNoTracking()
                .Where(task => task.PipelineRunId == runId
                    && (task.Status == TaskExecutionStatus.Pending || task.Status == TaskExecutionStatus.Assigned))
                .Select(task => task.Id).ToListAsync(Ct);
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            foreach (var taskId in taskIds)
            {
                await taskService.StartTaskAsync(taskId, Ct);
                await taskService.CompleteTaskAsync(taskId, new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 }, Ct);
            }

            if (taskIds.Count == 0) await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        await using var last = factory.Services.CreateAsyncScope();
        var finalStatus = await last.ServiceProvider.GetRequiredService<AppDbContext>().PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId).Select(run => run.Status).SingleAsync(Ct);
        Assert.Equal(PipelineStatus.Success, finalStatus);
    }

    /// <summary>The run's result is recorded for its subscribers on the background queue, after the completion:
    /// waits (bounded) until a delivery created since <paramref name="since"/> exists, so that work is part of what a
    /// test watches.</summary>
    internal static async Task WaitForTheRunResultNotificationAsync(WebApplicationFactory<Program> factory, int userId, DateTime since)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.NotificationDeliveries.AsNoTracking().AnyAsync(delivery =>
                    delivery.RecipientUserId == userId && delivery.CreatedAt >= since
                    && delivery.EventType == NotificationEventTypes.PipelineSucceeded, Ct))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        Assert.Fail("The run's result was never recorded for its subscriber, so the completion handlers did not all run.");
    }
}
