// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Inventory;

namespace Aetheus.Back.Tests.Inventory;

public sealed class InventoryBuilderTests
{
    private static readonly HashSet<string> NoDirectNames = [];

    private static InventoryEntry Entry(string ecosystem, string name, string version = "1.0.0", bool direct = false) =>
        new(ecosystem, name, version, direct, SourcePath: "a/file");

    [Fact]
    public void OneRowPerPackageVersionAndKind_SpelledLikeTheAdvisories()
    {
        var result = InventoryBuilder.Build(
            [
                Entry("NuGet", "Newtonsoft.Json"),
                Entry("NuGet", "newtonsoft.json", direct: true),
                Entry("NuGet", "Newtonsoft.Json", "2.0.0"),
                Entry("PyPI", "Zope.Interface")
            ],
            [], NoDirectNames, 100);

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
        Assert.Null(result.Reason);
        Assert.Equal(3, result.Items.Count);
        var first = result.Items[0];
        Assert.Equal("newtonsoft.json", first.PackageName);
        Assert.True(first.IsDirect);
        Assert.Equal("pkg:nuget/newtonsoft.json@1.0.0", first.PackageUrl);
        Assert.Equal("zope-interface", result.Items[2].PackageName);
    }

    [Fact]
    public void ANameThePackageAsksForItself_MakesTheLockedEntryDirect()
    {
        var result = InventoryBuilder.Build(
            [Entry("npm", "lodash")], [], new HashSet<string>(["lodash"], StringComparer.OrdinalIgnoreCase), 100);

        Assert.True(Assert.Single(result.Items).IsDirect);
    }

    [Fact]
    public void AGap_MakesTheInventoryPartial_AndNothingKnownIsNone()
    {
        InventoryGap[] gaps =
        [
            new(InventoryGapCodes.NoLockFile, "dotnet"), new(InventoryGapCodes.NoLockFile, "dotnet"), new(InventoryGapCodes.NotRead, "ruby")
        ];

        var partial = InventoryBuilder.Build([Entry("npm", "a")], gaps, NoDirectNames, 100);
        var none = InventoryBuilder.Build([], gaps, NoDirectNames, 100);

        Assert.Equal(ProjectInventoryCoverage.Partial, partial.Coverage);
        Assert.Equal("no-lock-file:dotnet=2, technology-not-read:ruby=1", partial.Reason);
        Assert.Equal(ProjectInventoryCoverage.None, none.Coverage);
        Assert.Equal(partial.Reason, none.Reason);
    }

