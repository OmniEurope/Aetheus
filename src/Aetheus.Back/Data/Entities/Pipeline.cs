// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aetheus.Back.Data.Entities;

public class Pipeline
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string YamlDefinition { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? TemplateReferenceName { get; set; }
    public int? TemplateReferenceVersion { get; set; }
    public PipelineTriggerType TriggerType { get; set; } = PipelineTriggerType.Manual;

    // Exactly-one-owner: exactly one of ProjectId / EnvironmentId / ProjectServerId is set
    // (legacy rows may have all null until re-saved). Enforced in the service layer.
    public int? ProjectId { get; set; }
    public int? SourceRepositoryId { get; set; }
    [MaxLength(255)]
    public string? SourceBranch { get; set; }
    public int? EnvironmentId { get; set; }
    public int? ProjectServerId { get; set; }

    /// <summary>The AI runner profile this pipeline's <c>ai</c> steps use when the step names none that
    /// can run. Null means automatic (the organization's most preferred usable profile). The profile
    /// belongs to the pipeline's organization (checked by the service); deleting it clears this.</summary>
    public int? AiRunnerProfileId { get; set; }

    // F-EXEC-1b: the user who created (or last re-saved an unowned) pipeline. Non-interactive
    // triggers (webhook / scheduler) have no ClaimsPrincipal, so automated runs are authorized
    // against this owner's permissions. Nullable: legacy rows have no owner and their automated
    // runs are denied fail-closed until an authorized user re-saves the pipeline.
    [MaxLength(256)]
    public string? CreatedByUsername { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Highest build number issued to a run of this pipeline. Monotonic and independent of run
    /// retention, so an application version derived from it never repeats or goes backwards even
    /// after old runs are purged. Reserved under a row lock (see
    /// <c>PipelineCoreRepository.ReserveNextBuildNumberAsync</c>) and snapshotted onto the run as
    /// <see cref="PipelineRun.BuildNumber"/>.
    /// </summary>
    public int BuildCounter { get; set; }

    /// <summary>
    /// The latest cron occurrence of this pipeline's schedule the scheduler took in charge (started, or
    /// skipped on purpose), UTC. It lives in the database so that an occurrence is decided once across
    /// ticks, restarts and replicas, and so that a new leader knows where the previous one stopped and
    /// can catch up an occurrence missed in between. Written only by the scheduler through a
    /// conditional bulk update (never by a save, so <see cref="UpdatedAt"/> and <see cref="RowVersion"/>
    /// do not move); null until the scheduler first sees the pipeline.
    /// </summary>
    public DateTime? LastScheduledOccurrence { get; set; }

    /// <summary>R-11 (decision of 2026-11-10, option A): the <c>.pipeline/</c> file of the external repository's mirror
    /// this pipeline was imported from, read-only. Null for every pipeline defined in Aetheus. An imported pipeline
    /// runs its stored definition (never the mirror's current file), is never edited in Aetheus and never fires on
    /// its own: see <c>PipelineExternalImportPolicy</c>.</summary>
    [MaxLength(255)]
    public string? ExternalImportPath { get; set; }

    /// <summary>The mirror commit the imported definition was read at (immutable).</summary>
    [MaxLength(64)]
    public string? ExternalImportCommit { get; set; }

    /// <summary>SHA-256 of the imported file, normalized as <c>PipelineRepositoryDefinitionGuard.Sha256</c>: a re-import
    /// of the same content is a no-op, a different content means the mirror's definition changed since import.</summary>
    [MaxLength(64)]
    public string? ExternalImportSha256 { get; set; }

    public DateTime? ExternalImportedAt { get; set; }

    [MaxLength(256)]
    public string? ExternalImportedBy { get; set; }

    /// <summary>True for a pipeline imported read-only from an external repository's mirror.</summary>
    [NotMapped]
    public bool IsExternalImport => ExternalImportCommit is not null;

    [ConcurrencyCheck]
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    // Navigation
    public Project? Project { get; set; }
    public GitInternalRepo? SourceRepository { get; set; }
    public Environment? Environment { get; set; }
    public ProjectServer? ProjectServer { get; set; }
    public AiRunnerProfile? AiRunnerProfile { get; set; }
    public List<PipelineRun> Runs { get; set; } = [];
}
