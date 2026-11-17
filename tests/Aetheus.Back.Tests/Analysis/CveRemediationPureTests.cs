// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyBump;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>PLAN-004, lot 4, points 10, 12 and 13: the pure parts of a remediation: the name of the branch, the line a run receives, the
/// version a lock file really holds, and the command handed over when the platform does not do the change itself.</summary>
public sealed class CveRemediationPureTests
{
    [Theory]
    [InlineData("CVE-2024-1234", "monolog/monolog", "cve/CVE-2024-1234-monolog-monolog")]
    [InlineData("GHSA-abcd-efgh-ijkl", "@scope/pkg", "cve/GHSA-abcd-efgh-ijkl-scope-pkg")]
    [InlineData("CVE-1", "mcr.microsoft.com/dotnet/aspnet", "cve/CVE-1-mcr-microsoft-com-dotnet-aspnet")]
    [InlineData("CVE-1", "a..b/../c", "cve/CVE-1-a-b-c")]
    [InlineData("CVE-1", "pkg.lock", "cve/CVE-1-pkg-lock")]
    public void ABranch_IsNamedFromTheAdvisoryAndThePackage_WithNothingARefCannotHold(string advisory, string package, string expected)
    {
        var branch = CveRemediationBranch.For(advisory, package);

        Assert.Equal(expected, branch);
        Assert.True(CveRemediationBranch.IsOurs(branch));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("cve/")]
    [InlineData("cve/../main")]
    [InlineData("cve/a/b")]
    [InlineData("xcve/a")]
    [InlineData(null)]
    public void OnlyANameThisFeatureProduces_IsOneItMayWrite(string? branch) => Assert.False(CveRemediationBranch.IsOurs(branch));

    [Fact]
    public void TheRunReceivesOneLine_InTheDirectoryOfEachLockFile()
    {
        var line = CveRemediationLockRun.CommandLine(
        [
            new DependencyBumpLockRefresh("composer.lock", string.Empty, "composer update a/b"),
            new DependencyBumpLockRefresh("web/app/composer.lock", "web/app", "composer update a/b")
        ]);

        Assert.Equal("( composer update a/b ) && ( cd web/app && composer update a/b )", line);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a;rm")]
    [InlineData("$(x)")]
    [InlineData("a`b`")]
    public void ADirectoryThatIsNotAPlainPath_NeverReachesAShell(string directory) =>
        Assert.Null(CveRemediationLockRun.CommandLine([new DependencyBumpLockRefresh("x/composer.lock", directory, "composer update a/b")]));

    [Theory]
    [InlineData("NuGet", "Serilog", "4.0.0", "4.0.1", "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Serilog\" Version=\"4.0.0\" />\n  </ItemGroup>\n</Project>\n")]
    [InlineData("npm", "lodash", "4.17.15", "4.17.21", "web/package.json", "{\n  \"dependencies\": {\n    \"lodash\": \"4.17.15\"\n  }\n}\n")]
    public async Task AByteOrderMark_ComesThroughThePlanAsTheRepositoryGaveIt(
        string ecosystem, string package, string current, string target, string path, string text)
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var source = Substitute.For<IProjectSourceReader>();
        source.ReadTreeAsync(1, "main", Arg.Any<CancellationToken>())
            .Returns(new ProjectSourceTree("abc", [new ProjectSourceFile(path, "blob")]));
        source.OpenFileAsync(1, "abc", path, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([.. bom, .. Encoding.UTF8.GetBytes(text)]));
        var git = new CveRemediationGit(
            source, Substitute.For<IGitLightService>(), Substitute.For<IGitLightCliService>(), Substitute.For<IGitLightRepository>(),
            Substitute.For<IGitSmartHttpService>(), new CveRemediationRepository(null!));

        var plan = await git.PlanAsync(1, "main", new DependencyBumpRequest(ecosystem, package, current, target, path), TestContext.Current.CancellationToken);

        Assert.Null(plan.Failure);
        var edit = Assert.Single(plan.Result!.Edits);
        // UTF-8 without a preamble is how the commit writes this text: the bytes below are the bytes of the file on the branch.
        Assert.Equal<byte>([.. bom, .. Encoding.UTF8.GetBytes(text.Replace(current, target, StringComparison.Ordinal))], new UTF8Encoding(false).GetBytes(edit.Content));
    }

