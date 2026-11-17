// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-5, the branch the planner reads a pipeline's schedule on. The pipeline's mirrored definition tells it by
/// default; the reader may choose main or develop per pipeline and the definition is then read from the project's repository
/// on that branch. The choice is display only: nothing is written, and a branch with no readable definition is said, not hidden.
/// The git read is a double here (the real repository read is the integration of <c>IPipelineGitService</c>); what is proved is
/// which branch is asked for, which schedule is drawn from the answer and what the plan says about it.
/// </summary>
public sealed class PipelineSchedulePlanSourceBranchTests : IDisposable
{
    private static readonly DateTime Day = new(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Project = 5;
    private const int Pipeline = 9;

    private readonly IPipelineGitService _git = Substitute.For<IPipelineGitService>();

    private static string Yaml(string trigger, string? cron) =>
        $"name: demo\ntrigger: {trigger}\n{(cron is null ? string.Empty : $"schedule: \"{cron}\"\n")}stages: []";

    // The server actions are out of the scope here (ServerActionScope.None): the reader never queries its repository.
    private PipelineSchedulePlanService Service(params PipelineScheduleRow[] rows)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(dbOptions);
        var reader = new ServerActionPlanReader(
            new ServerActionScheduleRepository(_db), clock, Options.Create(new VulnerabilityWatchOptions()));
        return new PipelineSchedulePlanService(new FakeRepository(rows), reader, _git, clock);
    }

    private AppDbContext? _db;

    public void Dispose() => _db?.Dispose();

    private static PipelineScheduleRow Row(string? ownBranch = "main", int? projectId = Project, string yaml = "") =>
        new(Pipeline, "demo", PipelineTriggerType.Schedule,
            yaml.Length > 0 ? yaml : Yaml("schedule", "0 0 1 * * *"), projectId, null, ownBranch);

    private Task<SchedulePlanDto> PlanAsync(PipelineSchedulePlanService service, params (int Id, string Branch)[] choices) =>
        service.GetPlanAsync(
            1, Day, Day.AddDays(1), null, ServerActionScope.None,
            choices.Length == 0 ? null : choices.ToDictionary(choice => choice.Id, choice => choice.Branch), Ct);

    [Fact]
    public async Task WithNoChoice_TheMirroredDefinitionIsDrawn_AndTheBranchItIsReadFromIsSaid()
    {
        var plan = await PlanAsync(Service(Row(ownBranch: "main")));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal("main", task.SourceBranch);
        Assert.Equal("main", task.OwnSourceBranch);
        Assert.True(task.CanChooseSourceBranch);
        Assert.False(task.ChosenBranchMissing);
        Assert.Equal("0 0 1 * * *", task.CronExpression);
        await _git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAsync(0, string.Empty, TestContext.Current.CancellationToken, null, null);
    }

    [Fact]
    public async Task AChosenBranch_IsReadFromGit_AndItsScheduleIsTheOneDrawn_NotTheMirroredOne()
    {
        _git.ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "develop", null)
            .Returns(Yaml("schedule", "0 0 3 * * *"));

        var plan = await PlanAsync(Service(Row(ownBranch: "main")), (Pipeline, "develop"));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal("develop", task.SourceBranch);
        Assert.Equal("main", task.OwnSourceBranch);
        Assert.Equal("0 0 3 * * *", task.CronExpression);
        Assert.Equal(new DateTime(2027, 1, 4, 3, 0, 0, DateTimeKind.Utc), Assert.Single(task.Occurrences).StartUtc);
        Assert.False(task.ChosenBranchMissing);
        await _git.Received(1).ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "develop", null);
    }

    [Fact]
    public async Task AChosenBranchWhoseDefinitionIsNotScheduled_DrawsNoRun_ButTheCardStaysSoTheReaderCanChooseBack()
    {
        _git.ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "develop", null)
            .Returns(Yaml("manual", null));

        var plan = await PlanAsync(Service(Row(ownBranch: "main")), (Pipeline, "develop"));

        var task = Assert.Single(plan.Tasks);
        Assert.False(task.Enabled);
        Assert.Empty(task.Occurrences);
        Assert.Null(task.NextRunUtc);
        Assert.Equal("develop", task.SourceBranch);
    }

    [Fact]
    public async Task AChosenBranchWithNoReadableDefinition_KeepsTheOwnScheduleAndSaysItIsMissing()
    {
        _git.ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "develop", null).Returns((string?)null);

        var plan = await PlanAsync(Service(Row(ownBranch: "main")), (Pipeline, "develop"));

        var task = Assert.Single(plan.Tasks);
        Assert.True(task.ChosenBranchMissing);
        Assert.Equal("develop", task.SourceBranch);
        Assert.Equal("0 0 1 * * *", task.CronExpression);
        Assert.NotEmpty(task.Occurrences);
    }

    [Fact]
    public async Task AChoiceEqualToTheOwnBranch_IsNotReadFromGit()
    {
        var plan = await PlanAsync(Service(Row(ownBranch: "main")), (Pipeline, "main"));

        Assert.Equal("main", Assert.Single(plan.Tasks).SourceBranch);
        await _git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAsync(0, string.Empty, TestContext.Current.CancellationToken, null, null);
    }

    [Fact]
    public async Task APipelineOfNoProject_HasNoRepositoryToRead_ThereforeNoChoice()
    {
        var plan = await PlanAsync(Service(Row(projectId: null)), (Pipeline, "develop"));

        var task = Assert.Single(plan.Tasks);
        Assert.False(task.CanChooseSourceBranch);
        Assert.True(task.ChosenBranchMissing);
        await _git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAsync(0, string.Empty, TestContext.Current.CancellationToken, null, null);
    }

    [Fact]
    public async Task AChoiceOnAnotherPipeline_ChangesNothingForThisOne()
    {
        var plan = await PlanAsync(Service(Row(ownBranch: "main")), (Pipeline + 1, "develop"));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal("main", task.SourceBranch);
        Assert.Equal("0 0 1 * * *", task.CronExpression);
    }

    [Fact]
    public async Task AManualPipelineWithNoSchedule_StaysOutOfThePlan_UnlessItCarriesAChoice()
    {
        var manual = new PipelineScheduleRow(Pipeline, "demo", PipelineTriggerType.Manual, Yaml("manual", null), Project, null, "main");
        _git.ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "develop", null)
            .Returns(Yaml("schedule", "0 0 2 * * *"));

        Assert.Empty((await PlanAsync(Service(manual))).Tasks);
        var chosen = Assert.Single((await PlanAsync(Service(manual), (Pipeline, "develop"))).Tasks);

        Assert.True(chosen.Enabled);
        Assert.Equal("0 0 2 * * *", chosen.CronExpression);
    }

    // The planner's own list (a pipeline that never chose a branch) must not break on a missing own branch.
    [Fact]
    public async Task APipelineWithNoOwnBranch_SaysNoBranch_AndTheChoiceStillWorks()
    {
        _git.ReadProjectPipelineYamlAsync(Project, "demo", Arg.Any<CancellationToken>(), "main", null)
            .Returns(Yaml("schedule", "0 0 4 * * *"));

        var plan = await PlanAsync(Service(Row(ownBranch: null)), (Pipeline, "main"));

        var task = Assert.Single(plan.Tasks);
        Assert.Null(task.OwnSourceBranch);
        Assert.Equal("main", task.SourceBranch);
        Assert.Equal("0 0 4 * * *", task.CronExpression);
    }

    /// <summary>The query side of the plan, from rows kept in memory.</summary>
    private sealed class FakeRepository(PipelineScheduleRow[] rows) : IPipelineScheduleRepository
    {
        public Task<PipelineScheduleTargetRow?> GetTargetAsync(int pipelineId, CancellationToken ct) =>
            Task.FromResult<PipelineScheduleTargetRow?>(null);

        public Task<List<PipelineScheduleRow>> GetCandidatesAsync(
            int organizationId, IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct) =>
            Task.FromResult(rows.ToList());

        public Task<int> GetExecutionCapacityAsync(int organizationId, CancellationToken ct) => Task.FromResult(1);

        public Task<Dictionary<int, ScheduleLastRunDto>> GetLastRunsAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<int, ScheduleLastRunDto>());

        public Task<Dictionary<int, int>> GetAverageMinutesAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<int, int>());
    }
}

