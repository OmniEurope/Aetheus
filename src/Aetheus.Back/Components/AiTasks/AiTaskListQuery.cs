// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary> The column header filters of the AI runner profiles and AI tasks lists. Each key is
/// the grid column's key; a column absent from these maps is not filterable in the grid. </summary>
internal static class AiTaskListQuery
{
    internal static readonly GridQueryMap<AiRunnerProfile> ProfileColumns = new GridQueryMap<AiRunnerProfile>()
        .Text("name", profile => profile.Name)
        .Number("priority", profile => profile.Priority)
        .Text("model", profile => profile.Model)
        .Number("timeoutSeconds", profile => profile.TimeoutSeconds)
        .Boolean("sendsDataExternally", profile => profile.SendsDataExternally);

    internal static readonly GridQueryMap<AiTaskDefinition> DefinitionColumns = new GridQueryMap<AiTaskDefinition>()
        .Text("name", definition => definition.Name)
        .Text("profileName", definition => definition.Profile.Name)
        .Text("schedule", definition => definition.Schedule)
        .Boolean("enabled", definition => definition.Enabled);
}
