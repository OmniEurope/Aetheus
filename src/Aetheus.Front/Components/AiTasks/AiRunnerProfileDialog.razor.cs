// SPDX-License-Identifier: EUPL-1.2

using System.Globalization;

namespace Aetheus.Front.Components.AiTasks;

/// <summary>
/// Creates or edits an API runner profile. The stored API key never reaches the browser: the profile
/// only says whether one exists, an empty field keeps it, and "Test the connection" sends the typed
/// values to the backend, which uses the stored key when none is typed.
/// </summary>
public partial class AiRunnerProfileDialog
{
    // Model IDs are examples only; users may enter any provider-supported identifier.
    private const string AnthropicModelExample = "claude-sonnet-5-5";
    private const string OpenAiModelExample = "gpt-5.5";

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;

    [Parameter] public int? ProfileId { get; set; }

    private bool IsEdit => ProfileId.HasValue;
    private EditModel _model = new();
    private bool _busy;
    private bool _loading;
    private bool _testing;
    private bool _hasStoredKey;
    private bool _needsReconfiguration;
    private AiConnectionTestDto? _testResult;
    // Displays backend validation errors when a provider connection test is refused.
    private string? _testRefusal;

    private IReadOnlyList<OmniOption<AiProvider?>> ProviderOptions =>
    [
        new(AiProvider.Anthropic, L["AiProviderAnthropic"]),
        new(AiProvider.OpenAi, L["AiProviderOpenAi"])
    ];

    private IReadOnlyList<OmniOption<AiReasoningEffort>> EffortOptions =>
    [
        new(AiReasoningEffort.Default, L["AiEffortDefault"]),
        new(AiReasoningEffort.Low, L["AiEffortLow"]),
        new(AiReasoningEffort.Medium, L["AiEffortMedium"]),
        new(AiReasoningEffort.High, L["AiEffortHigh"])
    ];

    // Recette R3-7: the list the backend read at the provider with this profile's key, overnight.
    // It belongs to the provider the profile is saved with; another provider, a new profile, or a
    // list never read offers the application's fixed list instead.
    private List<string> _fetchedModels = [];
    private AiProvider? _fetchedProvider;
    private DateTime? _modelsFetchedAt;
    private DateTime? _modelListCheckedAt;
    private string? _modelListError;
    // True while "read the list now" waits for the provider: the model field is veiled.
    private bool _refreshingModels;

    private IReadOnlyList<string> ListedModels(AiProvider? provider) =>
        provider is not null && provider == _fetchedProvider && _fetchedModels.Count > 0
            ? _fetchedModels
            : AiProviderCatalog.KnownModels(provider ?? AiProvider.Anthropic);

    /// <summary>The "?" of the model field: how to pick, then where the list comes from.</summary>
    private string ModelHelp
    {
        get
        {
            var help = string.Format(L["AiModelHint"], ModelExample);
            var fromProvider = _model.Provider is not null && _model.Provider == _fetchedProvider && _fetchedModels.Count > 0;
            if (fromProvider && _modelsFetchedAt is { } fetched)
                help += " " + string.Format(L["AiModelListFromProvider"], fetched.ToString("g", CultureInfo.CurrentCulture));
            else
                help += " " + L["AiModelListFixed"];
            if (_modelListError is { Length: > 0 } error && _modelListCheckedAt is { } checkedAt)
                help += " " + string.Format(L["AiModelListFailed"], checkedAt.ToString("g", CultureInfo.CurrentCulture), error);
            return help;
        }
    }

