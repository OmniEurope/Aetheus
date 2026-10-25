// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public partial class PipelineRunTableLiveDuration : LiveDurationComponentBase
{
    [Inject] private TimeProvider TimeProvider { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunTableItem Run { get; set; } = default!;

    private string Duration => PipelineRunTablePresentation.FormatDuration(
        Run,
        TimeProvider.GetLocalNow().LocalDateTime);
    protected override bool IsLive =>
        Run is { Status: PipelineStatus.Running, CompletedAt: null } && Run.StartedAt.Year >= 2000;

    protected override IDisposable CreateTimer(TimerCallback callback) => TimeProvider.CreateTimer(
        callback,
        null,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1));
}
