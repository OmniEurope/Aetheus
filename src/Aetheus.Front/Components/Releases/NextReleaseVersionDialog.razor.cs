// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Releases;

/// <summary>Picks the version series and the release number the next release takes; closes with a
/// <see cref="SetNextReleaseNumberRequest"/>, or nothing when dismissed. The series offered first is
/// the project's newest one, with the number it hands out next.</summary>
public partial class NextReleaseVersionDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The project's version series, newest first.</summary>
    [Parameter] public List<ProjectReleaseSeriesDto> KnownSeries { get; set; } = [];

    /// <summary>The project whose next release number is fixed.</summary>
    [Parameter] public int ProjectId { get; set; }

    private NextVersionModel _model = new();
    private bool _closed;
    private bool _saving;
    private string? _refusal;

    protected override void OnInitialized() =>
        _model = KnownSeries.Count > 0
            ? new NextVersionModel { Series = KnownSeries[0].Series, NextNumber = KnownSeries[0].NextNumber }
            : new NextVersionModel();

    /// <summary>The version the next release gets, null while the series is not major.minor.</summary>
    internal string? Version => _model.Series?.Trim() is { } series && ReleaseSeries.IsValid(series)
        ? $"{series}.{_model.NextNumber.ToString(CultureInfo.InvariantCulture)}"
        : null;

    private string? SeriesHelp => KnownSeries.Count == 0
        ? L["NextVersionSeriesHelp"].Value
        : string.Format(
            L["NextVersionSeriesKnown"].Value,
            string.Join(", ", KnownSeries.Select(item => $"{item.Series} ({item.NextNumber})")));

    /// <summary>Sends the number to the API. The dialog closes with the saved series; a number an earlier
    /// run already took (409) keeps it open with the reason, so the user can pick another number.</summary>
    private async Task Submit()
    {
        if (_closed || _saving) return;
        _saving = true;
        _refusal = null;
        try
        {
            var request = new SetNextReleaseNumberRequest { Series = _model.Series.Trim(), NextNumber = _model.NextNumber };
            var result = await Api.Projects.SetNextReleaseNumberAsync(ProjectId, request);
            if (result.Series is { } saved)
            {
                _closed = true;
                Toast.Success("SetNextVersion", "NextVersionSet", $"{saved.Series}.{saved.NextNumber}");
                Dialog.Close(saved);
            }
            else if (result.NumberAlreadyUsed)
                _refusal = RefusalText(request);
        }
        finally
        {
            _saving = false;
        }
    }

    private string RefusalText(SetNextReleaseNumberRequest request)
    {
        var taken = $"{request.Series}.{request.NextNumber.ToString(CultureInfo.InvariantCulture)}";
        var known = KnownSeries.FirstOrDefault(item => string.Equals(item.Series, request.Series, StringComparison.Ordinal));
        return known is not null && known.NextNumber != request.NextNumber
            ? string.Format(L["NextVersionAlreadyUsedSuggest"].Value, taken, $"{known.Series}.{known.NextNumber.ToString(CultureInfo.InvariantCulture)}")
            : string.Format(L["NextVersionAlreadyUsed"].Value, taken);
    }

    private sealed class NextVersionModel
    {
        [Required, StringLength(ReleaseSeries.MaxLength), RegularExpression(ReleaseSeries.Pattern)]
        public string Series { get; set; } = string.Empty;

        [Range(0, ReleaseSeries.MaxNumber)]
        public int NextNumber { get; set; }
    }
}