    [Fact]
    public void AnInventoryOverItsLimit_IsCut_AndSaysSo()
    {
        var result = InventoryBuilder.Build(
            Enumerable.Range(0, 5).Select(index => Entry("npm", $"p{index}")), [], NoDirectNames, 3);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ProjectInventoryCoverage.Partial, result.Coverage);
        Assert.Equal("item-limit=1", result.Reason);
    }

    [Fact]
    public void AnEntryOverAColumnLength_IsCounted_NotTruncated()
    {
        var result = InventoryBuilder.Build(
            [Entry("npm", "ok"), Entry("npm", "long", new string('1', 300))], [], NoDirectNames, 100);

        Assert.Single(result.Items);
        Assert.Equal("oversized-entry=1", result.Reason);
    }

    [Fact]
    public void AVeryLongReason_StaysInItsColumn_OnAWholeEntry()
    {
        var gaps = Enumerable.Range(0, 100).Select(index => new InventoryGap(InventoryGapCodes.Unreadable, $"technology-with-a-long-name-{index}"));

        var reason = InventoryBuilder.Empty([.. gaps]).Reason!;

        Assert.True(reason.Length <= 1000);
        Assert.EndsWith("=1", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NuGet", "newtonsoft.json", "13.0.3", "pkg:nuget/newtonsoft.json@13.0.3")]
    [InlineData("npm", "@babel/core", "7.24.0", "pkg:npm/%40babel/core@7.24.0")]
    [InlineData("Packagist", "symfony/console", "v7.1.0", "pkg:composer/symfony/console@v7.1.0")]
    [InlineData("PyPI", "django", "5.0.1", "pkg:pypi/django@5.0.1")]
    [InlineData("Maven", "org.springframework:spring-core", "6.1.4", "pkg:maven/org.springframework/spring-core@6.1.4")]
    [InlineData("Go", "github.com/gin-gonic/gin", "v1.9.1", "pkg:golang/github.com/gin-gonic/gin@v1.9.1")]
    [InlineData("crates.io", "serde", "1.0.197", "pkg:cargo/serde@1.0.197")]
    [InlineData("Debian:12", "glibc", "2.36-9+deb12u7", "pkg:deb/debian/glibc@2.36-9%2Bdeb12u7")]
    [InlineData("Alpine:v3.19", "musl", "1.2.4-r2", "pkg:apk/alpine/musl@1.2.4-r2")]
    [InlineData("docker", "mcr.microsoft.com/dotnet/aspnet", "10.0.13", "pkg:docker/mcr.microsoft.com/dotnet/aspnet@10.0.13")]
    [InlineData("runtime", "nodejs", ">=20", "pkg:generic/nodejs@%3E%3D20")]
    public void PackageUrls_FollowEachEcosystem(string ecosystem, string name, string version, string expected) =>
        Assert.Equal(expected, InventoryPurl.Build(ecosystem, name, version));

    [Fact]
    public void APackageUrl_ReadsBackIntoItsParts()
    {
        var purl = InventoryPurl.TryParse("pkg:deb/debian/libc6@2.36-9%2Bdeb12u7?arch=amd64&upstream=glibc&distro=debian-12")!;

        Assert.Equal("deb", purl.Type);
        Assert.Equal("debian", purl.Namespace);
        Assert.Equal("libc6", purl.Name);
        Assert.Equal("2.36-9+deb12u7", purl.Version);
        Assert.Equal("glibc", purl.Qualifiers["upstream"]);
        Assert.Null(InventoryPurl.TryParse("not a purl"));
        Assert.Null(InventoryPurl.TryParse(null));
    }

    private static SbomSnapshot Snapshot(bool truncated = false, params (string Name, string? Purl)[] components) =>
        new([1], "abc", "main", truncated, [.. components.Select(item => new SbomComponent(item.Name, "9.9.9", item.Purl, false))]);

    [Theory]
    [InlineData("pkg:nuget/Serilog@4.0.0", "NuGet", "Serilog", "4.0.0", ProjectInventoryItemKind.Dependency)]
    [InlineData("pkg:nuget/Microsoft.AspNetCore.App.Runtime.linux-x64@10.0.13", "NuGet", "Microsoft.AspNetCore.App.Runtime.linux-x64", "10.0.13", ProjectInventoryItemKind.Runtime)]
    [InlineData("pkg:npm/%40babel/core@7.24.0", "npm", "@babel/core", "7.24.0", ProjectInventoryItemKind.Dependency)]
    [InlineData("pkg:composer/symfony/console@7.1.0", "Packagist", "symfony/console", "7.1.0", ProjectInventoryItemKind.Dependency)]
    [InlineData("pkg:maven/org.apache/commons@1.0", "Maven", "org.apache:commons", "1.0", ProjectInventoryItemKind.Dependency)]
    [InlineData("pkg:golang/github.com/gin-gonic/gin@v1.9.1", "Go", "github.com/gin-gonic/gin", "v1.9.1", ProjectInventoryItemKind.Dependency)]
    // Debian publishes on the source package: libc6 is matched through glibc.
    [InlineData("pkg:deb/debian/libc6@2.36-9?upstream=glibc&distro=debian-12", "Debian:12", "glibc", "2.36-9", ProjectInventoryItemKind.SystemPackage)]
    [InlineData("pkg:deb/debian/libgcc-s1@12.2.0-14?upstream=gcc-12%4012.2.0-14&distro=debian-12", "Debian:12", "gcc-12", "12.2.0-14", ProjectInventoryItemKind.SystemPackage)]
    [InlineData("pkg:apk/alpine/musl-utils@1.2.4-r2?upstream=musl&distro=alpine-3.19.1", "Alpine:v3.19", "musl", "1.2.4-r2", ProjectInventoryItemKind.SystemPackage)]
    [InlineData("pkg:deb/ubuntu/bash@5.1?distro=ubuntu-22.04", "Ubuntu:22.04", "bash", "5.1", ProjectInventoryItemKind.SystemPackage)]
    public void AnSbomComponent_BecomesAnEntryOfItsEcosystem(string purl, string ecosystem, string name, string version, ProjectInventoryItemKind kind)
    {
        var (entries, gaps) = SbomInventoryMapper.Map(Snapshot(false, ("x", purl)), 100);

        Assert.Empty(gaps);
        var entry = Assert.Single(entries);
        Assert.Equal(ecosystem, entry.Ecosystem);
        Assert.Equal(name, entry.Name);
        Assert.Equal(version, entry.Version);
        Assert.Equal(kind, entry.Kind);
    }

    [Fact]
    public void AnSbomComponentThatMapsToNothing_IsCounted()
    {
        var (entries, gaps) = SbomInventoryMapper.Map(
            Snapshot(true, ("no purl", null), ("binary", "pkg:generic/openssl@3.0"), ("ok", "pkg:pypi/django@5.0")), 100);

        Assert.Single(entries);
        Assert.Equal(
            [InventoryGapCodes.SbomTruncated, InventoryGapCodes.UnmappedComponent, InventoryGapCodes.UnmappedComponent],
            gaps.Select(gap => gap.Code));
    }
}
