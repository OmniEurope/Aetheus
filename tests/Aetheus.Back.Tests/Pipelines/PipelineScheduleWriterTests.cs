// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Pipelines;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>What the writer asks of the pipeline service, on doubles: the real commit, conflict and owner are
/// proved over HTTP in <c>PipelineScheduleWriteHttpIntegrationTests</c>.</summary>
public sealed class PipelineScheduleWriterTests
{
    private const string Yaml = "name: n\ntrigger: schedule\nschedule: \"0 0 1 * * *\"\nstages: []\n";

    private readonly IPipelineService _service = Substitute.For<IPipelineService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineScheduleWriter _writer;
    private readonly PipelineDto _pipeline = new()
    {
        Id = 7,
        Name = "n",
        Description = "d",
        YamlDefinition = Yaml,
        TriggerType = PipelineTriggerType.Schedule,
        ProjectId = 3,
        SourceRepositoryId = 4,
        SourceBranch = "develop",
        AiRunnerProfileId = 5
    };

    public PipelineScheduleWriterTests()
    {
        _service.ValidateYamlStrictAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _service.UpdatePipelineAsync(7, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>()).Returns(_pipeline);
        _writer = new PipelineScheduleWriter(_service, _audit);
    }

    private static UpdatePipelineScheduleRequest Move(int hour = 2, int minute = 30) => new()
    {
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = hour,
        Minute = minute,
        ExpectedCronExpression = "0 0 1 * * *",
        ExpectedEnabled = true
    };

    private async Task<PipelineDto> ApplyAsync(UpdatePipelineScheduleRequest request) => await
        _writer.ApplyAsync(_pipeline, PipelineScheduleWriter.Prepare(_pipeline, request), request.Enabled, TestContext.Current.CancellationToken);

    private Task NoWriteAsync() =>
        _service.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task Move_GoesThroughTheUpdate_WithEverySettingOfThePipelineKept()
    {
        await ApplyAsync(Move());

        await _service.Received(1).UpdatePipelineAsync(7, Arg.Is<UpdatePipelineRequest>(request =>
            request.YamlDefinition == Yaml.Replace("0 0 1 * * *", "0 30 2 * * *", StringComparison.Ordinal)
            && request.Name == "n" && request.Description == "d" && request.ProjectId == 3
            && request.SourceRepositoryId == 4 && request.SourceBranch == "develop" && request.AiRunnerProfileId == 5),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "ScheduleChanged", "Pipeline", 7, "'0 0 1 * * *' -> '0 30 2 * * *'; enabled: True -> True", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameSchedule_WritesNothing()
    {
        var result = await ApplyAsync(Move(1, 0));

        Assert.Same(_pipeline, result);
        await NoWriteAsync();
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0 0 9 * * *", true)]
    [InlineData(null, true)]
    [InlineData("0 0 1 * * *", false)]
    public async Task WhatTheCallerReadIsNoLongerTrue_IsAConflict_AndNothingIsWritten(string? expected, bool expectedEnabled)
    {
        await Assert.ThrowsAsync<ConflictException>(() => ApplyAsync(
            Move() with { ExpectedCronExpression = expected, ExpectedEnabled = expectedEnabled }));

        await NoWriteAsync();
    }

    [Fact]
    public async Task ProposalThatCannotBeWritten_IsABadRequest_AndNothingIsWritten()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => ApplyAsync(Move() with { Frequency = ScheduleFrequency.Custom }));

        await NoWriteAsync();
    }

    [Fact]
    public async Task DefinitionTheSaveGateRefuses_IsABadRequest_AndNothingIsWritten()
    {
        _service.ValidateYamlStrictAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new YamlValidationResultDto { IsValid = false, Errors = ["no template"] });

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => ApplyAsync(Move()));

        Assert.Contains("no template", refusal.Message, StringComparison.Ordinal);
        await NoWriteAsync();
    }

    [Fact]
    public async Task PipelineGoneDuringTheWrite_IsNotFound()
    {
        _service.UpdatePipelineAsync(7, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>()).Returns((PipelineDto?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => ApplyAsync(Move()));
    }

    [Fact]
    public async Task Disabling_KeepsTheValue()
    {
        await ApplyAsync(new UpdatePipelineScheduleRequest
        {
            Enabled = false,
            ExpectedCronExpression = "0 0 1 * * *",
            ExpectedEnabled = true
        });

        await _service.Received(1).UpdatePipelineAsync(7, Arg.Is<UpdatePipelineRequest>(request =>
            request.YamlDefinition == Yaml.Replace("trigger: schedule", "trigger: manual", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "ScheduleChanged", "Pipeline", 7, "'0 0 1 * * *' -> '0 0 1 * * *'; enabled: True -> False", Arg.Any<CancellationToken>());
    }

    private Task<PipelineDto> ChangeBranchAsync(string branch, string? expected) =>
        _writer.ChangeSourceBranchAsync(_pipeline, branch, expected, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SourceBranch_GoesThroughTheUpdate_WithEverySettingKept_AndIsAudited()
    {
        var main = _pipeline with { SourceBranch = "main" };
        await _writer.ChangeSourceBranchAsync(main, "develop", "main", TestContext.Current.CancellationToken);

        await _service.Received(1).UpdatePipelineAsync(7, Arg.Is<UpdatePipelineRequest>(request =>
            request.SourceBranch == "develop" && request.YamlDefinition == Yaml && request.Name == "n" && request.ProjectId == 3
            && request.SourceRepositoryId == 4 && request.AiRunnerProfileId == 5),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "SourceBranchChanged", "Pipeline", 7, "'main' -> 'develop'", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceBranch_AlreadyThatBranch_WritesNothing()
    {
        var result = await ChangeBranchAsync("develop", "develop");

        Assert.Same(_pipeline, result);
        await NoWriteAsync();
    }

    [Theory]
    [InlineData("release")]
    [InlineData("")]
    public async Task SourceBranch_OutsideMainAndDevelop_IsRefused_AndNothingIsWritten(string branch)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => ChangeBranchAsync(branch, "develop"));
        await NoWriteAsync();
    }

    [Fact]
    public async Task SourceBranch_ChangedSinceTheRead_IsAConflict_AndNothingIsWritten()
    {
        await Assert.ThrowsAsync<ConflictException>(() => ChangeBranchAsync("main", "release"));
        await NoWriteAsync();
    }

    [Fact]
    public async Task SourceBranch_OfAPipelineWithNoProject_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _writer.ChangeSourceBranchAsync(
            _pipeline with { ProjectId = null }, "main", "develop", TestContext.Current.CancellationToken));
        await NoWriteAsync();
    }
}
