// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

/// <summary>Project retention fields inherit the profile value when left empty.</summary>
public partial class ProjectDataRetentionSection
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private DataRetentionFormModel? _model;
    private ProjectDataRetentionDto? _saved;
    private int? _loadedProjectId;
    private bool _loadFailed;
    private bool _saving;
    private List<OmniOption<DataRetentionProfile>> _profileOptions = [];

    private DataRetentionValues Defaults => DataRetentionProfiles.Of(_model?.Profile ?? DataRetentionProfile.Standard);

    protected override async Task OnParametersSetAsync()
    {
        _profileOptions = [.. DataRetentionProfiles.Ordered.Select(profile =>
            new OmniOption<DataRetentionProfile>(profile, L[$"Enum_DataRetentionProfile_{profile}"].Value))];
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        try
        {
            var retention = await Api.Projects.GetDataRetentionAsync(ProjectId);
            _saved = retention;
            _model = retention is null ? null : DataRetentionFormModel.From(retention);
            _loadFailed = retention is null;
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
    }

    /// <summary>The interval saved for the project, as the telemetry views state it: what its applications
    /// are told, not what the form holds before it is saved.</summary>
    private string? SavedIntervalText => _saved is null
        ? null
        : TelemetrySendIntervalText.Setting(L, _saved.Effective.TelemetryExportIntervalMinutes * 60, _saved.Profile,
            _saved.Overrides.TelemetryExportIntervalMinutes.HasValue);

    /// <summary>The send interval field label: the field name, then the saved state once there is one.</summary>
    private string ExportIntervalLabel => SavedIntervalText is { } saved
        ? L["DataRetentionExportIntervalCurrent", L["DataRetentionExportInterval"].Value, saved].Value
        : L["DataRetentionExportInterval"].Value;

    private string ProfileValue(int value) =>
        L["DataRetentionProfileValue", value.ToString(CultureInfo.CurrentCulture)].Value;

    private string DefaultValue(int value) =>
        L["DataRetentionDefaultValue", value.ToString(CultureInfo.CurrentCulture)].Value;

    private async Task OnSubmit()
    {
        _saving = true;
        var saved = await Api.Projects.UpdateDataRetentionAsync(ProjectId, _model!.ToRequest());
        if (saved is not null)
        {
            _saved = saved;
            _model = DataRetentionFormModel.From(saved);
            Toast.Success("Saved", "DataRetentionSaved");
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
        _saving = false;
    }
}