/// <summary>Recette R-5, the query of the plan: <c>sourceBranch=&lt;id&gt;:&lt;branch&gt;</c>, main or develop only.</summary>
public sealed class PipelineScheduleSourceBranchesTests
{
    [Fact]
    public void NoValue_IsNoChoice()
    {
        Assert.Null(PipelineScheduleSourceBranches.Parse(null));
        Assert.Null(PipelineScheduleSourceBranches.Parse([]));
    }

    [Fact]
    public void AValidChoice_IsReadPerPipeline_TheLastOneWinning()
    {
        var choices = PipelineScheduleSourceBranches.Parse(["7:develop", "9:main", "7:main"]);

        Assert.NotNull(choices);
        Assert.Equal(2, choices.Count);
        Assert.Equal("main", choices[7]);
        Assert.Equal("main", choices[9]);
    }

    [Theory]
    [InlineData("develop")]
    [InlineData(":develop")]
    [InlineData("x:develop")]
    [InlineData("0:develop")]
    [InlineData("-3:develop")]
    [InlineData("7:")]
    [InlineData("7:feature/x")]
    [InlineData("7:Main")]
    [InlineData("7:--upload-pack=x")]
    public void AnythingElse_IsRefused_SoNoOtherRefReachesGit(string value) =>
        Assert.Throws<Aetheus.Back.Exceptions.BadRequestException>(() => PipelineScheduleSourceBranches.Parse([value]));
}
