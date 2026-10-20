// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.AiTasks;

public sealed record AiRunnerProfileDto
{
    public int Id { get; init; }
    public int OrganizationId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Null for a profile written before the API design (a CLI profile): it cannot run until
    /// it is reconfigured, see <see cref="NeedsReconfiguration"/>.</summary>
    public AiProvider? Provider { get; init; }

    /// <summary>The base URL the profile set, empty when it uses the provider default.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>True when an API key is stored. The key itself never leaves the backend.</summary>
    public bool HasApiKey { get; init; }

    public AiReasoningEffort ReasoningEffort { get; init; }
    public int MaxOutputTokens { get; init; }
    public int TimeoutSeconds { get; init; }
    public int MaxOutputBytes { get; init; }
    public bool SendsDataExternally { get; init; }

    /// <summary>Which profile a pipeline AI step falls back on: the lowest value is preferred.</summary>
    public int Priority { get; init; }

    /// <summary>A profile that has no provider, model or key yet (a former CLI profile): shown as "to
    /// reconfigure", refused when a task or a step uses it.</summary>
    public bool NeedsReconfiguration { get; init; }

    /// <summary>Recette R3-7: the model identifiers the provider listed for this profile's key at the
    /// last successful nightly read, empty when none was read yet. The dialog falls back to
    /// <see cref="AiProviderCatalog.KnownModels"/> when it is empty.</summary>
    public List<string> AvailableModels { get; init; } = [];

    /// <summary>When <see cref="AvailableModels"/> was read; null when it never was.</summary>
    public DateTime? AvailableModelsFetchedAt { get; init; }

    /// <summary>When the nightly read was last attempted, successful or not.</summary>
    public DateTime? ModelListCheckedAt { get; init; }

