// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using DataEnvironment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Projects;

/// <summary>
/// R-29: the project's production address is the one its latest successful production deployment run deployed, read at project
/// level; a project without such a run has none, and a project never sees another project's deployment.
/// </summary>
public sealed class ProjectProductionAddressTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    // The shipped production templates declare no stage environment.
    private const string TemplateSnapshot = """
        name: deploy
        stages:
          - name: Switch
            steps:
              - name: Switch traffic
                type: bluegreen-switch
        """;

    private static string EnvironmentSnapshot(string environment) => $"""
        name: deploy
        stages:
          - name: Switch
            environment: {environment}
            steps:
              - name: Switch traffic
                type: bluegreen-switch
        """;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private async Task<Pipeline> PipelineOfAsync(string projectName)
    {
        var project = new Project { Name = projectName, Description = "d" };
        var pipeline = new Pipeline { Name = "deploy", YamlDefinition = "name: deploy", Project = project };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return pipeline;
    }

    private void AddRun(
        Pipeline pipeline, DateTime startedAt, string? snapshot, Dictionary<string, string> variables,
        PipelineStatus status = PipelineStatus.Success)
    {
        var run = new PipelineRun
        {
            Pipeline = pipeline,
            Status = status,
            StartedAt = startedAt,
            YamlSnapshot = snapshot,
            ResolvedVariablesJson = JsonSerializer.Serialize(variables),
            StepRuns = [new PipelineStepRun { StageName = "Switch", StepName = "Switch traffic", Status = TaskExecutionStatus.Success }]
        };
        _db.PipelineRuns.Add(run);
    }

    private static Dictionary<string, string> Address(string app, string? api = null)
    {
        var variables = new Dictionary<string, string> { ["BG_PUBLIC_ORIGIN"] = app };
        if (api is not null) variables["BG_PUBLIC_API_ORIGIN"] = api;
        return variables;
    }

    private async Task<PipelineRunDeployedAddressDto?> ReadAsync(int projectId)
    {
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new ProjectRepository(_db, Substitute.For<IProjectAnalysisGradeReader>());
        return await repository.GetProductionAddressAsync(projectId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheLatestSuccessfulProductionDeployment_Wins()
    {
        var pipeline = await PipelineOfAsync("Wordmorrow");
        AddRun(pipeline, T0, TemplateSnapshot, Address("https://old.example.test"));
        AddRun(pipeline, T0.AddDays(2), TemplateSnapshot, Address("https://app.example.test", "https://api.example.test/"));
        AddRun(pipeline, T0.AddDays(1), TemplateSnapshot, Address("https://middle.example.test"));

        var address = await ReadAsync(pipeline.ProjectId!.Value);

        Assert.NotNull(address);
        Assert.Equal("https://app.example.test", address.AppUrl);
        Assert.Equal("https://api.example.test", address.ApiUrl);
    }

    [Fact]
    public async Task ANewerFailedRun_AndANewerRunThatDeployedNothing_AreIgnored()
    {
        var pipeline = await PipelineOfAsync("Wordmorrow");
        AddRun(pipeline, T0, TemplateSnapshot, Address("https://app.example.test"));
        AddRun(pipeline, T0.AddDays(1), TemplateSnapshot, Address("https://failed.example.test"), PipelineStatus.Failed);
        // A successful run that resolved a public address but whose definition has no deploy step (a CI run).
        AddRun(pipeline, T0.AddDays(2), "name: ci\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        type: script",
            Address("https://ci.example.test"));

        var address = await ReadAsync(pipeline.ProjectId!.Value);

        Assert.Equal("https://app.example.test", address!.AppUrl);
    }

    [Fact]
    public async Task AProjectWithoutAProductionDeployment_HasNoAddress()
    {
        var pipeline = await PipelineOfAsync("Fresh");
        AddRun(pipeline, T0, "name: ci\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        type: script", []);
        var empty = await PipelineOfAsync("Empty");

        Assert.Null(await ReadAsync(pipeline.ProjectId!.Value));
        Assert.Null(await ReadAsync(empty.ProjectId!.Value));
    }

    [Fact]
    public async Task AnotherProjectsDeployment_IsNeverVisible()
    {
        var other = await PipelineOfAsync("Other");
        var mine = await PipelineOfAsync("Mine");
        AddRun(other, T0, TemplateSnapshot, Address("https://other.example.test"));

        Assert.Null(await ReadAsync(mine.ProjectId!.Value));
        Assert.Equal("https://other.example.test", (await ReadAsync(other.ProjectId!.Value))!.AppUrl);
    }

    [Fact]
    public async Task ADeploymentOfAnotherEnvironment_IsNotProduction()
    {
        var pipeline = await PipelineOfAsync("Wordmorrow");
        AddRun(pipeline, T0, EnvironmentSnapshot("prod"), Address("https://app.example.test"));
        AddRun(pipeline, T0.AddDays(1), EnvironmentSnapshot("demo"), Address("https://demo.example.test"));

        var address = await ReadAsync(pipeline.ProjectId!.Value);

        Assert.Equal("https://app.example.test", address!.AppUrl);
    }

    [Fact]
    public async Task AnEnvironmentOfTypeProduction_IsProductionWhateverItsName()
    {
        var pipeline = await PipelineOfAsync("Wordmorrow");
        _db.Environments.Add(new DataEnvironment { Name = "live", Type = EnvironmentType.Production, ProjectId = pipeline.ProjectId });
        AddRun(pipeline, T0, EnvironmentSnapshot("live"), Address("https://live.example.test"));

        var address = await ReadAsync(pipeline.ProjectId!.Value);

        Assert.Equal("https://live.example.test", address!.AppUrl);
    }

    [Fact]
    public async Task ALocalSelfDeployRun_IsNotProduction()
    {
        var pipeline = await PipelineOfAsync("Aetheus");
        AddRun(pipeline, T0, TemplateSnapshot, Address("https://app.example.test"));
        var local = Address("https://localhost:5001");
        local["AETHEUS_DEPLOY_TARGET"] = "local";
        AddRun(pipeline, T0.AddDays(1), TemplateSnapshot, local);

        var address = await ReadAsync(pipeline.ProjectId!.Value);

        Assert.Equal("https://app.example.test", address!.AppUrl);
    }
}
