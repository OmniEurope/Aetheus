// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AiTasks;

/// <summary>API runner profiles (ADR-023, amendment 2026-10-11): admin-only, audited, with the API key
/// encrypted at rest and never returned.</summary>
public interface IAiRunnerProfileService
{
    Task<PaginatedResult<AiRunnerProfileDto>> GetProfilesAsync(PaginationRequest request, CancellationToken ct);
    Task<List<AiRunnerProfileDto>> GetProfileOptionsAsync(List<int> organizationIds, CancellationToken ct);
    Task<AiRunnerProfileDto?> GetProfileAsync(int id, CancellationToken ct);
    Task<AiRunnerProfileDto> CreateProfileAsync(CreateAiRunnerProfileRequest request, CancellationToken ct);
    Task<AiRunnerProfileDto?> UpdateProfileAsync(int id, UpdateAiRunnerProfileRequest request, CancellationToken ct);
    Task<bool> DeleteProfileAsync(int id, CancellationToken ct);

    /// <summary>One tiny completion from the backend with the dialog's values (and the stored key when
    /// none is typed). The answer says whether the provider accepted the key, endpoint and model.</summary>
    Task<AiConnectionTestDto> TestConnectionAsync(TestAiRunnerProfileRequest request, CancellationToken ct);

    /// <summary>Recette R3-7: reads the saved profile's model list at its provider now, with its stored
    /// key, as the nightly pass does. Null when the profile does not exist; a
    /// <see cref="AiModelListRefreshAttempt.RetryAfter"/> when the list was read less than a minute ago.</summary>
    Task<AiModelListRefreshAttempt?> RefreshModelListAsync(int id, CancellationToken ct);
}