    /// <summary>Why the last nightly read failed (API key masked), null when it succeeded.</summary>
    public string? ModelListError { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public abstract record AiRunnerProfileRequest
{
    /// <summary>Sent back by the client in place of a stored key: keep the stored one.</summary>
    public const string KeepStoredApiKey = "***";

    /// <summary>Required to save an existing profile (the update record says so); empty on create
    /// generates one, and a connection test needs none.</summary>
    [StringLength(120)]
    public virtual string Name { get; init; } = string.Empty;

    [StringLength(500)]
    public string Description { get; init; } = string.Empty;

    [Required]
    public AiProvider? Provider { get; init; }

    /// <summary>Empty uses the provider default; otherwise an https URL (checked by the service).</summary>
    [StringLength(500)]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Model { get; init; } = string.Empty;

    /// <summary>Required on create. On update, empty or <see cref="KeepStoredApiKey"/> keeps the stored key.</summary>
    [StringLength(4096)]
    public string ApiKey { get; init; } = string.Empty;

    public AiReasoningEffort ReasoningEffort { get; init; } = AiReasoningEffort.Low;

    [Range(256, 128_000)]
    public int MaxOutputTokens { get; init; } = 16_000;

    [Range(1, 86400)]
    public int TimeoutSeconds { get; init; } = 600;

    [Range(1024, 1_000_000)]
    public int MaxOutputBytes { get; init; } = 200_000;

    public bool SendsDataExternally { get; init; } = true;

    /// <summary>Lower is preferred when a pipeline AI step names no profile that can run.</summary>
    [Range(AiProfilePriority.Minimum, AiProfilePriority.Maximum)]
    public int Priority { get; init; } = AiProfilePriority.Default;
}

/// <summary>The bounds of <see cref="AiRunnerProfileRequest.Priority"/>, shared by the request, the
/// dialog and the entity default.</summary>
public static class AiProfilePriority
{
    public const int Minimum = 0;
    public const int Maximum = 100_000;
    public const int Default = 100;
}

/// <summary>An empty <see cref="AiRunnerProfileRequest.Name"/> is named after the provider and model
/// (recette R3-5).</summary>
public sealed record CreateAiRunnerProfileRequest : AiRunnerProfileRequest
{
    public int? OrganizationId { get; init; }
}

public sealed record UpdateAiRunnerProfileRequest : AiRunnerProfileRequest
{
    [Required]
    [StringLength(120)]
    public override string Name { get; init; } = string.Empty;
}

/// <summary>"Test the connection" on the values of the dialog. With <see cref="ProfileId"/> and no new
/// key, the stored key of that profile is used: the browser never receives it.</summary>
public sealed record TestAiRunnerProfileRequest : AiRunnerProfileRequest
{
    [Range(1, int.MaxValue)]
    public int? ProfileId { get; init; }
}

public sealed record AiConnectionTestDto
{
    public bool Succeeded { get; init; }

    /// <summary>The provider error, or a short confirmation; never the key.</summary>
    public string Message { get; init; } = string.Empty;

    public long DurationMs { get; init; }
}

public sealed record AiTaskDefinitionDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public bool ProfileSendsDataExternally { get; init; }
    public string PromptTemplate { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public int? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? Schedule { get; init; }
    public bool Enabled { get; init; }
    public List<string> EventTypes { get; init; } = [];
    public List<string> ContextPaths { get; init; } = [];
    public string? GitRange { get; init; }
    public DateTime? LastScheduledAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public abstract record AiTaskDefinitionRequest : IValidatableObject
{
    [Required]
    [StringLength(120)]
    public string Name { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ProfileId { get; init; }

    [Required]
    [StringLength(20_000)]
    public string PromptTemplate { get; init; } = string.Empty;

    public int? ProjectId { get; init; }
    public int? ServerId { get; init; }

    [StringLength(120)]
    public string? Schedule { get; init; }

    public bool Enabled { get; init; } = true;

    [MaxItemStringLength(120)]
    public List<string> EventTypes { get; init; } = [];

    /// <summary>Workspace-relative files or directories of the project repository attached to the prompt
    /// (at most 32, the pipeline <c>context_paths</c> rule). Project tasks only.</summary>
    [MaxItemStringLength(260)]
    public List<string> ContextPaths { get; init; } = [];

    /// <summary>Commit range <c>A..B</c> whose log and diff are attached to the prompt, for example
    /// <c>HEAD~10..HEAD</c>. Project tasks only; null or empty for none.</summary>
    [StringLength(200)]
    public string? GitRange { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProjectId.HasValue == ServerId.HasValue)
            yield return new ValidationResult(
                "Exactly one owner must be set: ProjectId or ServerId.",
                [nameof(ProjectId), nameof(ServerId)]);
    }
}

public sealed record CreateAiTaskDefinitionRequest : AiTaskDefinitionRequest;

public sealed record UpdateAiTaskDefinitionRequest : AiTaskDefinitionRequest;

public sealed record AiRunResultDto
{
    public int Id { get; init; }
    public int ServerTaskId { get; init; }
    public int? PipelineRunId { get; init; }
    public int? AiTaskDefinitionId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public bool SendsDataExternally { get; init; }
    public string ReportMarkdown { get; init; } = string.Empty;
    public AiVerdict Verdict { get; init; }
    public string? DiffPatch { get; init; }
    public long DurationMs { get; init; }
    public bool Truncated { get; init; }
    public bool Succeeded { get; init; }
    public int? ProjectId { get; init; }
    public int? SourceRepositoryId { get; init; }
    public string? BaseCommitSha { get; init; }
    public int? ProposedRepositoryId { get; init; }
    public string? ProposedBranchName { get; init; }
    public string? ProposedCommitSha { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record AiRunStartDto
{
    public int TaskId { get; init; }
}

public sealed record PublishAiRunResultRequest
{
    [Range(1, int.MaxValue)]
    public int ServerTaskId { get; init; }

    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string ProfileName { get; init; } = string.Empty;
    public bool SendsDataExternally { get; init; }

    [Required]
    [StringLength(1_000_000)]
    public string ReportMarkdown { get; init; } = string.Empty;

    public AiVerdict Verdict { get; init; }

    [Range(0, long.MaxValue)]
    public long DurationMs { get; init; }

    [Range(1, int.MaxValue)]
    public int? SourceRepositoryId { get; init; }

    [StringLength(64, MinimumLength = 40)]
    public string? BaseCommitSha { get; init; }

    public bool Truncated { get; init; }
    public bool Succeeded { get; init; }
}

public sealed record AiConsumptionDto
{
    public int RunCount { get; init; }
    public int FailedCount { get; init; }
    public long DurationMs { get; init; }
    public List<AiProfileConsumptionDto> Profiles { get; init; } = [];
}

public sealed record AiProfileConsumptionDto
{
    public string ProfileName { get; init; } = string.Empty;
    public int RunCount { get; init; }
    public int FailedCount { get; init; }
    public long DurationMs { get; init; }
}
