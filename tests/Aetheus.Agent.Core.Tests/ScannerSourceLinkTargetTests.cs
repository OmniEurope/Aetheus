// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Which symbolic link of a scanner source the staging copies: only a relative target that stays inside the
/// source tree, kept as written so the copy is never followed. Pure path checks: no link is created, so the
/// rules are verified on every platform (the link creation itself is covered on Linux by
/// <c>StageSourceTree_PreservesInternalSymlinkWithoutFollowingIt</c>).
/// </summary>
public sealed class ScannerSourceLinkTargetTests
{
    private static readonly string SourceRoot = Path.Combine(Path.GetTempPath(), "aetheus-stage-source");

    private static string InSource(params string[] parts) => Path.Combine([SourceRoot, .. parts]);

    [Theory]
    [InlineData("target.txt")]
    [InlineData("../shared/target.txt")]
    [InlineData("./nested/../target.txt")]
    public void ARelativeTargetInsideTheSource_IsKeptAsWritten(string target)
    {
        var link = InSource("module", "link.txt");

        Assert.Equal(target, ScannerSourceStager.SafeLinkTarget(link, target, SourceRoot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ALinkWithoutTarget_IsRefused(string? target)
    {
        var link = InSource("link.txt");

        var refusal = Assert.Throws<IOException>(() => ScannerSourceStager.SafeLinkTarget(link, target, SourceRoot));

        Assert.Contains("unsafe symbolic link", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(link, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsoluteTarget_IsRefused_EvenInsideTheSource()
    {
        var link = InSource("link.txt");

        var refusal = Assert.Throws<IOException>(() =>
            ScannerSourceStager.SafeLinkTarget(link, InSource("target.txt"), SourceRoot));

        Assert.Contains("unsafe symbolic link", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    public void ATargetThatLeavesTheSource_IsRefused(string target)
    {
        var link = InSource("link.txt");

        var refusal = Assert.Throws<IOException>(() => ScannerSourceStager.SafeLinkTarget(link, target, SourceRoot));

        Assert.Contains("escapes the workspace", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetNamingASiblingOfTheSourceWithTheSamePrefix_IsRefused()
    {
        var link = InSource("link.txt");

        var refusal = Assert.Throws<IOException>(() =>
            ScannerSourceStager.SafeLinkTarget(link, "../aetheus-stage-source-other/secret.txt", SourceRoot));

        Assert.Contains("escapes the workspace", refusal.Message, StringComparison.Ordinal);
    }
}
