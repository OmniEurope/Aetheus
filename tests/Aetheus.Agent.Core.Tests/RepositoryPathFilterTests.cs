// SPDX-License-Identifier: EUPL-1.2

using System.IO;
using Xunit;

namespace Aetheus.Agent.Core.Tests;

public sealed class RepositoryPathFilterTests
{
    [Fact]
    public void IsNestedCheckout_KeepsFileOfRootLocatedUnderWorktreesFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Aetheus", ".worktrees", "rca");
        var file = Path.Combine(root, "src", "Aetheus.Agent.Core", "Thing.cs");

        Assert.False(RepositoryPathFilter.IsNestedCheckout(root, file));
    }

    [Fact]
    public void IsNestedCheckout_DropsNestedWorktreesAndClaudeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Aetheus", ".worktrees", "rca");

        Assert.True(RepositoryPathFilter.IsNestedCheckout(root, Path.Combine(root, ".worktrees", "x", "src", "Thing.cs")));
        Assert.True(RepositoryPathFilter.IsNestedCheckout(root, Path.Combine(root, ".claude", "worktrees", "y", "Thing.cs")));
    }
}
