// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The top of the mail page when the stack is not compliant (R1-29): the failing status lines, read with
/// the same diagnostics as the State tab and kept current by the same server pushes, and a button to the
/// tab. Reading the diagnostics queues no agent task (inventory and DNS), so task completions can reload it.
/// </summary>
public sealed partial class MailComplianceBanner : IAsyncDisposable
{
    internal const string StateTabSlug = "status";

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ILogger<MailComplianceBanner> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>False while the State tab itself is shown: it lists the same lines, with their fixes.</summary>
    [Parameter] public bool Visible { get; set; } = true;

    private MailDiagnosticsDto? _diagnostics;
    private ServerLiveFeed? _liveFeed;

    /// <summary>The status lines that do not pass, in fix order.</summary>
    internal IReadOnlyList<MailDiagnosticItemDto> Failing =>
        [.. ServerMailStatusList.SelectStatusItems(_diagnostics).Where(item => item.Verdict != MailCheckVerdict.Ok)];

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    protected override void OnParametersSet()
    {
        _liveFeed ??= new ServerLiveFeed(HubFactory);
        if (_liveFeed.ServerId != ServerId)
            _ = _liveFeed.StartAsync(ServerId, ServerLiveFeedTriggers.HeartbeatAndTasks,
                () => InvokeAsync(ReloadFromPushAsync));
    }

    private async Task ReloadAsync()
    {
        try
        {
            _diagnostics = await Api.Mail.GetMailDiagnosticsAsync(ServerId);
        }
        catch (HttpRequestException ex)
        {
            // The last read stays on screen (or nothing before one); the next push reads again.
            Logger.LogWarning(ex, "Mail compliance read failed for server {ServerId}", ServerId);
        }
    }

    private async Task ReloadFromPushAsync()
    {
        await ReloadAsync();
        StateHasChanged();
    }

    /// <summary>Reads the status again now and waits for it (the section asks for it before its veil lifts).</summary>
    internal Task ReloadNowAsync() => InvokeAsync(ReloadFromPushAsync);

    private void ShowState() => Nav.NavigateTo(Nav.GetUriWithQueryParameter("tab", StateTabSlug));

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }
}
