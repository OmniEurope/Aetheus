// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Verify the enrollment identity source order with injected readers without accessing
/// this host machine identity or registry.</summary>
public sealed class MachineIdentityTests
{
    private const string LinuxMachineId = "0123456789abcdef0123456789abcdef";
    private const string WindowsMachineGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // HMAC-SHA256 under the fixed "Aetheus.MachineIdentity.v1" key. Pinned: a different value means
    // every enrolled host's identity changed, and no reinstalled machine would be recognised again.
    private const string LinuxMachineIdHash = "e3e3614ef735806e75a7a18840cf90c2fe75fc703c7792682935bbf27a0d668a";
    private const string WindowsMachineGuidHash = "781ac27a3d3ce31dc856d9ad3fed758a5a2ed57290a1da169f8da535907efd2f";
    private const string UnkeyedSha256OfLinuxMachineId = "3eb1bd439947eb762998e566ccc2e099c791118b2f40579cc4f7da2b5061b7f9";

    private static Func<string, string?> Files(params (string Path, string Content)[] files) =>
        path => files.FirstOrDefault(file => file.Path == path).Content;

    [Fact]
    public void Linux_HashesEtcMachineId_WithTheApplicationKey_NeverSendingTheRawValue()
    {
        var hash = MachineIdentity.ComputeHash(
            isWindows: false,
            Files((MachineIdentity.LinuxMachineIdPath, LinuxMachineId + "\n")),
            () => WindowsMachineGuid);

        Assert.Equal(LinuxMachineIdHash, hash);
        Assert.NotEqual(UnkeyedSha256OfLinuxMachineId, hash);
        Assert.DoesNotContain(LinuxMachineId, hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Linux_FallsBackToTheDbusMachineId_WhenEtcMachineIdIsMissingOrBlank(string? etcMachineId)
    {
        var hash = MachineIdentity.ComputeHash(
            isWindows: false,
            path => path == MachineIdentity.LinuxMachineIdPath ? etcMachineId
                : path == MachineIdentity.LinuxDbusMachineIdPath ? LinuxMachineId : null,
            () => null);

        Assert.Equal(LinuxMachineIdHash, hash);
    }

    [Fact]
    public void Windows_HashesTheRegistryMachineGuid_AndIgnoresLinuxFiles()
    {
        var hash = MachineIdentity.ComputeHash(
            isWindows: true,
            Files((MachineIdentity.LinuxMachineIdPath, LinuxMachineId)),
            () => WindowsMachineGuid.ToUpperInvariant());

        Assert.Equal(WindowsMachineGuidHash, hash);
    }

    [Fact]
    public void NoIdentityOnTheHost_ReportsNone_SoOnlyTheHostnameRuleApplies()
    {
        Assert.Null(MachineIdentity.ComputeHash(isWindows: false, Files(), () => WindowsMachineGuid));
        Assert.Null(MachineIdentity.ComputeHash(isWindows: true, Files((MachineIdentity.LinuxMachineIdPath, LinuxMachineId)), () => null));
    }

    // The injected-reader tests above pin the logic; this one proves the real registry read on the
    // platform where it is always present (a Linux container may legitimately have no machine-id).
    [PlatformFact("windows")]
    public void ReadHash_OnWindows_ReadsTheRealMachineGuid()
    {
        Assert.Matches("^[0-9a-f]{64}$", MachineIdentity.ReadHash());
    }

    [Fact]
    public void TheHashMatchesTheBackendContract()
    {
        var hash = MachineIdentity.ComputeHash(isWindows: false, Files((MachineIdentity.LinuxMachineIdPath, LinuxMachineId)), () => null);

        // ServerRegistrationRequest.MachineIdHash accepts exactly 64 lowercase hex characters.
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
