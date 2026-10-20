// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// S-FEAT-VPNP: persists the manually-arranged node positions of a pipeline's Visual editor as a user
/// preference keyed by pipeline id (R1-66, formerly the browser's localStorage), so a hand-laid-out graph
/// survives a reload and follows the user to their other devices. A structure
/// <b>signature</b> (the ordered stage names) is stored alongside the positions and re-checked on load:
/// any structural edit - add, remove, <b>or reorder</b> a stage - changes the signature, so stale
/// index-keyed positions are never re-applied to the wrong stages (challenge follow-up). Extracted from
/// <see cref="VisualPipelineEditor"/> to keep that file under the 600-line budget.
/// </summary>
internal sealed class VisualPipelineLayoutStore(IUserPreferences preferences)
{
    /// <summary>Overlay saved positions onto <paramref name="positions"/> only when the saved signature matches.</summary>
    public async Task LoadIntoAsync(int pipelineId, string signature, Dictionary<string, NodePosition> positions)
    {
        var blob = await preferences.GetAsync<LayoutBlob>(PreferenceKeys.VisualLayout(pipelineId)).ConfigureAwait(false);
        if (blob is null || blob.Sig != signature || blob.Pos is null) return;
        foreach (var (nodeId, xy) in blob.Pos)
            if (xy.Length == 2 && positions.ContainsKey(nodeId))
                positions[nodeId] = new NodePosition(xy[0], xy[1]);
    }

    public Task SaveAsync(int pipelineId, string signature, Dictionary<string, NodePosition> positions)
    {
        var payload = new LayoutBlob
        {
            Sig = signature,
            Pos = positions.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y })
        };
        return preferences.SetAsync(PreferenceKeys.VisualLayout(pipelineId), payload);
    }

    internal sealed class LayoutBlob
    {
        public string Sig { get; set; } = string.Empty;
        public Dictionary<string, double[]>? Pos { get; set; }
    }
}
