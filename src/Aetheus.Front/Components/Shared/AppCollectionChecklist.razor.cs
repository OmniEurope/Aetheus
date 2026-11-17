// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// R-26: what the setup prepared for one application and what is still missing to receive each signal, read from
/// the saved settings (the application, its web-analytics configuration and its last ingestions). Every line is one
/// real field; a line whose source could not be read is said unreadable, never guessed. The card shows only while
/// something is missing, so a collection that works leaves the page as it was.
/// </summary>
public partial class AppCollectionChecklist
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public MonitoredAppDto App { get; set; } = default!;

    /// <summary>Raised by the host when the settings changed (a key, the audience configuration): the lines are read again.</summary>
    [Parameter] public int Version { get; set; }

    internal enum LineStatus { Ready, Missing, Info }

    internal sealed record Line(LineStatus Status, string Text);

    internal sealed record Group(string Title, IReadOnlyList<Line> Lines);

    private AppWebAnalyticsConfigurationDto? _configuration;
    private AppWebAnalyticsSummaryDto? _summary;
    private bool _audienceUnreadable;
    private bool _loaded;
    private int _loadedAppId = -1;
    private int _loadedVersion = -1;
    private int _generation;

    protected override async Task OnParametersSetAsync()
    {
        if (App.Id == _loadedAppId && Version == _loadedVersion) return;
        _loadedAppId = App.Id;
        _loadedVersion = Version;
        var generation = ++_generation;
        var appId = App.Id;
        var configuration = ReadAsync(() => Api.Monitoring.GetAppWebAnalyticsConfigurationAsync(appId));
        var summary = ReadAsync(() => Api.Monitoring.GetAppWebAnalyticsAsync(appId));
        await Task.WhenAll(configuration, summary);
        if (generation != _generation) return;
        _configuration = configuration.Result.Value;
        _summary = summary.Result.Value;
        _audienceUnreadable = configuration.Result.Failed || summary.Result.Failed;
        _loaded = true;
    }

    // A read that fails is reported, not turned into "nothing configured": an unreadable line must not look like a missing one.
    private static async Task<(T? Value, bool Failed)> ReadAsync<T>(Func<Task<T?>> read) where T : class
    {
        try
        {
            return (await read(), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, true);
        }
    }

    private bool HasMissing => Groups.Any(group => group.Lines.Any(line => line.Status == LineStatus.Missing));

    private IReadOnlyList<Group> Groups => _loaded ? BuildGroups() : [];

    internal IReadOnlyList<Group> BuildGroups() => [Technical(), Audience(), Availability()];

    private Group Technical() => new(L["CollectionGroupTechnical"], [
        App.Enabled
            ? new Line(LineStatus.Ready, L["CollectionAppEnabled"])
            : new Line(LineStatus.Missing, L["CollectionAppDisabled"]),
        App.HasIngestKey
            ? new Line(LineStatus.Ready, string.Format(L["CollectionIngestKeyReady"], Date(App.IngestKeyCreatedAt)))
            // R-27: a key that exists but no longer counts (HasIngestKey is false) has expired; it is not a missing key.
            : App.IngestKeyCreatedAt is { } created
                ? new Line(LineStatus.Missing, string.Format(L["CollectionIngestKeyExpired"], Date(created)))
                : new Line(LineStatus.Missing, L["CollectionIngestKeyMissing"]),
        App.LastIngestAt is { } technical
            ? new Line(LineStatus.Ready, string.Format(L["CollectionTechnicalReceived"], Date(technical)))
            : new Line(LineStatus.Missing, L["CollectionTechnicalNone"])
    ]);

    private Group Audience()
    {
        if (_audienceUnreadable)
            return new Group(L["CollectionGroupAudience"], [new Line(LineStatus.Missing, L["CollectionAudienceUnreadable"])]);

        var configuration = _configuration;
        var origins = configuration?.AllowedOrigins.Count ?? 0;
        var version = configuration?.PseudonymKeyVersion ?? 0;
        return new Group(L["CollectionGroupAudience"], [
            configuration?.Enabled == true
                ? new Line(LineStatus.Ready, L["CollectionAudienceEnabled"])
                : new Line(LineStatus.Missing, L["CollectionAudienceDisabled"]),
            string.IsNullOrWhiteSpace(configuration?.SiteId)
                ? new Line(LineStatus.Missing, L["CollectionSiteIdMissing"])
                : new Line(LineStatus.Ready, string.Format(L["CollectionSiteIdReady"], configuration!.SiteId)),
            origins == 0
                ? new Line(LineStatus.Missing, L["CollectionOriginsMissing"])
                : new Line(LineStatus.Ready, string.Format(L["CollectionOriginsReady"], origins)),
            configuration?.PublicIngestEnabled == true
                ? new Line(LineStatus.Ready, L["CollectionPublicIngestReady"])
                : new Line(LineStatus.Missing, L["CollectionPublicIngestMissing"]),
            version > 0
                ? new Line(LineStatus.Ready, string.Format(L["CollectionPseudonymKeyReady"], version, Date(configuration!.PseudonymKeyCreatedAt)))
                : new Line(LineStatus.Missing, L["CollectionPseudonymKeyMissing"]),
            _summary?.LastIngestAtUtc is { } audience
                ? new Line(LineStatus.Ready, string.Format(L["CollectionAudienceReceived"], Date(audience)))
                : new Line(LineStatus.Missing, L["CollectionAudienceNone"])
        ]);
    }

    // The probe tests availability only: a missing URL is told, but it is not a prerequisite of any telemetry signal.
    private Group Availability() => new(L["CollectionGroupAvailability"], [
        string.IsNullOrWhiteSpace(App.ProbeUrl)
            ? new Line(LineStatus.Info, L["CollectionProbeNone"])
            : new Line(LineStatus.Ready, string.Format(L["CollectionProbeReady"], App.ProbeUrl))
    ]);

    private static string Date(DateTime? value) => value?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "-";

    private static OmniTone Tone(LineStatus status) => status switch
    {
        LineStatus.Ready => OmniTone.Success,
        LineStatus.Missing => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private string StatusText(LineStatus status) => status switch
    {
        LineStatus.Ready => L["CollectionReady"],
        LineStatus.Missing => L["CollectionMissing"],
        _ => L["CollectionInfo"]
    };
}
