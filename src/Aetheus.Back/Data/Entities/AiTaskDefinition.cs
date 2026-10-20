// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public sealed class AiTaskDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ProfileId { get; set; }
    public string PromptTemplate { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public int? ServerId { get; set; }
    public string? Schedule { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Workspace-relative files or directories attached to the prompt of a project task, as a
    /// JSON array (the pipeline step key <c>context_paths</c>). Null when none.</summary>
    public string? ContextPathsJson { get; set; }

    /// <summary>Commit range whose log and diff are attached to the prompt (the pipeline step key
    /// <c>git_range</c>), for example <c>HEAD~10..HEAD</c>. Null when none.</summary>
    public string? GitRange { get; set; }

    public DateTime? LastScheduledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AiRunnerProfile Profile { get; set; } = null!;
    public Project? Project { get; set; }
    public Server? Server { get; set; }
    public List<AiTaskTrigger> Triggers { get; set; } = [];
    public List<AiRunResult> Results { get; set; } = [];
}
