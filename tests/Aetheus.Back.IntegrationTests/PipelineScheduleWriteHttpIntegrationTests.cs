// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// <c>PUT api/pipelines/{id}/schedule</c> (PLAN-005 lot 2) through the real HTTP pipeline, JWT included, on
/// real PostgreSQL and a real bare Git repository on disk, read back with the real <c>git</c>. What is not
/// real: a repository change is written by the pipeline Git service, not pushed, and the engine test drives
/// the real scheduler on a manual clock with a double for the start of the run.
/// </summary>
public sealed class PipelineScheduleWriteHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string FirstCron = "0 0 1 * * *";

    private static readonly IntegrationUsers.Grant[] Writer =
    [
        new(ResourceType.Pipeline, null, Permission.Write),
        new(ResourceType.Pipeline, null, Permission.Read),
        new(ResourceType.Project, null, Permission.Write)
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(int id) => $"/api/pipelines/{id}/schedule";

    private static string Definition(string name, string runner, string trigger = "schedule", string cron = FirstCron, string command = "echo one") => $$"""
        # the nightly of this project
        name: {{name}}
        trigger: {{trigger}}
        schedule: "{{cron}}" # keep this comment
        stages:
          - name: Build
            agent: {{runner}}
            steps:
              - name: Compile
                shell: {{command}}

        """.ReplaceLineEndings("\n");

    private static UpdatePipelineScheduleRequest MoveTo(int hour, int minute, string expected = FirstCron, bool expectedEnabled = true) => new()
    {
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = hour,
        Minute = minute,
        ExpectedCronExpression = expected,
        ExpectedEnabled = expectedEnabled
    };

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, int id, UpdatePipelineScheduleRequest request) =>
        client.PutAsJsonAsync(Url(id), request, IntegrationJsonOptions.Default, Ct);

    private static async Task<PipelineDto> MoveAsync(HttpClient client, int id, UpdatePipelineScheduleRequest request)
    {
        using var response = await PutAsync(client, id, request);
        Assert.True(response.IsSuccessStatusCode, $"PUT gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, Ct)
            ?? throw new InvalidOperationException("The PUT came back empty.");
    }

    [Fact]
    public async Task Move_CommitsTheFile_AndTheMirrorAndTheTriggerFollow_AndOnlyTheValueChanges()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner, trigger: "manual");
        var id = await scene.CreatePipelineAsync(yaml);
        var head = scene.Git("rev-parse", "main");
        using var mover = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);

        var moved = await MoveAsync(mover, id, MoveTo(2, 45) with { Enabled = true, ExpectedEnabled = false });

        var expected = yaml
            .Replace("trigger: manual", "trigger: schedule", StringComparison.Ordinal)
            .Replace(FirstCron, "0 45 2 * * *", StringComparison.Ordinal);
        Assert.Equal(PipelineTriggerType.Schedule, moved.TriggerType);
        Assert.Equal(expected, scene.Git("show", $"main:.pipeline/{scene.Name}.yaml"));
        Assert.NotEqual(head, scene.Git("rev-parse", "main"));
        Assert.Equal(head, scene.Git("rev-parse", "main~1"));
        Assert.Contains($"chore(pipeline): update {scene.Name} via Aetheus", scene.Git("log", "-1", "--format=%B", "main"), StringComparison.Ordinal);
        Assert.Contains($"Requested-by: {scene.Launcher}", scene.Git("log", "-1", "--format=%B", "main"), StringComparison.Ordinal);
        Assert.Equal($".pipeline/{scene.Name}.yaml", scene.Git("show", "--format=", "--name-only", "main").Trim());
        var row = await ReadAsync(factory, id);
        Assert.Equal((expected, PipelineTriggerType.Schedule), (row.YamlDefinition, row.TriggerType));
        var audit = await ScheduleAuditAsync(factory, id);
        Assert.Equal(scene.Launcher, Assert.Single(audit).Username);
        Assert.Contains("0 45 2 * * *", audit[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeInTheRepositoryBetweenTheReadAndTheWrite_Is409_AndNothingIsWritten()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner);
        var id = await scene.CreatePipelineAsync(yaml);
        using var mover = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);

        // What the caller read is still the mirror: only the repository moved, and no sync came.
        var pushed = yaml.Replace("echo one", "echo pushed", StringComparison.Ordinal);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (outcome, error) = await scope.ServiceProvider.GetRequiredService<IPipelineGitService>()
                .WriteProjectPipelineYamlAsync(scene.ProjectId, scene.Name, pushed, "someone", Ct, "main");
            Assert.True(outcome == GitWriteOutcome.Committed, error);
        }

        var head = scene.Git("rev-parse", "main");
        var before = await ReadAsync(factory, id);

        using var response = await PutAsync(mover, id, MoveTo(2, 45));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(head, scene.Git("rev-parse", "main"));
        Assert.Equal(pushed, scene.Git("show", $"main:.pipeline/{scene.Name}.yaml"));
        var after = await ReadAsync(factory, id);
        Assert.Equal((before.YamlDefinition, before.TriggerType, before.CreatedByUsername), (after.YamlDefinition, after.TriggerType, after.CreatedByUsername));
        Assert.Empty(await ScheduleAuditAsync(factory, id));
    }

    [Fact]
    public async Task DisablingThenEnabling_KeepsTheValue_AndTheFileComesBackAsItWas()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner);
        var id = await scene.CreatePipelineAsync(yaml);
        using var mover = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);

        var disabled = await MoveAsync(mover, id, new UpdatePipelineScheduleRequest
        {
            Enabled = false,
            ExpectedCronExpression = FirstCron,
            ExpectedEnabled = true
        });

        Assert.Equal(PipelineTriggerType.Manual, disabled.TriggerType);
        Assert.Equal(
            yaml.Replace("trigger: schedule", "trigger: manual", StringComparison.Ordinal),
            scene.Git("show", $"main:.pipeline/{scene.Name}.yaml"));
        var plan = await mover.GetFromJsonAsync<SchedulePlanDto>(
            $"/api/pipelines/schedule-plan?organizationId={scene.OrganizationId}&from=2027-01-04T00:00:00Z&to=2027-01-05T00:00:00Z",
            IntegrationJsonOptions.Default, Ct);
        var task = Assert.Single(plan!.Tasks);
        Assert.Equal((false, FirstCron), (task.Enabled, task.CronExpression));

        using var launcher = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);
        var enabled = await MoveAsync(launcher, id, new UpdatePipelineScheduleRequest
        {
            Enabled = true,
            ExpectedCronExpression = task.CronExpression,
            ExpectedEnabled = task.Enabled
        });

        Assert.Equal(PipelineTriggerType.Schedule, enabled.TriggerType);
        Assert.Equal(yaml, scene.Git("show", $"main:.pipeline/{scene.Name}.yaml"));
        Assert.Equal(scene.Owner, (await ReadAsync(factory, id)).CreatedByUsername);
    }

    // The owner holds Server.Admin on the target, the mover does not: the real authorization of an automated
    // run tells whose rights it acts with, and a real edit shows the same check refusing the mover.
    [Fact]
    public async Task AfterAMove_ScheduledRunsActAsTheOwner_AfterAStepEdit_AsTheEditor()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner);
        var id = await scene.CreatePipelineAsync(yaml);
        using var mover = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);

        await MoveAsync(mover, id, MoveTo(2, 45));

        Assert.Equal(scene.Owner, (await ReadAsync(factory, id)).CreatedByUsername);
        Assert.NotNull(await PrepareScheduledRunAsync(factory, id));

        // The whole definition sent to the other route, with only the schedule line changed: same rule.
        var pipeline = await mover.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{id}", IntegrationJsonOptions.Default, Ct);
        using var scheduleOnly = await PutDefinitionAsync(mover, pipeline!, pipeline!.YamlDefinition.Replace("0 45 2", "0 50 2", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, scheduleOnly.StatusCode);
        Assert.Equal(scene.Owner, (await ReadAsync(factory, id)).CreatedByUsername);

        var edited = pipeline.YamlDefinition.Replace("0 45 2", "0 50 2", StringComparison.Ordinal);
        using var stepEdit = await PutDefinitionAsync(mover, pipeline, edited.Replace("echo one", "echo mine", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, stepEdit.StatusCode);
        Assert.Equal(scene.Mover, (await ReadAsync(factory, id)).CreatedByUsername);
        Assert.Null(await PrepareScheduledRunAsync(factory, id));
    }

    // Turning a schedule on starts runs nobody asked for at that moment: it takes the run right, on both routes.
    [Fact]
    public async Task TurningAScheduleOn_ThroughTheScheduleRoute_TakesTheRunRight()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner, trigger: "manual");
        var id = await scene.CreatePipelineAsync(yaml);
        var head = scene.Git("rev-parse", "main");
        using var withoutRun = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);
        using var withRun = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);
        var turnOn = new UpdatePipelineScheduleRequest { Enabled = true, ExpectedCronExpression = FirstCron, ExpectedEnabled = false };

        using var refused = await PutAsync(withoutRun, id, turnOn);
        using var refusedMove = await PutAsync(withoutRun, id, MoveTo(2, 45, expectedEnabled: false));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refusedMove.StatusCode);
        Assert.Equal(head, scene.Git("rev-parse", "main"));
        var row = await ReadAsync(factory, id);
        Assert.Equal((yaml, PipelineTriggerType.Manual), (row.YamlDefinition, row.TriggerType));
        Assert.Empty(await ScheduleAuditAsync(factory, id));

        Assert.Equal(PipelineTriggerType.Schedule, (await MoveAsync(withRun, id, turnOn)).TriggerType);
        Assert.Equal(scene.Owner, (await ReadAsync(factory, id)).CreatedByUsername);
    }

    [Fact]
    public async Task TurningAScheduleOn_ThroughTheDefinitionRoute_TakesTheRunRight()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner, trigger: "manual");
        var id = await scene.CreatePipelineAsync(yaml);
        var head = scene.Git("rev-parse", "main");
        using var withoutRun = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);
        using var withRun = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);
        var pipeline = await withoutRun.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{id}", IntegrationJsonOptions.Default, Ct);
        var turnedOn = yaml.Replace("trigger: manual", "trigger: schedule", StringComparison.Ordinal);

        using var refused = await PutDefinitionAsync(withoutRun, pipeline!, turnedOn);
        // A change of a step beside it moves the owner, so it is no way around the right.
        using var refusedWithEdit = await PutDefinitionAsync(
            withoutRun, pipeline!, turnedOn.Replace("echo one", "echo mine", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refusedWithEdit.StatusCode);
        Assert.Equal(head, scene.Git("rev-parse", "main"));
        var row = await ReadAsync(factory, id);
        Assert.Equal((yaml, PipelineTriggerType.Manual, scene.Owner), (row.YamlDefinition, row.TriggerType, row.CreatedByUsername));

        using var allowed = await PutDefinitionAsync(withRun, pipeline!, turnedOn);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var saved = await ReadAsync(factory, id);
        Assert.Equal((PipelineTriggerType.Schedule, scene.Owner), (saved.TriggerType, saved.CreatedByUsername));
    }

    // The third route that rewrites a definition: the YAML given with a template extraction.
    [Fact]
    public async Task TurningAScheduleOn_ThroughATemplateExtraction_TakesTheRunRight()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner, trigger: "manual");
        var id = await scene.CreatePipelineAsync(yaml);
        var head = scene.Git("rev-parse", "main");
        IntegrationUsers.Grant[] templateWriter = [.. Writer, new(ResourceType.PipelineTemplate, null, Permission.Write)];
        var plain = await IntegrationUsers.CreateAsync(factory.Services, "sched-tpl", templateWriter);
        var runner = await IntegrationUsers.CreateAsync(factory.Services, "sched-tpl-run", templateWriter, permissionKeys: [PermissionKeys.PipelineRun]);
        using var withoutRun = await IntegrationUsers.LoginClientAsync(factory, plain.Username);
        using var withRun = await IntegrationUsers.LoginClientAsync(factory, runner.Username);
        var turnedOn = yaml.Replace("trigger: manual", "trigger: schedule", StringComparison.Ordinal);
        var refusedName = $"tpl-refused-{scene.Name}";
        Task<HttpResponseMessage> ExtractAsync(HttpClient client, string name, bool rewrite = true) => client.PostAsJsonAsync(
            $"/api/pipelines/{id}/extract-template",
            new ExtractPipelineTemplateRequest { TemplateName = name, RewrittenPipelineYaml = turnedOn, RewritePipeline = rewrite },
            IntegrationJsonOptions.Default, Ct);

        using var refused = await ExtractAsync(withoutRun, refusedName);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(head, scene.Git("rev-parse", "main"));
        var row = await ReadAsync(factory, id);
        Assert.Equal((yaml, PipelineTriggerType.Manual, scene.Owner), (row.YamlDefinition, row.TriggerType, row.CreatedByUsername));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PipelineTemplates.AnyAsync(template => template.Name == refusedName, Ct));
        }

        // Without the rewrite the YAML given is not used: nothing is turned on, so nothing more is asked.
        using var notRewritten = await ExtractAsync(withoutRun, $"tpl-kept-{scene.Name}", rewrite: false);
        Assert.True(notRewritten.IsSuccessStatusCode, await notRewritten.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PipelineTriggerType.Manual, (await ReadAsync(factory, id)).TriggerType);

        using var allowed = await ExtractAsync(withRun, $"tpl-on-{scene.Name}");
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PipelineTriggerType.Schedule, (await ReadAsync(factory, id)).TriggerType);
    }

    [Fact]
    public async Task MovingAndDisablingAnEnabledSchedule_TakeWriteOnly_OnBothRoutes()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var id = await scene.CreatePipelineAsync(Definition(scene.Name, scene.Runner));
        using var withoutRun = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);

        await MoveAsync(withoutRun, id, MoveTo(2, 45));
        var pipeline = await withoutRun.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{id}", IntegrationJsonOptions.Default, Ct);
        using var moved = await PutDefinitionAsync(withoutRun, pipeline!, pipeline!.YamlDefinition.Replace("0 45 2", "0 50 2", StringComparison.Ordinal));
        var reread = await withoutRun.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{id}", IntegrationJsonOptions.Default, Ct);
        using var disabled = await PutDefinitionAsync(withoutRun, reread!, reread!.YamlDefinition.Replace("trigger: schedule", "trigger: manual", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(PipelineTriggerType.Manual, (await ReadAsync(factory, id)).TriggerType);

        // And off again through the schedule route, after the one with the right turned it back on.
        using var withRun = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);
        await MoveAsync(withRun, id, new UpdatePipelineScheduleRequest { Enabled = true, ExpectedCronExpression = "0 50 2 * * *", ExpectedEnabled = false });
        var off = await MoveAsync(withoutRun, id, new UpdatePipelineScheduleRequest { Enabled = false, ExpectedCronExpression = "0 50 2 * * *", ExpectedEnabled = true });
        Assert.Equal(PipelineTriggerType.Manual, off.TriggerType);
    }

    // The check reads the definition before the validation does: an unreadable one stays a 400, never a 500.
    [Fact]
    public async Task UnreadableDefinition_StaysA400_ForACallerWithoutTheRunRight()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var yaml = Definition(scene.Name, scene.Runner, trigger: "manual");
        var id = await scene.CreatePipelineAsync(yaml);
        using var withoutRun = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);
        var pipeline = await withoutRun.GetFromJsonAsync<PipelineDto>($"/api/pipelines/{id}", IntegrationJsonOptions.Default, Ct);

        using var twoDocuments = await PutDefinitionAsync(withoutRun, pipeline!, yaml + "---\nname: other\n");
        using var broken = await PutDefinitionAsync(withoutRun, pipeline!, "name: [unclosed\n");

        Assert.Equal(HttpStatusCode.BadRequest, twoDocuments.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Fact]
    public async Task UserOfAnotherOrganization_IsRefused_AndNothingIsWritten()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organizationA, _) = await SeedOrganizationAsync(factory, "a");
        var (_, projectB) = await SeedOrganizationAsync(factory, "b");
        var inB = await SeedPipelineAsync(factory, projectB, "secret-b");
        var before = await ReadAsync(factory, inB);
        var userA = await IntegrationUsers.CreateAsync(factory.Services, "write-a", Writer, organizationA);
        using var client = await IntegrationUsers.LoginClientAsync(factory, userA.Username);

        using var response = await PutAsync(client, inB, MoveTo(2, 45));

        // The grants are wildcards, held for organization A: they reach nothing of organization B.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("secret-b", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(before.YamlDefinition, (await ReadAsync(factory, inB)).YamlDefinition);
    }

    [Fact]
    public async Task UsersWithoutTheRightsOfTheDefinitionRoute_AreRefused_AndNothingIsWritten()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, project) = await SeedOrganizationAsync(factory, "rights");
        var id = await SeedPipelineAsync(factory, project, "guarded");
        var before = await ReadAsync(factory, id);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "sched-read",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read)], organization);
        var noOwner = await IntegrationUsers.CreateAsync(factory.Services, "sched-noowner",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Write)], organization);
        var writer = await IntegrationUsers.CreateAsync(factory.Services, "sched-write", Writer, organization);

        foreach (var username in new[] { reader.Username, noOwner.Username })
        {
            using var client = await IntegrationUsers.LoginClientAsync(factory, username);
            using var response = await PutAsync(client, id, MoveTo(2, 45));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{username} got {response.StatusCode}");
        }

        Assert.Equal(before.YamlDefinition, (await ReadAsync(factory, id)).YamlDefinition);

        // The positive control: the same request passes with the rights of the definition route.
        using var allowed = await IntegrationUsers.LoginClientAsync(factory, writer.Username);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(allowed, id, MoveTo(2, 45))).StatusCode);
    }

    [Fact]
    public async Task PipelineOfNoOrganization_IsOnlyForTheSiteAdmin()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (organization, _) = await SeedOrganizationAsync(factory, "orphan");
        var orphan = await SeedPipelineAsync(factory, null, "orphan");
        var user = await IntegrationUsers.CreateAsync(factory.Services, "sched-orphan", Writer, organization);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, orphan, MoveTo(2, 45))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, orphan, MoveTo(2, 45))).StatusCode);
    }

    [Fact]
    public async Task InvalidRequests_AreRefused_NeverA500_AndAMissingPipelineIs404_AndAnonymousIs401()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (_, project) = await SeedOrganizationAsync(factory, "invalid");
        var id = await SeedPipelineAsync(factory, project, "target");
        var webhook = await SeedPipelineAsync(factory, project, "hooked", yaml: $"name: hooked\ntrigger: webhook\nschedule: \"{FirstCron}\"\nstages: []\n", trigger: PipelineTriggerType.Webhook);
        var twice = await SeedPipelineAsync(factory, project, "twice", yaml: $"name: twice\ntrigger: schedule\nschedule: \"{FirstCron}\"\nschedule: \"{FirstCron}\"\nstages: []\n");
        var bare = await SeedPipelineAsync(factory, project, "bare", yaml: "name: bare\ntrigger: manual\nstages: []\n", trigger: PipelineTriggerType.Manual);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        (int Id, UpdatePipelineScheduleRequest Request)[] refused =
        [
            (id, MoveTo(2, 0) with { Frequency = ScheduleFrequency.Custom }),
            (id, MoveTo(24, 0)),
            (id, MoveTo(2, 60)),
            (id, MoveTo(-1, 0)),
            (id, MoveTo(2, 0) with { Hour = null }),
            (id, MoveTo(2, 0) with { Frequency = ScheduleFrequency.Weekly }),
            (id, MoveTo(2, 0) with { Frequency = ScheduleFrequency.Weekly, Day = 7 }),
            (id, MoveTo(2, 0) with { Frequency = ScheduleFrequency.Monthly, Day = 29 }),
            (webhook, MoveTo(2, 0) with { ExpectedEnabled = false }),
            (twice, MoveTo(2, 0)),
            (bare, new UpdatePipelineScheduleRequest { Enabled = true })
        ];

        foreach (var (target, request) in refused)
        {
            using var response = await PutAsync(admin, target, request);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{request} gave {response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, id + 100_000, MoveTo(2, 0))).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(anonymous, id, MoveTo(2, 0))).StatusCode);
    }

    // Point 13: the write goes through HTTP; the real engine, on a manual clock, then reads what it wrote.
    [Fact]
    public async Task AfterAMove_TheEngineFiresAtTheNewTime_AndNotAtTheOldOne()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var oldTime = now.AddHours(6);
        var newTime = now.AddHours(3);
        var oldCron = $"0 {oldTime.Minute} {oldTime.Hour} * * *";
        var (_, project) = await SeedOrganizationAsync(factory, "engine");
        var moved = await SeedPipelineAsync(factory, project, "moved", cron: oldCron);
        var control = await SeedPipelineAsync(factory, project, "control", cron: oldCron);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        using var harness = new PipelineSchedulerHarness(fixture.ConnectionString, now);
        await harness.TickAsync(Ct);

        await MoveAsync(admin, moved, MoveTo(newTime.Hour, newTime.Minute, oldCron));

        static DateTimeOffset Past(DateTimeOffset time) => new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, 10, TimeSpan.Zero);
        await harness.TickAtAsync(Past(newTime).AddMinutes(-1), Ct);
        Assert.Empty(harness.Triggered);
        await harness.TickAtAsync(Past(newTime), Ct);
        Assert.Equal([moved], harness.Triggered.Select(entry => entry.PipelineId));
        Assert.Equal(Past(newTime), harness.Triggered[0].At);

        await harness.TickAtAsync(Past(oldTime), Ct);
        await harness.NextMinuteAsync(Ct);
        Assert.Equal([moved, control], harness.Triggered.Select(entry => entry.PipelineId));
        Assert.Equal(Past(oldTime), harness.Triggered[1].At);
    }

    private static Task<HttpResponseMessage> PutDefinitionAsync(HttpClient client, PipelineDto pipeline, string yaml) =>
        client.PutAsJsonAsync($"/api/pipelines/{pipeline.Id}", new UpdatePipelineRequest
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = yaml,
            ProjectId = pipeline.ProjectId,
            SourceBranch = pipeline.SourceBranch
        }, IntegrationJsonOptions.Default, Ct);

    private static async Task<PipelineRunPreparation?> PrepareScheduledRunAsync(AetheusWebApplicationFactory factory, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPipelineRunService>().PrepareAutomatedRunAsync(id, "Scheduler", ct: Ct);
    }

    private static Task<HttpResponseMessage> PutBranchAsync(HttpClient client, int id, string branch, string? expected) =>
        client.PutAsJsonAsync($"/api/pipelines/{id}/source-branch",
            new UpdatePipelineSourceBranchRequest { Branch = branch, ExpectedBranch = expected }, IntegrationJsonOptions.Default, Ct);

    // Recette R-5 (user decision 2026-11-09): the planner's selector changes the pipeline's real source branch. On real PostgreSQL
    // and a real repository: the stored branch, the mirror adopted from that branch and the plan the scheduler's own reader draws
    // all follow it, and the other branch's file is left alone.
    [Fact]
    public async Task SourceBranch_IsStored_TheMirrorAdoptsThatBranchsSchedule_AndThePlanFollows()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        const string developCron = "0 30 3 * * *";
        var mainYaml = Definition(scene.Name, scene.Runner);
        var developYaml = Definition(scene.Name, scene.Runner, cron: developCron);
        var id = await scene.CreatePipelineAsync(mainYaml);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var (outcome, error) = await scope.ServiceProvider.GetRequiredService<IPipelineGitService>()
                .WriteProjectPipelineYamlAsync(scene.ProjectId, scene.Name, developYaml, "someone", Ct, "develop");
            Assert.True(outcome == GitWriteOutcome.Committed, error);
        }

        var mainHead = scene.Git("rev-parse", "main");
        using var launcher = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);

        using var response = await PutBranchAsync(launcher, id, "develop", "main");

        Assert.True(response.IsSuccessStatusCode, $"PUT gave {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var row = await ReadAsync(factory, id);
        Assert.Equal(("develop", developYaml), (row.SourceBranch, row.YamlDefinition));
        Assert.Equal(mainHead, scene.Git("rev-parse", "main"));
        var plan = await launcher.GetFromJsonAsync<SchedulePlanDto>(
            $"/api/pipelines/schedule-plan?organizationId={scene.OrganizationId}&from=2027-01-04T00:00:00Z&to=2027-01-05T00:00:00Z",
            IntegrationJsonOptions.Default, Ct);
        var task = Assert.Single(plan!.Tasks);
        Assert.Equal(("develop", "develop", developCron), (task.SourceBranch, task.OwnSourceBranch, task.CronExpression));
        Assert.Contains(await ScheduleAuditAsync(factory, id, "SourceBranchChanged"), entry => entry.Username == scene.Launcher);
    }

    [Fact]
    public async Task SourceBranch_WithoutTheRunRight_Is403_AndNothingChanges()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var id = await scene.CreatePipelineAsync(Definition(scene.Name, scene.Runner));
        using var mover = await IntegrationUsers.LoginClientAsync(factory, scene.Mover);

        using var response = await PutBranchAsync(mover, id, "develop", "main");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("main", (await ReadAsync(factory, id)).SourceBranch);
    }

    [Fact]
    public async Task SourceBranch_OutsideMainAndDevelop_Is400_AndABranchChangedSinceTheRead_Is409()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var scene = await Scene.CreateAsync(factory);
        var id = await scene.CreatePipelineAsync(Definition(scene.Name, scene.Runner));
        using var launcher = await IntegrationUsers.LoginClientAsync(factory, scene.Launcher);

        using var unknown = await PutBranchAsync(launcher, id, "feature/x", "main");
        using var stale = await PutBranchAsync(launcher, id, "develop", "release");

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("main", (await ReadAsync(factory, id)).SourceBranch);
    }

    private static async Task<Pipeline> ReadAsync(AetheusWebApplicationFactory factory, int id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Pipelines.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private static async Task<List<AuditLog>> ScheduleAuditAsync(
        AetheusWebApplicationFactory factory, int id, string action = "ScheduleChanged")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == action && entry.EntityId == id).ToListAsync(Ct);
    }

    private static async Task<(int OrganizationId, int ProjectId)> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"sched-{label}-{Guid.NewGuid():N}", Slug = $"w{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"sched-{label}", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return (organization.Id, project.Id);
    }

    private static async Task<int> SeedPipelineAsync(
        AetheusWebApplicationFactory factory, int? projectId, string name, string cron = FirstCron,
        string? yaml = null, PipelineTriggerType trigger = PipelineTriggerType.Schedule)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new Pipeline
        {
            ProjectId = projectId,
            Name = name,
            TriggerType = trigger,
            CreatedByUsername = "owner",
            YamlDefinition = yaml ?? $"name: {name}\ntrigger: schedule\nschedule: \"{cron}\"\nstages:\n  - name: b\n    steps:\n      - name: s\n        shell: echo one\n"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        return pipeline.Id;
    }

    /// <summary>A project of the default organization with its internal repository and an online runner,
    /// an owner who administers that runner, a mover who may write the pipeline but neither run it nor touch the runner, and a launcher who has the run right as well.</summary>
    private sealed class Scene
    {
        private readonly AetheusWebApplicationFactory _factory;

        private Scene(AetheusWebApplicationFactory factory) => _factory = factory;

        public string Name { get; private init; } = string.Empty;
        public string Runner { get; private init; } = string.Empty;
        public int OrganizationId { get; private init; }
        public int ProjectId { get; private init; }
        public string Owner { get; private init; } = string.Empty;
        public string Mover { get; private init; } = string.Empty;
        public string Launcher { get; private init; } = string.Empty;
        private string DiskPath { get; init; } = string.Empty;

        public static async Task<Scene> CreateAsync(AetheusWebApplicationFactory factory)
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var runner = $"sched-runner-{suffix}";
            int organizationId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                organizationId = await db.Organizations.Where(o => o.Slug == IntegrationUsers.DefaultOrganizationSlug)
                    .Select(o => o.Id).FirstAsync(Ct);
                db.Servers.Add(new Server
                {
                    Name = runner,
                    Hostname = runner,
                    Status = ServerStatus.Online,
                    PipelineRunnerEnabled = true,
                    LastHeartbeat = DateTime.UtcNow,
                    OrganizationId = organizationId,
                    AgentProtocolVersion = AgentProtocol.CurrentVersion,
                    AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
                });
                await db.SaveChangesAsync(Ct);
            }

            using var admin = await IntegrationUsers.AdminClientAsync(factory);
            var project = await PipelineLaunchScenario.PostAsync<ProjectDto>(admin, "/api/projects", new CreateProjectRequest
            {
                Name = $"sched-{suffix}",
                Description = "a pipeline whose schedule moves",
                OrganizationId = organizationId
            });
            var repository = await PipelineLaunchScenario.PostAsync<GitLightRepoDto>(admin, "/api/git/repos", new CreateGitLightRepoRequest
            {
                ProjectId = project.Id,
                Name = $"sched-repo-{suffix}",
                DefaultBranch = "main"
            });
            var owner = await IntegrationUsers.CreateAsync(factory.Services, "sched-owner",
                [.. Writer, new IntegrationUsers.Grant(ResourceType.Server, null, Permission.Admin)]);
            var mover = await IntegrationUsers.CreateAsync(factory.Services, "sched-mover", Writer);
            var launcher = await IntegrationUsers.CreateAsync(factory.Services, "sched-launcher", Writer, permissionKeys: [PermissionKeys.PipelineRun]);
            await using var last = factory.Services.CreateAsyncScope();
            return new Scene(factory)
            {
                Name = $"sched-{suffix}",
                Runner = runner,
                OrganizationId = organizationId,
                ProjectId = project.Id,
                Owner = owner.Username,
                Mover = mover.Username,
                Launcher = launcher.Username,
                DiskPath = last.ServiceProvider.GetRequiredService<IGitLightService>().ResolveDiskPath(project.Id, repository.Slug)
            };
        }

        public async Task<int> CreatePipelineAsync(string yaml)
        {
            using var owner = await IntegrationUsers.LoginClientAsync(_factory, Owner);
            var pipeline = await PipelineLaunchScenario.PostAsync<PipelineDto>(owner, "/api/pipelines", new CreatePipelineRequest
            {
                Name = Name,
                Description = "created by its owner, committed to the project's repository",
                ProjectId = ProjectId,
                SourceBranch = "main",
                YamlDefinition = yaml
            });
            Assert.Equal(Owner, (await ReadAsync(_factory, pipeline.Id)).CreatedByUsername);
            return pipeline.Id;
        }

        /// <summary>The real <c>git</c> on the bare repository; a failure fails the test.</summary>
        public string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(DiskPath);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
            return arguments[0] == "show" && arguments[1].Contains(':', StringComparison.Ordinal) ? output : output.Trim();
        }
    }
}