    /// <summary>The suggestions of the model field: the list read at the provider (or the application's
    /// fixed one) filtered by what is typed. The field takes any text, so a model the list lacks is typed.</summary>
    private Task<IReadOnlyList<OmniOption<string>>> SearchModelsAsync(string text, CancellationToken cancellationToken)
    {
        IReadOnlyList<OmniOption<string>> found =
        [
            .. ListedModels(_model.Provider)
                .Where(known => string.IsNullOrWhiteSpace(text) || known.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(known => new OmniOption<string>(known, known))
        ];
        return Task.FromResult(found);
    }

    private bool IsKnownModel(AiProvider? provider, string model) =>
        ListedModels(provider).Contains(model, StringComparer.Ordinal);

    private string DefaultBaseUrl => AiProviderCatalog.DefaultBaseUrl(_model.Provider ?? AiProvider.Anthropic);

    private string ModelExample => _model.Provider == AiProvider.OpenAi ? OpenAiModelExample : AnthropicModelExample;

    // What the backend names a profile created without a name (before any -2 suffix): shown as a hint.
    private string GeneratedName => _model.Provider is { } provider && !string.IsNullOrWhiteSpace(_model.Model)
        ? $"{AiProviderCatalog.Token(provider)}-{_model.Model.Trim()}".ToLowerInvariant()
        : string.Empty;

    internal bool CanTest =>
        _model.Provider is not null
        && !string.IsNullOrWhiteSpace(_model.Model)
        && (_hasStoredKey || !string.IsNullOrWhiteSpace(_model.ApiKey));

    /// <summary>"Read the list now" reads the saved profile (its provider, URL and stored key), so it
    /// needs one saved with an API provider and a key.</summary>
    internal bool CanRefreshModelList => IsEdit && _fetchedProvider is not null && _hasStoredKey;

    protected override async Task OnInitializedAsync()
    {
        _model.L = L;
        if (!ProfileId.HasValue)
        {
            // A new profile starts on its provider's URL, nothing to type for the usual case.
            _model.BaseUrl = DefaultBaseUrl;
            return;
        }
        _loading = true;
        try
        {
            var profile = await Api.Ai.GetAiRunnerProfileAsync(ProfileId.Value);
            if (profile is not null)
            {
                _hasStoredKey = profile.HasApiKey;
                _needsReconfiguration = profile.NeedsReconfiguration;
                ShowModelList(profile);
                _model = new EditModel
                {
                    Name = profile.Name,
                    IsEdit = true,
                    L = L,
                    Description = profile.Description,
                    Provider = profile.Provider,
                    BaseUrl = profile.BaseUrl,
                    Model = profile.Model,
                    ReasoningEffort = profile.ReasoningEffort,
                    MaxOutputTokens = profile.MaxOutputTokens > 0 ? profile.MaxOutputTokens : 16_000,
                    TimeoutSeconds = profile.TimeoutSeconds,
                    MaxOutputBytes = profile.MaxOutputBytes,
                    SendsDataExternally = profile.SendsDataExternally,
                    Priority = profile.Priority
                };
            }
        }
        catch (HttpRequestException)
        {
            Notify.Error("Error", "LoadFailed");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            if (IsEdit)
            {
                var updated = await Api.Ai.UpdateAiRunnerProfileAsync(ProfileId!.Value, Fill(new UpdateAiRunnerProfileRequest()));
                if (updated is not null)
                {
                    Notify.Success("Updated");
                    Dialog.Close(true);
                }
            }
            else
            {
                var created = await Api.Ai.CreateAiRunnerProfileAsync(Fill(new CreateAiRunnerProfileRequest()));
                if (created is not null)
                {
                    Notify.Success("Created");
                    Dialog.Close(true);
                }
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A URL left on the previous provider's default (or empty) follows the new provider; a
    /// URL the user typed stays.</summary>
    private void OnProviderChanged(AiProvider? provider)
    {
        var followsDefault = string.IsNullOrWhiteSpace(_model.BaseUrl)
            || string.Equals(_model.BaseUrl.Trim(), DefaultBaseUrl, StringComparison.OrdinalIgnoreCase);
        var followsListedModel = IsKnownModel(_model.Provider, _model.Model);
        _model.Provider = provider;
        if (followsDefault) _model.BaseUrl = provider is null ? string.Empty : DefaultBaseUrl;
        // A listed model follows the provider (its default when the list read at the provider still has
        // it); a typed identifier stays.
        if (followsListedModel && provider is { } selected)
        {
            var listed = ListedModels(selected);
            var preferred = AiProviderCatalog.KnownModels(selected)[0];
            _model.Model = listed.Contains(preferred, StringComparer.Ordinal) ? preferred : listed[0];
        }
    }

    private void ShowModelList(AiRunnerProfileDto profile)
    {
        _fetchedModels = profile.AvailableModels;
        _fetchedProvider = profile.Provider;
        _modelsFetchedAt = profile.AvailableModelsFetchedAt;
        _modelListCheckedAt = profile.ModelListCheckedAt;
        _modelListError = profile.ModelListError;
    }

    /// <summary>Recette R3-7: reads the saved profile's list at the provider now, under the veil, then
    /// shows the new list, read time and error. The fields being edited are left as typed; the typed
    /// model stays whatever the new list holds, so nothing is lost.</summary>
    internal async Task RefreshModelListAsync()
    {
        if (!CanRefreshModelList || _refreshingModels) return;
        _refreshingModels = true;
        try
        {
            var outcome = await Api.Ai.RefreshAiRunnerProfileModelListAsync(ProfileId!.Value);
            if (outcome.Value is { } profile)
            {
                ShowModelList(profile);
                if (string.IsNullOrWhiteSpace(profile.ModelListError)) Notify.Success("AiModelListRefreshed");
                else Notify.Notify(OmniSeverity.Warning, "AiModelListRefreshFailed", profile.ModelListError);
            }
            else if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage))
            {
                // A 400 is the caller's to explain; a 429 or another failure is already shown by the
                // global error notifier with the backend's reason.
                Notify.Notify(OmniSeverity.Warning, "AiModelListRefreshFailed", outcome.ErrorMessage);
            }
        }
        finally
        {
            _refreshingModels = false;
        }
    }

    internal async Task TestAsync()
    {
        if (!CanTest) return;
        _testing = true;
        _testResult = null;
        _testRefusal = null;
        try
        {
            var outcome = await Api.Ai.TestAiRunnerProfileAsync(
                Fill(new TestAiRunnerProfileRequest()) with { ProfileId = ProfileId });
            _testResult = outcome.Value;
            // The detail (provider message, duration) stays in the dialog; the toast gives the outcome.
            if (_testResult?.Succeeded == true) Notify.Success("AiConnectionSucceeded");
            else if (_testResult is not null) Notify.Warning("AiConnectionFailed", "AiConnectionFailedDetail");
            else
                _testRefusal = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                    ? string.Format(L["AiConnectionTestRefused"], (int?)outcome.StatusCode)
                    : outcome.ErrorMessage;
        }
        finally
        {
            _testing = false;
        }
    }

    private T Fill<T>(T request) where T : AiRunnerProfileRequest => request with
    {
        Name = _model.Name,
        Description = _model.Description,
        Provider = _model.Provider,
        BaseUrl = _model.BaseUrl.Trim(),
        Model = _model.Model.Trim(),
        // Empty keeps the stored key; the browser never had it.
        ApiKey = _model.ApiKey,
        ReasoningEffort = _model.ReasoningEffort,
        MaxOutputTokens = _model.MaxOutputTokens,
        TimeoutSeconds = _model.TimeoutSeconds,
        MaxOutputBytes = _model.MaxOutputBytes,
        SendsDataExternally = _model.SendsDataExternally,
        Priority = _model.Priority
    };

    private void Cancel() => Dialog.Close(false);

    private sealed class EditModel : IValidatableObject
    {
        // Empty on create names the profile after its provider and model (recette R3-5); an existing
        // profile keeps a name.
        [StringLength(120)]
        public string Name { get; set; } = string.Empty;

        public bool IsEdit { get; set; }

        internal IStringLocalizer? L { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (IsEdit && string.IsNullOrWhiteSpace(Name))
                yield return new ValidationResult(L!["Required"], [nameof(Name)]);
        }

        [StringLength(500)]
        public string Description { get; set; } = string.Empty;

        [Required]
        public AiProvider? Provider { get; set; } = AiProvider.Anthropic;

        [StringLength(500)]
        public string BaseUrl { get; set; } = string.Empty;

        [Required, StringLength(200)]
        public string Model { get; set; } = AnthropicModelExample;

        [StringLength(4096)]
        public string ApiKey { get; set; } = string.Empty;

        public AiReasoningEffort ReasoningEffort { get; set; } = AiReasoningEffort.Low;

        [Range(256, 128_000)]
        public int MaxOutputTokens { get; set; } = 16_000;

        [Range(1, 86_400)]
        public int TimeoutSeconds { get; set; } = 600;

        [Range(1024, 1_000_000)]
        public int MaxOutputBytes { get; set; } = 200_000;

        public bool SendsDataExternally { get; set; } = true;

        [Range(AiProfilePriority.Minimum, AiProfilePriority.Maximum)]
        public int Priority { get; set; } = AiProfilePriority.Default;
    }
}
