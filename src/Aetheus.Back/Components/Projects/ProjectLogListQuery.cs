// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Projects;

/// <summary>The column filters of a project's logs section. The keys are the grid's column
/// keys.</summary>
internal static class ProjectLogListQuery
{
    internal static readonly GridQueryMap<TaskLog> Columns = new GridQueryMap<TaskLog>()
        .Date("timestamp", l => l.Timestamp)
        .Enum("level", l => l.Level)
        .Text("message", l => l.Message);
}
