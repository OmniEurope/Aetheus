// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-97 (ADR-058): what the repository reads for the deletion plan beyond the project itself,
/// the other projects' libraries and slugs, and when the project last ran something on each server. Runs on the
/// EF in-memory provider: it proves the queries' shape and grouping, not their PostgreSQL translation. </summary>
public sealed class ProjectServerCleanupRepositoryTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, _time);

    private static VariableLibrary Library(int id, string name, string key, string value) => new()
    {
        Id = id,
        Name = name,
        Entries = [new VariableLibraryEntry { Key = key, Value = value }],
    };

    [Fact]
    public async Task OtherProjects_GroupEveryLibraryOutsideTheProject_ByTheProjectThatOwnsIt()
    {
        await using var db = CreateDb();
        db.Projects.AddRange(
            new Project { Id = 7, Name = "Portfolio", OrganizationId = 1 },
            new Project { Id = 8, Name = "Shop", OrganizationId = 1 },
            new Project { Id = 9, Name = "Other", OrganizationId = 2 });
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 30, Name = "prod", ProjectId = 8 });
        var own = Library(1, "portfolio.prod", "COMPOSE_PROJECT", "portfolio-prod");
        own.ProjectId = 7;
        var shop = Library(2, "anything", "COMPOSE_PROJECT", "PORTFOLIO-PROD");
        shop.EnvironmentId = 30;
        db.VariableLibraries.AddRange(own, shop, Library(3, "global", "APP_HOST", "app.portfolio.example"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var inputs = await new ProjectServerCleanupRepository(db).LoadInputsAsync(7, TestContext.Current.CancellationToken);

        Assert.NotNull(inputs);
        Assert.Equal(["portfolio.prod"], inputs.Libraries.Select(item => item.Name));
        Assert.Equal(
            [("shop", true, "anything"), ("other", false, "-"), ("", false, "global")],
            inputs.OtherProjects.Select(item => (item.Slug, item.SameOrganization,
                item.Libraries.Count == 0 ? "-" : string.Join(",", item.Libraries.Select(library => library.Name)))));
        Assert.Equal("PORTFOLIO-PROD", inputs.OtherProjects[0].Libraries[0].Entries["COMPOSE_PROJECT"]);
    }

    [Fact]
    public async Task ProductionDeployServers_AreTheDeployServerDefaults_OfThisProjectsPipelines()
    {
        await using var db = CreateDb();
        db.Projects.AddRange(new Project { Id = 27, Name = "W8", OrganizationId = 1 }, new Project { Id = 28, Name = "Shop", OrganizationId = 1 });
        const string Deploy = """
            name: w8-deploy-prod
            extends: application-deploy-prod@3
            trigger: manual
            parameters:
              - name: deployServer
                type: choice
                required: true
                allowed_values:
                  - "runner"
                  - "vpssim"
                default: "vpssim"
            """;
        db.Pipelines.AddRange(
            new Pipeline { Id = 50, Name = "w8-deploy-prod", ProjectId = 27, YamlDefinition = Deploy },
            new Pipeline { Id = 51, Name = "w8-release-fast", ProjectId = 27, YamlDefinition = Deploy.Replace("w8-deploy-prod", "w8-release-fast", StringComparison.Ordinal) },
            new Pipeline { Id = 52, Name = "w8-ci", ProjectId = 27, YamlDefinition = "name: w8-ci\nextends: application-ci@2\n" },
            new Pipeline { Id = 53, Name = "w8-broken", ProjectId = 27, YamlDefinition = "name: [" },
            new Pipeline { Id = 54, Name = "w8-unset", ProjectId = 27, YamlDefinition = "name: w8-unset\nparameters:\n  - name: deployServer\n    default: \"${{ parameters.other }}\"\n" },
            new Pipeline { Id = 55, Name = "shop-deploy-prod", ProjectId = 28, YamlDefinition = Deploy.Replace("default: \"vpssim\"", "default: \"runner\"", StringComparison.Ordinal) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var inputs = await new ProjectServerCleanupRepository(db).LoadInputsAsync(27, TestContext.Current.CancellationToken);

        Assert.NotNull(inputs);
        Assert.Equal(["vpssim"], inputs.ProductionDeployServers);
    }

    [Fact]
    public async Task TaskInputs_KeepTheProjectsOwnDataAndDeployServer_ButNotOtherProjectsLibraries()
    {
        await using var db = CreateDb();
        db.Projects.AddRange(
            new Project { Id = 7, Name = "Portfolio", OrganizationId = 1 },
            new Project { Id = 8, Name = "Shop", OrganizationId = 1 });
        var own = Library(1, "portfolio.prod", "COMPOSE_PROJECT", "portfolio-prod");
        own.ProjectId = 7;
        db.VariableLibraries.AddRange(own, Library(3, "global", "APP_HOST", "app.portfolio.example"));
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 30, Name = "prod", ProjectId = 7 });
        db.Pipelines.Add(new Pipeline
        {
            Id = 50,
            Name = "deploy",
            ProjectId = 7,
            YamlDefinition = "name: deploy\nparameters:\n  - name: deployServer\n    default: \"vpssim\"\n",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new ProjectServerCleanupRepository(db);

        var full = await repository.LoadInputsAsync(7, TestContext.Current.CancellationToken);
        var light = await repository.LoadTaskInputsAsync(7, TestContext.Current.CancellationToken);

        Assert.NotNull(full);
        Assert.NotNull(light);
        Assert.Equal(["shop", ""], full.OtherProjects.Select(item => item.Slug));
        Assert.Equal(["vpssim"], full.ProductionDeployServers);
        Assert.Empty(light.OtherProjects);
        Assert.Equal(["vpssim"], light.ProductionDeployServers);
        Assert.Equal((full.ProjectId, full.ProjectName, full.OrganizationId), (light.ProjectId, light.ProjectName, light.OrganizationId));
        Assert.Equal(full.Libraries.Select(item => item.Name), light.Libraries.Select(item => item.Name));
        Assert.Equal(full.Environments.Select(item => (item.Id, item.Name)), light.Environments.Select(item => (item.Id, item.Name)));
        Assert.Null(await repository.LoadTaskInputsAsync(99, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TaskOutputs_GroupTheLogLinesOfEachTask_InLogOrder_AndGiveATaskWithoutLogsAnEmptyOutput()
    {
        await using var db = CreateDb();
        db.TaskLogs.AddRange(
            new TaskLog { Id = 1, TaskId = 41, Message = "a" },
            new TaskLog { Id = 2, TaskId = 42, Message = "x" },
            new TaskLog { Id = 3, TaskId = 41, Message = "b" },
            new TaskLog { Id = 4, TaskId = 99, Message = "not asked" },
            new TaskLog { Id = 5, TaskId = 41, Message = "c" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outputs = await new ProjectServerCleanupRepository(db)
            .GetTaskOutputsAsync([41, 42, 43, 41], TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<int, string> { [41] = "a\nb\nc", [42] = "x", [43] = string.Empty }, outputs);
        Assert.Empty(await new ProjectServerCleanupRepository(db).GetTaskOutputsAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LastProjectActivity_IsTheLatestPipelineTaskOfThisProject_PerServer()
    {
        await using var db = CreateDb();
        db.Pipelines.AddRange(new Pipeline { Id = 50, Name = "deploy", ProjectId = 7 }, new Pipeline { Id = 51, Name = "deploy", ProjectId = 8 });
        db.PipelineRuns.AddRange(new PipelineRun { Id = 60, PipelineId = 50 }, new PipelineRun { Id = 61, PipelineId = 51 });
        db.Tasks.Add(new ServerTask { ServerId = 3, Name = "first", PipelineRunId = 60 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(10));
        var latest = _time.GetUtcNow().UtcDateTime;
        db.Tasks.Add(new ServerTask { ServerId = 3, Name = "second", PipelineRunId = 60 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(10));
        // Later work on the same server that is not this project's pipeline: another project's, or no run at all.
        db.Tasks.AddRange(
            new ServerTask { ServerId = 3, Name = "shop", PipelineRunId = 61 },
            new ServerTask { ServerId = 3, Name = "discovery" },
            new ServerTask { ServerId = 5, Name = "elsewhere", PipelineRunId = 60 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var activity = await new ProjectServerCleanupRepository(db).GetLastProjectActivityAsync(7, [3, 4], TestContext.Current.CancellationToken);

        Assert.Equal(latest, Assert.Single(activity, item => item.Key == 3).Value);
        Assert.False(activity.ContainsKey(4));
        Assert.False(activity.ContainsKey(5));
    }
}
