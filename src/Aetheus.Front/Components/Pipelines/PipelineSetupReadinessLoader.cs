// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The readiness verdict of the setup wizard's summary, loaded when the summary is reached, not earlier: the answer depends
/// on the capabilities chosen on the previous step, so checking before they are settled would show findings for templates
/// the user then deselects. The selection is fingerprinted, so stepping back and forth without changing anything does not
/// re-issue a check that reads the repository tree.
/// </summary>
internal sealed class PipelineSetupReadinessLoader(ApiClient api, Action changed)
{
    private string? _signature;

    /// <summary>The verdict; null while it was not read, or when it could not be (the panel then says so).</summary>
    public PipelineSetupReadinessDto? Verdict { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>The next load reads the repository again, whatever the selection.</summary>
    public void Invalidate() => _signature = null;

    /// <param name="keepCurrent">A check run again after a creation keeps the verdict on screen under the panel's veil (R2-20).</param>
    public async Task LoadAsync(int projectId, IReadOnlyList<string> templateNames, bool keepCurrent = false)
    {
        var signature = $"{projectId}:{string.Join(",", templateNames)}";
        if (signature == _signature) return;

        _signature = signature;
        if (!keepCurrent) Verdict = null;
        Loading = true;
        changed();
        try
        {
            Verdict = await api.Pipelines.CheckSetupReadinessAsync(projectId, templateNames);
        }
        catch (HttpRequestException)
        {
            // The panel renders "could not be checked" for a null verdict, which is the honest
            // outcome: no claim is made about a project state that was never read.
            Verdict = null;
            _signature = null;
        }
        finally
        {
            Loading = false;
            changed();
        }
    }
}
