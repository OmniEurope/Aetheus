// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>The service actions still running, linked to their tasks (recette R2-030, R3-22).</summary>
public partial class ServiceActionsInFlightBanner
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IEnumerable<ServiceInfoDto> Services { get; set; } = [];
    [Parameter] public Func<string, ServiceActionState?>? ActionStateOf { get; set; }

    private IReadOnlyList<(string Service, ServiceActionState Action)> InFlightActions =>
        [.. Services.Select(service => (Service: service.Name, Action: ActionStateOf?.Invoke(service.Name)))
            .Where(item => item.Action is { InFlight: true })
            .Select(item => (item.Service, item.Action!))];

    /// <summary>"Installation de « nginx » en cours…", or "en attente de l'agent" before it starts.</summary>
    internal string InFlightText(string service, ServiceActionState action) => string.Format(
        L[action.Phase == ServiceActionPhase.Queued ? "ServiceActionQueuedBanner" : "ServiceActionRunningBanner"],
        L[$"ServiceActionNoun{action.ActionKey}"], service);
}
