// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public sealed class AiRunnerProfile
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Preserves the CLI binary column required as NOT NULL by the preceding blue-green
    /// release, writing an empty value (ADR-023). Schema contraction is pending in
    /// docs/contracts/deployment.md.</summary>
    public string Binary { get; set; } = string.Empty;

    /// <summary>Writes an empty array to the compatibility column until the contraction described
    /// by <see cref="Binary"/>.</summary>
    public string ArgsTemplateJson { get; set; } = "[]";

    /// <summary>Writes an encrypted empty object so the preceding blue-green release can decrypt
    /// the compatibility column; see <see cref="Binary"/> for contraction.</summary>
    public string EnvironmentJsonEncrypted { get; set; } = string.Empty;

    /// <summary><see cref="Aetheus.Shared.Components.AiTasks.AiProviderCatalog"/> token; empty on a
    /// profile written by the CLI design, which then reads as "to reconfigure".</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Empty means the provider's default base URL.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>AES-encrypted API key (<c>IEncryptionService</c>); empty when none is stored.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    /// <summary>low, medium, high, or empty for the provider default.</summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    public int MaxOutputTokens { get; set; }
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxOutputBytes { get; set; } = 200_000;
    public bool SendsDataExternally { get; set; }

    /// <summary>Which profile a pipeline AI step falls back on when it names none that can run: the
    /// lowest value is preferred, ties go to the lowest id. The expand migration gives the column a
    /// database default of 100 so the previous blue-green colour can still insert.</summary>
    public int Priority { get; set; } = AiProfilePriority.Default;

    /// <summary>Recette R3-7: JSON array of the model identifiers the provider listed for this key at
    /// the last successful nightly read (<c>AiModelListRefreshService</c>); null until one succeeded.
    /// Nullable, so the previous blue-green colour, which does not map it, still inserts.</summary>
    public string? AvailableModelsJson { get; set; }

    /// <summary>When <see cref="AvailableModelsJson"/> was read.</summary>
    public DateTime? AvailableModelsFetchedAt { get; set; }

    /// <summary>When the nightly read was last attempted, successful or not: one attempt per night.</summary>
    public DateTime? ModelListCheckedAt { get; set; }

    /// <summary>Why the last attempt failed, API key masked; null after a success.</summary>
    public string? ModelListError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
    public List<AiTaskDefinition> TaskDefinitions { get; set; } = [];
}
