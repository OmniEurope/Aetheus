// SPDX-License-Identifier: EUPL-1.2
//
// Path filters shared by the Agent.Core repository scans. They judge the path RELATIVE to the repository
// root: the checkout running the scan may itself live under .worktrees, and testing the absolute path
// would then exclude every file and turn the guard into a vacuous green.

using System;
using System.IO;

namespace Aetheus.Agent.Core.Tests;

internal static class RepositoryPathFilter
{
    /// <summary>True when the file sits in a nested checkout or tool folder below <paramref name="root"/>
    /// (.claude, .worktrees), never judged on the part of the path above the root.</summary>
    internal static bool IsNestedCheckout(string root, string file)
    {
        var separator = Path.DirectorySeparatorChar;
        var relative = separator + Path.GetRelativePath(root, file);
        return relative.Contains($"{separator}.claude{separator}", StringComparison.OrdinalIgnoreCase)
            || relative.Contains($"{separator}.worktrees{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
