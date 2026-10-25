// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// API runner profiles. A profile names a provider, an https base URL, a model and an API key; the key
/// is encrypted with <see cref="IEncryptionService"/> (as the retired CLI environment was), reported
/// only as present or absent, and kept when an update sends it back empty or masked. The retired CLI
/// columns are still written (empty, <c>[]</c>, encrypted <c>{}</c>) for the previous blue-green
/// colour, which maps them NOT NULL.
/// </summary>
public sealed class AiRunnerProfileService(
    IAiTaskRepository repo,
    IOrganizationRepository organizations,
    IAuditService audit,
    IEncryptionService encryption,
    IHttpClientFactory httpClientFactory,
    AiModelListReader modelListReader,
    TimeProvider timeProvider) : IAiRunnerProfileService
{
    /// <summary>Backend client of the connection test: SSRF-guarded at connect time, no redirects.</summary>
    public const string TestHttpClientName = "ai-provider-test";

    private const int TestTimeoutSeconds = 60;

    /// <summary>The shortest time between two reads of one profile's model list, whatever started
    /// them (the dialog's "read now" or the nightly pass): each read is a call to the provider with
    /// the profile's key.</summary>
    internal static readonly TimeSpan ModelListReadInterval = TimeSpan.FromMinutes(1);

    public async Task<PaginatedResult<AiRunnerProfileDto>> GetProfilesAsync(
        PaginationRequest request, CancellationToken ct)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetProfilesPageAsync(
            request.Search, page, pageSize, ct, request.Filters,
            request.SortBy, request.SortDescending).ConfigureAwait(false);
        return new PaginatedResult<AiRunnerProfileDto>
        {
            Items = items.Select(MapProfile).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AiRunnerProfileDto?> GetProfileAsync(int id, CancellationToken ct)
    {
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        return profile is null ? null : MapProfile(profile);
    }

    public async Task<List<AiRunnerProfileDto>> GetProfileOptionsAsync(
        List<int> organizationIds, CancellationToken ct) =>
        (await repo.GetProfilesForOrganizationsAsync(organizationIds, ct).ConfigureAwait(false))
            .Select(MapProfile)
            .ToList();

    public async Task<AiRunnerProfileDto> CreateProfileAsync(
        CreateAiRunnerProfileRequest request, CancellationToken ct)
    {
        var (provider, baseUrl) = ValidateProfile(request);
        if (IsKeepOrEmpty(request.ApiKey))
            throw new BadRequestException("An API key is required.");
        var organizationId = request.OrganizationId
            ?? await organizations.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization available.");
        var profile = new AiRunnerProfile
        {
            OrganizationId = organizationId,
            Binary = string.Empty,
            ArgsTemplateJson = "[]",
            EnvironmentJsonEncrypted = encryption.EncryptValue("{}")
        };
        Apply(profile, request, provider, baseUrl);
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = await GenerateNameAsync(organizationId, provider, profile.Model, ct).ConfigureAwait(false);
        profile.ApiKeyEncrypted = encryption.EncryptValue(request.ApiKey.Trim());
        await repo.AddProfileAsync(profile, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "AiRunnerProfile", profile.Id, profile.Name, ct)
            .ConfigureAwait(false);
        return MapProfile(profile);
    }

    public async Task<AiRunnerProfileDto?> UpdateProfileAsync(
        int id, UpdateAiRunnerProfileRequest request, CancellationToken ct)
    {
        var (provider, baseUrl) = ValidateProfile(request);
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        if (profile is null) return null;
        if (IsKeepOrEmpty(request.ApiKey) && string.IsNullOrWhiteSpace(profile.ApiKeyEncrypted))
            throw new BadRequestException("An API key is required.");
        var previousTarget = (profile.Provider, profile.BaseUrl);
        Apply(profile, request, provider, baseUrl);
        var keyChanged = !IsKeepOrEmpty(request.ApiKey);
        if (keyChanged)
            profile.ApiKeyEncrypted = encryption.EncryptValue(request.ApiKey.Trim());
        // Recette R3-7: the nightly model list belongs to one provider, endpoint and key. Another one
        // drops it, so the dialog offers the fixed list until the next night reads the new one.
        if (keyChanged || previousTarget != (profile.Provider, profile.BaseUrl))
            ForgetModelList(profile);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "AiRunnerProfile", profile.Id, profile.Name, ct)
            .ConfigureAwait(false);
        return MapProfile(profile);
    }

    public async Task<bool> DeleteProfileAsync(int id, CancellationToken ct)
    {
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        if (profile is null) return false;
        if (await repo.ProfileHasDefinitionsAsync(id, ct).ConfigureAwait(false))
            throw new ConflictException("The AI runner profile is used by one or more task definitions.");
        await repo.RemoveProfileAsync(profile, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "AiRunnerProfile", id, profile.Name, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<AiConnectionTestDto> TestConnectionAsync(
        TestAiRunnerProfileRequest request, CancellationToken ct)
    {
        var (provider, baseUrl) = ValidateProfile(request);
        var apiKey = request.ApiKey.Trim();
        if (IsKeepOrEmpty(apiKey))
        {
            var stored = request.ProfileId is { } profileId
                ? await repo.FindProfileAsync(profileId, ct).ConfigureAwait(false)
                : null;
            if (stored is null || string.IsNullOrWhiteSpace(stored.ApiKeyEncrypted))
                throw new BadRequestException("An API key is required.");
            // The stored key only goes where the profile already sends it: otherwise an administrator could
            // point the test at a host they control and read the decrypted key there.
            if (!SameBaseUrl(baseUrl, AiProfileRuntime.BaseUrl(stored)))
                throw new BadRequestException(
                    "The stored API key is only sent to the base URL of the saved profile. Type the API key again to test another URL.");
            apiKey = encryption.DecryptValue(stored.ApiKeyEncrypted);
        }
        var client = new AiProviderClient(httpClientFactory.CreateClient(TestHttpClientName));
        var stopwatch = Stopwatch.StartNew();
        var outcome = await client.CompleteAsync(
            new AiProviderSettings(provider, baseUrl, apiKey),
            new AiCompletionRequest(
                request.Model.Trim(),
                "You are a connectivity check. Answer with the single word OK.",
                "Connectivity check.",
                // Enough room for a model that thinks before answering; the call is about reaching it.
                1024,
                request.ReasoningEffort),
            TimeSpan.FromSeconds(Math.Min(request.TimeoutSeconds, TestTimeoutSeconds)),
            ct).ConfigureAwait(false);
        stopwatch.Stop();
        await audit.LogAsync(
            outcome.Succeeded ? "ConnectionTestSucceeded" : "ConnectionTestFailed",
            "AiRunnerProfile", request.ProfileId ?? 0, $"{request.Name.Trim()} (host {baseUrl.Host})", ct).ConfigureAwait(false);
        var warnings = outcome.Warnings.Count == 0 ? string.Empty : " " + string.Join(" ", outcome.Warnings);
        return new AiConnectionTestDto
        {
            Succeeded = outcome.Succeeded,
            Message = AiProviderClient.Mask(
                (outcome.Succeeded
                    ? $"The provider answered with model '{request.Model.Trim()}'."
                    : outcome.Error ?? "The provider did not answer.") + warnings,
                apiKey),
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<AiModelListRefreshAttempt?> RefreshModelListAsync(int id, CancellationToken ct)
    {
        var profile = await repo.FindProfileAsync(id, ct).ConfigureAwait(false);
        if (profile is null) return null;
        if (!AiProviderCatalog.TryParse(profile.Provider, out var provider))
            throw new BadRequestException("Only an API profile reads its model list at the provider.");
        if (string.IsNullOrWhiteSpace(profile.ApiKeyEncrypted))
            throw new BadRequestException("An API key is required.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Recette R3-7: the last attempt is stored on the profile, so the limit holds across clicks,
        // browsers and backend instances without any state of its own.
        if (profile.ModelListCheckedAt is { } lastRead && now - lastRead < ModelListReadInterval)
            return new AiModelListRefreshAttempt(MapProfile(profile), ModelListReadInterval - (now - lastRead));
        var read = await modelListReader.ReadIntoAsync(profile, provider, encryption, now, ct).ConfigureAwait(false);
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync(
            read ? "ModelListRefreshed" : "ModelListRefreshFailed",
            "AiRunnerProfile", profile.Id, profile.Name, ct).ConfigureAwait(false);
        return new AiModelListRefreshAttempt(MapProfile(profile), null);
    }

    private static (AiProvider Provider, Uri BaseUrl) ValidateProfile(AiRunnerProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Provider is not { } provider || !Enum.IsDefined(provider))
            throw new BadRequestException("The AI provider is not supported.");
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new BadRequestException("The AI model is required.");
        var baseUrlText = string.IsNullOrWhiteSpace(request.BaseUrl)
            ? AiProviderCatalog.DefaultBaseUrl(provider)
            : request.BaseUrl;
        if (!AiProviderCatalog.IsValidBaseUrl(baseUrlText, out var baseUrl))
            throw new BadRequestException("The AI base URL must be an absolute https URL without credentials, query or fragment.");
        if (!Enum.IsDefined(request.ReasoningEffort))
            throw new BadRequestException("The reasoning effort is not supported.");
        return (provider, baseUrl!);
    }

    private static void Apply(
        AiRunnerProfile profile, AiRunnerProfileRequest request, AiProvider provider, Uri baseUrl)
    {
        profile.Name = request.Name.Trim();
        profile.Description = request.Description.Trim();
        profile.Provider = AiProviderCatalog.Token(provider);
        // An empty field, or the provider default the dialog now prefills, keeps following the provider
        // default instead of freezing today's value.
        var followsDefault = string.IsNullOrWhiteSpace(request.BaseUrl)
            || SameBaseUrl(baseUrl, AiProviderCatalog.DefaultBaseUrl(provider));
        profile.BaseUrl = followsDefault ? string.Empty : baseUrl.AbsoluteUri.TrimEnd('/');
        profile.Model = request.Model.Trim();
        profile.ReasoningEffort = AiEffortTokens.Of(request.ReasoningEffort) ?? string.Empty;
        profile.MaxOutputTokens = Math.Clamp(request.MaxOutputTokens, 256, 128_000);
        profile.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 86_400);
        profile.MaxOutputBytes = Math.Clamp(request.MaxOutputBytes, 1024, 1_000_000);
        profile.SendsDataExternally = request.SendsDataExternally;
        profile.Priority = Math.Clamp(request.Priority, AiProfilePriority.Minimum, AiProfilePriority.Maximum);
    }

    /// <summary>Recette R3-5: a profile created without a name is called after its provider and model
    /// (<c>anthropic-claude-sonnet-5-5</c>), with -2, -3... when the organization already uses that name.</summary>
    private async Task<string> GenerateNameAsync(int organizationId, AiProvider provider, string model, CancellationToken ct)
    {
        var stem = ProfileNameStem(provider, model);
        var taken = (await repo.GetProfilesForOrganizationsAsync([organizationId], ct).ConfigureAwait(false))
            .Select(existing => existing.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var suffix = 2; taken.Contains(name); suffix++)
            name = $"{stem}-{suffix}";
        return name;
    }

    internal static string ProfileNameStem(AiProvider provider, string model)
    {
        var raw = $"{AiProviderCatalog.Token(provider)}-{model}".ToLowerInvariant();
        var cleaned = new string(raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray()).Trim('-');
        // Leaves room for a "-NN" suffix within the 120 characters of the name column.
        return cleaned.Length > 110 ? cleaned[..110] : cleaned;
    }

    private static bool SameBaseUrl(Uri requested, string storedBaseUrl) =>
        AiProviderCatalog.IsValidBaseUrl(storedBaseUrl, out var stored)
        && string.Equals(
            requested.AbsoluteUri.TrimEnd('/'), stored!.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    private static void ForgetModelList(AiRunnerProfile profile)
    {
        profile.AvailableModelsJson = null;
        profile.AvailableModelsFetchedAt = null;
        profile.ModelListCheckedAt = null;
        profile.ModelListError = null;
    }

    private static bool IsKeepOrEmpty(string apiKey) =>
        string.IsNullOrWhiteSpace(apiKey)
        || string.Equals(apiKey.Trim(), AiRunnerProfileRequest.KeepStoredApiKey, StringComparison.Ordinal);

    internal static AiRunnerProfileDto MapProfile(AiRunnerProfile profile) => new()
    {
        Id = profile.Id,
        OrganizationId = profile.OrganizationId,
        Name = profile.Name,
        Description = profile.Description,
        Provider = AiProviderCatalog.TryParse(profile.Provider, out var provider) ? provider : null,
        BaseUrl = profile.BaseUrl,
        Model = profile.Model,
        HasApiKey = !string.IsNullOrWhiteSpace(profile.ApiKeyEncrypted),
        ReasoningEffort = AiEffortTokens.Parse(profile.ReasoningEffort),
        MaxOutputTokens = profile.MaxOutputTokens,
        TimeoutSeconds = profile.TimeoutSeconds,
        MaxOutputBytes = profile.MaxOutputBytes,
        SendsDataExternally = profile.SendsDataExternally,
        Priority = profile.Priority,
        NeedsReconfiguration = AiProfileRuntime.NeedsReconfiguration(profile),
        AvailableModels = AiModelListRefreshService.ReadModels(profile.AvailableModelsJson),
        AvailableModelsFetchedAt = profile.AvailableModelsFetchedAt,
        ModelListCheckedAt = profile.ModelListCheckedAt,
        ModelListError = profile.ModelListError,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt
    };
}