    /// <summary>The path of the in-memory provider, which has no bulk update. The relational one is proved over PostgreSQL by the HTTP tests.</summary>
    [Fact]
    public async Task TheEndOfARun_IsTakenOnce_AndAFailureEndsOnlyAnActiveAttempt()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;
        var row = new VulnerabilityRemediation { Status = VulnerabilityRemediationStatus.Running };
        db.VulnerabilityRemediations.Add(row);
        await db.SaveChangesAsync(ct);
        var repository = new CveRemediationRepository(db);
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(await repository.TryClaimRunEndAsync(row, now));
        Assert.False(await repository.TryClaimRunEndAsync(row, now));
        Assert.Equal(VulnerabilityRemediationStatus.BranchPushed, row.Status);

        await repository.WriteFailureAsync(row, "why", now);
        Assert.Equal((VulnerabilityRemediationStatus.Failed, "why", now), (row.Status, row.FailureReason, row.EndedAt));

        await repository.WriteFailureAsync(row, "again", now.AddHours(1));
        Assert.Equal(("why", now), (row.FailureReason, row.EndedAt));
    }

    private const string Lock = """{"packages":[{"name":"acme/lib","version":"2.4.1"},{"name":"other/lib","version":"0.1.0"}],"packages-dev":[]}""";
    private const string NpmLock = """{"lockfileVersion":3,"packages":{"":{"name":"app"},"node_modules/lodash":{"version":"4.17.21"},"node_modules/a/node_modules/lodash":{"version":"4.17.15"}}}""";

    [Fact]
    public void AVersionAboveTheTarget_IsWhatTheLockHolds_NotTheTarget()
    {
        var resolution = CveRemediationResolved.Read("Packagist", "acme/lib", "2.4.0", [("composer.lock", Lock)]);

        Assert.Equal(new CveRemediationResolution("2.4.1", null), resolution);
    }

    [Fact]
    public void ALockThatStartsWithAByteOrderMark_ReadsAsTheSameLock()
    {
        var resolution = CveRemediationResolved.Read("Packagist", "acme/lib", "2.4.0", [("composer.lock", "﻿" + Lock)]);

        Assert.Equal(new CveRemediationResolution("2.4.1", null), resolution);
    }

    [Fact]
    public void ANestedCopyBelowTheTarget_StillFails_TheAdvisoryStillApplies()
    {
        var resolution = CveRemediationResolved.Read("npm", "lodash", "4.17.21", [("package-lock.json", NpmLock)]);

        Assert.Equal("4.17.15", resolution.Version);
        Assert.Contains("below", resolution.Failure!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("composer.lock", "{not json", "does not read")]
    [InlineData("composer.lock", """{"packages":[]}""", "does not hold")]
    [InlineData("yarn.lock", "anything", "does not read")]
    public void ALockThatDoesNotAnswer_IsAFailure_NeverAnAssumedVersion(string path, string content, string reason)
    {
        var resolution = CveRemediationResolved.Read("Packagist", "acme/lib", "2.4.0", [(path, content)]);

        Assert.Null(resolution.Version);
        Assert.Contains(reason, resolution.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuntime_IsStatedByTheManifest_TheLockNeverNamesIt() =>
        Assert.Equal(new CveRemediationResolution(null, null), CveRemediationResolved.Read("runtime", "php", "8.2.20", [("composer.lock", Lock)]));

    [Theory]
    [InlineData("NuGet", "Serilog", "4.0.1", "dotnet add package Serilog --version 4.0.1")]
    [InlineData("npm", "@scope/pkg", "1.2.3", "npm install @scope/pkg@1.2.3 --ignore-scripts")]
    [InlineData("Packagist", "monolog/monolog", "1.27.1", "composer require monolog/monolog:1.27.1 --no-scripts --no-plugins")]
    [InlineData("PyPI", "requests", "2.32.0", "pip install requests==2.32.0")]
    [InlineData("Go", "golang.org/x/net", "0.17.0", "go get golang.org/x/net@v0.17.0")]
    [InlineData("crates.io", "chrono", "0.4.20", "cargo update -p chrono --precise 0.4.20")]
    [InlineData("RubyGems", "rack", "2.2.8", "bundle update rack --conservative")]
    public void TheFallback_IsTheCommandOfTheEcosystem(string ecosystem, string package, string target, string command)
    {
        var fallback = CveRemediationFallback.For(
            new CveRemediationCommandInput(ecosystem, package, target, "app/manifest"), CveRemediationFallbackReason.BumpRefused, "why");

        Assert.Equal(command, fallback.Command);
        Assert.Equal("app", fallback.WorkingDirectory);
        Assert.Equal(["app/manifest"], fallback.Files);
        Assert.Equal((CveRemediationFallbackReason.BumpRefused, "why"), (fallback.Reason, fallback.Detail));
    }

    [Theory]
    [InlineData("npm", "x; rm -rf /", "1.0.0")]
    [InlineData("npm", "$(evil)", "1.0.0")]
    [InlineData("npm", "lodash", "1.0.0; rm -rf /")]
    [InlineData("npm", "lodash", null)]
    [InlineData("Maven", "org.acme:lib", "1.0.0")]
    [InlineData("docker", "node", "20.11.1")]
    [InlineData("Debian:12", "openssl", "3.0.1")]
    public void NoCommandIsInvented_ForAnUncheckedTextOrAnEcosystemWithoutOne(string ecosystem, string package, string? target)
    {
        var fallback = CveRemediationFallback.For(new CveRemediationCommandInput(ecosystem, package, target, null), CveRemediationFallbackReason.NoFileToChange, "why");

        Assert.Null(fallback.Command);
        Assert.Equal("why", fallback.Detail);
        Assert.Empty(fallback.Files);
    }

    [Theory]
    [InlineData("Microsoft.NETCore.App.Runtime.linux-x64", null)]
    [InlineData("Microsoft.AspNetCore.App.Runtime.linux-x64", null)]
    [InlineData("Microsoft.NETCore.App.Runtime.linux-x64", ProjectInventoryItemKind.Runtime)]
    [InlineData("Serilog", ProjectInventoryItemKind.Runtime)]
    [InlineData("Serilog", ProjectInventoryItemKind.BaseImage)]
    public void ARuntimePack_OrARuntimeOrImageItem_NeverGetsADotnetAddPackageCommand(string package, ProjectInventoryItemKind? kind)
    {
        var fallback = CveRemediationFallback.For(
            new CveRemediationCommandInput("NuGet", package, "10.0.14", "sbom", kind), CveRemediationFallbackReason.NoFileToChange, string.Empty);

        Assert.Null(fallback.Command);
    }

    [Theory]
    [InlineData("sbom")]
    [InlineData("server:vps-1")]
    public void AnInventoryOrigin_IsNeitherAFileNorAWorkingDirectoryOfTheFallback(string origin)
    {
        var fallback = CveRemediationFallback.For(
            new CveRemediationCommandInput("NuGet", "Serilog", "4.0.1", origin), CveRemediationFallbackReason.NoFileToChange, string.Empty);

        Assert.Empty(fallback.Files);
        Assert.Null(fallback.WorkingDirectory);
    }

    [Fact]
    public void ADependencyWithARealFile_StillGetsItsCommand()
    {
        var fallback = CveRemediationFallback.For(
            new CveRemediationCommandInput("NuGet", "Serilog", "4.0.1", "src/App/App.csproj", ProjectInventoryItemKind.Dependency),
            CveRemediationFallbackReason.BumpRefused, "why");

        Assert.Equal("dotnet add package Serilog --version 4.0.1", fallback.Command);
        Assert.Equal(["src/App/App.csproj"], fallback.Files);
    }
}
