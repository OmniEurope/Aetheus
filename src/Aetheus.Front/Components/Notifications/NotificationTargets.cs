// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Notifications;

/// <summary>
/// Recette R3-16: the page a notification is about, read from its payload, so its detail can offer to
/// open it. Only events whose payload names the thing get a target; the others have none.
/// </summary>
public static class NotificationTargets
{
    public static string? Path(string eventType, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (eventType == NotificationEventTypes.AiTaskCompleted)
                return Id(root, "ProjectId") is { } project && Id(root, "AiTaskDefinitionId") is { } task
                    ? $"/projects/{project}/ai-tasks/{task}" + (Id(root, "AiRunResultId") is { } run ? $"?result={run}" : string.Empty)
                    : null;
            if (eventType == NotificationEventTypes.ReleaseDeployed && Id(root, "ReleaseId") is { } release)
                return $"/releases/{release}";
            return eventType.StartsWith("pipeline.", StringComparison.Ordinal) && Id(root, "PipelineRunId") is { } pipelineRun
                ? $"/pipelines/runs/{pipelineRun}"
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Id(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var id) && id > 0 ? id : null;
}
