// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>Writes a schedule change to a pipeline the caller was authorized to write.</summary>
public interface IPipelineScheduleWriter
{
    /// <summary>Writes <paramref name="change"/>, from <see cref="PipelineScheduleWriter.Prepare"/>, to
    /// <paramref name="pipeline"/>, as the fleet update rewrites a definition: through
    /// <see cref="IPipelineService.UpdatePipelineAsync"/>, so the file is committed, a repository change
    /// Aetheus has not seen refuses the write, a failed save puts the file back, and the change is audited
    /// and broadcast. A change that changes nothing writes nothing.</summary>
    Task<PipelineDto> ApplyAsync(PipelineDto pipeline, PipelineScheduleChangeResult change, bool enabled, CancellationToken ct);

    /// <summary>Stores <paramref name="branch"/> as the source branch of <paramref name="pipeline"/> (recette R-5, user decision
    /// 2026-11-09): the branch its definition, hence its schedule, is read on from now on. It goes through
    /// <see cref="IPipelineService.UpdatePipelineAsync"/> with every other setting kept, so the repository's file on that branch
    /// is adopted as the update does, a failed save puts things back, and the change is audited and broadcast. The same branch
    /// again writes nothing. Only <see cref="ScheduleDefaults.SourceBranchChoices"/> are accepted (<see cref="BadRequestException"/>);
    /// a stored branch other than <paramref name="expectedBranch"/> is a <see cref="ConflictException"/>.</summary>
    Task<PipelineDto> ChangeSourceBranchAsync(PipelineDto pipeline, string branch, string? expectedBranch, CancellationToken ct);
}

/// <inheritdoc cref="IPipelineScheduleWriter"/>
public sealed class PipelineScheduleWriter(IPipelineService pipelineService, IAuditService audit) : IPipelineScheduleWriter
{
    /// <summary>The change <paramref name="request"/> asks of <paramref name="pipeline"/>, written nowhere, so
    /// that the caller can tell what it amounts to before authorizing it. What the caller last read no
    /// longer being true is a <see cref="ConflictException"/>; a change the YAML cannot take is a
    /// <see cref="BadRequestException"/>.</summary>
    public static PipelineScheduleChangeResult Prepare(PipelineDto pipeline, UpdatePipelineScheduleRequest request)
    {
        var current = PipelineScheduleReader.Read(pipeline.YamlDefinition)?.Schedule ?? string.Empty;
        var enabled = pipeline.TriggerType == PipelineTriggerType.Schedule;
        if (current != (request.ExpectedCronExpression ?? string.Empty) || enabled != request.ExpectedEnabled)
        {
            throw new ConflictException(
                $"The schedule of pipeline '{pipeline.Name}' changed since it was read. Reload it before changing it.");
        }

        var change = request.Frequency is { } frequency
            ? PipelineScheduleChange.Prepare(
                pipeline.YamlDefinition,
                new ScheduleFrequencySpec(frequency, request.Day, request.Hour, request.Minute),
                request.Enabled)
            : PipelineScheduleChange.PrepareEnabled(pipeline.YamlDefinition, request.Enabled);
        return change.Succeeded ? change : throw new BadRequestException(change.Error!);
    }

    public async Task<PipelineDto> ApplyAsync(
        PipelineDto pipeline, PipelineScheduleChangeResult change, bool enabled, CancellationToken ct)
    {
        if (string.Equals(change.Yaml, pipeline.YamlDefinition, StringComparison.Ordinal))
        {
            return pipeline;
        }

        // The same gate as PUT api/pipelines/{id}: what is refused there is refused here.
        var validation = await pipelineService.ValidateYamlStrictAsync(
            change.Yaml!, pipeline.ProjectId, pipeline.EnvironmentId, pipeline.ProjectServerId, ct: ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new BadRequestException(string.Join("; ", validation.Errors));
        }

        var updated = await pipelineService.UpdatePipelineAsync(pipeline.Id, new UpdatePipelineRequest
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = change.Yaml!,
            ProjectId = pipeline.ProjectId,
            SourceRepositoryId = pipeline.SourceRepositoryId,
            SourceBranch = pipeline.SourceBranch,
            EnvironmentId = pipeline.EnvironmentId,
            ProjectServerId = pipeline.ProjectServerId,
            AiRunnerProfileId = pipeline.AiRunnerProfileId
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Pipeline {pipeline.Id} was not found.");

        // The owner stays when a schedule moves, so this line is what says who moved it.
        var current = PipelineScheduleReader.Read(pipeline.YamlDefinition)?.Schedule ?? string.Empty;
        await audit.LogAsync(
            "ScheduleChanged",
            "Pipeline",
            pipeline.Id,
            $"'{current}' -> '{change.Cron ?? current}'; enabled: {pipeline.TriggerType == PipelineTriggerType.Schedule} -> {enabled}",
            ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<PipelineDto> ChangeSourceBranchAsync(
        PipelineDto pipeline, string branch, string? expectedBranch, CancellationToken ct)
    {
        if (!ScheduleDefaults.SourceBranchChoices.Contains(branch, StringComparer.Ordinal))
        {
            throw new BadRequestException(
                $"A pipeline's source branch is chosen between {string.Join(" and ", ScheduleDefaults.SourceBranchChoices)} here.");
        }

        if (pipeline.ProjectId is null)
        {
            throw new BadRequestException($"Pipeline '{pipeline.Name}' has no project repository to read a branch of.");
        }

        if (!string.Equals(pipeline.SourceBranch, expectedBranch, StringComparison.Ordinal))
        {
            throw new ConflictException(
                $"The source branch of pipeline '{pipeline.Name}' changed since it was read. Reload it before changing it.");
        }

        if (string.Equals(pipeline.SourceBranch, branch, StringComparison.Ordinal))
        {
            return pipeline;
        }

        var updated = await pipelineService.UpdatePipelineAsync(pipeline.Id, new UpdatePipelineRequest
        {
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = pipeline.YamlDefinition,
            ProjectId = pipeline.ProjectId,
            SourceRepositoryId = pipeline.SourceRepositoryId,
            SourceBranch = branch,
            EnvironmentId = pipeline.EnvironmentId,
            ProjectServerId = pipeline.ProjectServerId,
            AiRunnerProfileId = pipeline.AiRunnerProfileId
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Pipeline {pipeline.Id} was not found.");

        await audit.LogAsync(
            "SourceBranchChanged", "Pipeline", pipeline.Id,
            $"'{pipeline.SourceBranch ?? "default"}' -> '{branch}'", ct).ConfigureAwait(false);
        return updated;
    }
}
