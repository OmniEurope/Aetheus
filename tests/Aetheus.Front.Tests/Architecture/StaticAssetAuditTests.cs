// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Architecture;

public class StaticAssetAuditTests
{
    [Fact]
    public void UnusedBootstrapDistribution_IsNotPublished()
    {
        var root = FindRepoRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "lib", "bootstrap")));
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
