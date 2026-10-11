// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-181, point 1 of the follow-up: the pipeline edit page has no Refresh entry. When the
/// definition changes elsewhere (another tab, another user, a model update) the entities hub says so;
/// the page reloads it at once when nothing is being edited, and otherwise shows a banner so an edit
/// in progress is never overwritten silently.
/// </summary>
internal sealed class PipelineDefinitionLiveSync(HubConnectionFactory hubFactory, int pipelineId, Func<Task> onChanged)
    : IAsyncDisposable
{
    private HubConnection? _hub;

    public async Task StartAsync()
    {
        try
        {
            _hub = hubFactory.Create("entities");
            _hub.On<ResourceType, int, string>("EntityChanged", (type, id, operation) =>
                type == ResourceType.Pipeline && id == pipelineId && operation == "Updated"
                    ? onChanged()
                    : Task.CompletedTask);
            _hub.RejoinOnReconnect(async () =>
            {
                await _hub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
                await onChanged();
            });
            await _hub.StartAsync();
            await _hub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            or Microsoft.AspNetCore.SignalR.HubException)
        {
            // Hub unavailable: the page keeps the definition it loaded.
        }
    }

    /// <summary>
    /// True when the form differs from the pipeline as last loaded or saved. FRONT-009: the source branch
    /// counts too; <paramref name="savedSourceBranch"/> is the branch the form showed once loaded or saved
    /// (the pipeline's own, or the fallback the page resolved when it has none).
    /// </summary>
    public static bool HasUnsavedChanges(PipelineModel model, PipelineDto? saved, string? savedSourceBranch) =>
        saved is not null
        && (model.Name != saved.Name
            || (model.SourceBranch ?? string.Empty) != (savedSourceBranch ?? string.Empty)
            || (model.Description ?? string.Empty) != (saved.Description ?? string.Empty)
            || model.YamlDefinition != saved.YamlDefinition
            || model.ProjectId != saved.ProjectId
            || model.EnvironmentId != saved.EnvironmentId
            || model.ProjectServerId != saved.ProjectServerId);

    /// <summary>
    /// SITE-FUNC-SELF-SAVE-001: the hub announces this client's own save too, often before the save's
    /// HTTP response has replaced the page's copy, so the form still looked unsaved and the page called
    /// its own change "changed elsewhere". The change is this client's when the stored pipeline is
    /// exactly what <paramref name="sent"/> (the last update this page submitted) asked for; any other
    /// stored content is a genuine remote change.
    /// </summary>
    public static bool IsOwnSave(UpdatePipelineRequest? sent, PipelineDto? stored) =>
        sent is not null && stored is not null
        && sent.Name == stored.Name
        && (sent.Description ?? string.Empty) == (stored.Description ?? string.Empty)
        && sent.YamlDefinition == stored.YamlDefinition
        && (sent.SourceBranch?.Trim() ?? string.Empty) == (stored.SourceBranch ?? string.Empty)
        && sent.ProjectId == stored.ProjectId
        && sent.EnvironmentId == stored.EnvironmentId
        && sent.ProjectServerId == stored.ProjectServerId;

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
