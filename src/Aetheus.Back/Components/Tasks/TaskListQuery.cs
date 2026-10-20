// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

/// <summary> The column filters of the task lists (the global /tasks page, a server's tasks and a
/// project's tasks, all reading <see cref="ServerTaskDto"/>). The keys are the grid's column keys.
/// </summary>
internal static class TaskListQuery
{
    internal static readonly GridQueryMap<ServerTask> Columns = new GridQueryMap<ServerTask>()
        .Number("id", t => t.Id)
        .Text("name", t => t.Name)
        .Text("serverName", t => t.Server.Name)
        .Enum("executor", t => t.Executor)
        .Enum("status", t => t.Status)
        .Date("createdAt", t => t.CreatedAt)
        .Number("exitCode", t => t.ExitCode);

    /// <summary>The distinct server names of the tasks in a scope, for the Server column's checkable list.</summary>
    internal static async Task<TaskFilterValuesDto> FilterValuesAsync(IQueryable<ServerTask> scope, CancellationToken ct)
    {
        var names = await scope
            .Select(t => t.Server.Name)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new TaskFilterValuesDto
        {
            ServerNames = [.. names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)]
        };
    }
}
