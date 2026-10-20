// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests;

/// <summary>
/// S-FEAT-W8KN: the shared service vocabulary, the backend service map, the resolved apt package set
/// and the agent's argv-exact sudoers drop-in must stay in lockstep. A drift here means an installable
/// service the agent cannot sudo, or vice-versa. (The sudoers ↔ allow-list parity is covered by
/// <see cref="SudoersAuditTests"/>.)
/// </summary>
public class ManageablePackagesParityTests
{
    [Fact]
    public void SharedServiceKeys_MatchBackendWellKnownServices()
    {
        var shared = ManageablePackages.Services;
        var backend = ServerDataMapper.WellKnownManageableServices.Keys;

        Assert.Equal(
            shared.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            backend.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryServiceKey_ResolvesToAnAptPackageInAll()
    {
        foreach (var service in ManageablePackages.Services)
        {
            var apt = ManageablePackages.AptPackageFor(service);
            Assert.False(string.IsNullOrWhiteSpace(apt), $"service '{service}' has no apt mapping");
            Assert.Contains(apt!, ManageablePackages.All);
        }
    }

    [Fact]
    public void All_EqualsTheSetOfResolvedAptPackages()
    {
        var resolved = ManageablePackages.Services
            .Select(ManageablePackages.AptPackageFor)
            .Where(p => p is not null)!;

        Assert.Equal(
            ManageablePackages.All.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            resolved!.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Removable_IsAStrictSubsetOfAll_ExcludingProtectedPackages()
    {
        Assert.ProperSubset(ManageablePackages.All.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ManageablePackages.Removable.ToHashSet(StringComparer.OrdinalIgnoreCase));

        // The data stores, security-posture and pipeline-runner packages must NOT be removable.
        foreach (var protectedPkg in new[] { "docker.io", "postgresql", "mysql-server", "mariadb-server", "redis-server", "mongodb-org", "ufw", "fail2ban" })
            Assert.DoesNotContain(protectedPkg, ManageablePackages.Removable);
    }

    [Theory]
    [InlineData("docker", "docker.io")]
    [InlineData("mysql", "mysql-server")]
    [InlineData("mariadb", "mariadb-server")]
    [InlineData("mongod", "mongodb-org")]
    [InlineData("nginx", "nginx")]
    public void AptPackageFor_MapsServiceKeyToRealAptName(string service, string expected)
        => Assert.Equal(expected, ManageablePackages.AptPackageFor(service));
}
