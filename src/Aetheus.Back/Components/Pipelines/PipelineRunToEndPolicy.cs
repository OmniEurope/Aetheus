// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Settings;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Whether this instance accepts manual runs in "run to end" mode. Read from the platform setting
/// <see cref="PipelineRunToEndSetting.Key"/>, off unless an administrator set it to <c>true</c>: on a
/// production instance the mode would let a deployment step run after failed tests.
/// </summary>
public interface IPipelineRunToEndPolicy
{
    Task<bool> IsAllowedAsync(CancellationToken ct = default);
}

public sealed class PipelineRunToEndPolicy(ISettingsService settings) : IPipelineRunToEndPolicy
{
    /// <summary>The refusal a launch receives when it asks for the mode while the setting is off.</summary>
    public const string RefusalMessage =
        "Run-to-end mode is disabled on this instance. An administrator must enable it in the platform settings first.";

    public async Task<bool> IsAllowedAsync(CancellationToken ct = default) =>
        PipelineRunToEndSetting.IsAllowed(
            await settings.GetSettingValueAsync(PipelineRunToEndSetting.Key, ct).ConfigureAwait(false));
}
