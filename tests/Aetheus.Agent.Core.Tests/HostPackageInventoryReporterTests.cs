// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Aetheus.Shared.Components.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-41: the host package reporter, driven on any platform through its <c>IsLinux</c> and <c>ReadFile</c> seams.
/// The shell and the API client are doubles: these tests prove the reporter's decisions and cadence, not a real
/// dpkg-query run nor a real HTTP call.</summary>
public class HostPackageInventoryReporterTests
{
    private const string UbuntuRelease =
        "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nID=ubuntu\n";

    private const string TwoPackages =
        "ii \topenssl\t3.0.13-0ubuntu3.4\t\tamd64\nii \tlibssl3t64\t3.0.13-0ubuntu3.4\topenssl\tamd64\n";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Collect_NormalHost_BuildsThePayloadWithOsFieldsAndSourceNames()
    {
        using var h = new Harness();

        var report = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal("ubuntu", report.OsId);
        Assert.Equal("24.04", report.OsVersionId);
        Assert.Equal("24.04.1 LTS (Noble Numbat)", report.OsVersion);
        Assert.Equal("Ubuntu 24.04.1 LTS", report.OsPrettyName);
        Assert.Equal(["libssl3t64", "openssl"], report.Packages.Select(p => p.Name));
        var libssl = report.Packages[0];
        Assert.Equal("3.0.13-0ubuntu3.4", libssl.Version);
        Assert.Equal("openssl", libssl.SourceName);
        Assert.Null(report.Packages[1].SourceName);
    }

    [Fact]
    public async Task Collect_ReportsTheRunningKernel_ReadFromProc_AndNullWhenAbsent()
    {
        using var h = new Harness();
        h.Files["/proc/sys/kernel/osrelease"] = "6.8.0-147-generic\n";

        var report = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.Equal("6.8.0-147-generic", report?.RunningKernelRelease);

        h.Files.Remove("/proc/sys/kernel/osrelease");
        var without = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.NotNull(without);
        Assert.Null(without.RunningKernelRelease);
    }

    [Fact]
    public async Task Collect_AsksDpkgQueryWithTheSharedArgv()
    {
        using var h = new Harness();

        await h.Reporter.CollectAsync(CancellationToken.None);

        await h.Shell.Received(1).RunExecAsync(
            "dpkg-query",
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(DpkgHostPackages.QueryArgv)),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Collect_FallsBackToUsrLibOsRelease()
    {
        using var h = new Harness();
        h.Files.Remove("/etc/os-release");
        h.Files["/usr/lib/os-release"] = UbuntuRelease;

        var report = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.Equal("ubuntu", report?.OsId);
    }

    [Fact]
    public async Task Collect_LongOsFields_AreCutToTheBounds()
    {
        using var h = new Harness();
        h.Files["/etc/os-release"] =
            $"ID={new string('a', 200)}\nVERSION_ID={new string('1', 200)}\nVERSION={new string('v', 300)}\nPRETTY_NAME={new string('p', 400)}\n";

        var report = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(HostPackageLimits.MaxOsIdLength, report.OsId.Length);
        Assert.Equal(HostPackageLimits.MaxOsIdLength, report.OsVersionId!.Length);
        Assert.Equal(HostPackageLimits.MaxOsVersionLength, report.OsVersion!.Length);
        Assert.Equal(HostPackageLimits.MaxOsPrettyNameLength, report.OsPrettyName!.Length);
    }

    [Fact]
    public async Task Report_NormalHost_SendsOnceToTheEnrolledServer()
    {
        using var h = new Harness();

        var ok = await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.True(ok);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(7, sent.ServerId);
        Assert.Equal(2, sent.Report.Packages.Count);
    }

    [Fact]
    public async Task Report_NoServerId_SendsNothingAndAsksForARetry()
    {
        using var h = new Harness(serverId: null);

        var ok = await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\n")]
    [InlineData("ID=\n")]
    [InlineData("ID=\"  \"\n")]
    [InlineData("# ID=ubuntu\n")]
    public async Task Report_NoOsId_SendsNothingAndDoesNotRetrySoon(string? release)
    {
        using var h = new Harness();
        if (release is null) h.Files.Clear();
        else h.Files["/etc/os-release"] = release;

        var ok = await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.ShellCalls);
    }

    [Fact]
    public async Task Report_DpkgQueryCannotBeLaunched_OnADebianFamilyHost_SendsNothing()
    {
        using var h = new Harness { ShellFailure = new System.ComponentModel.Win32Exception("dpkg-query not found") };

        var ok = await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Report_NoDpkg_OnAnRpmHost_SendsTheReleaseAlone_FlaggedUnsupported()
    {
        using var h = new Harness { ShellFailure = new System.ComponentModel.Win32Exception("dpkg-query not found") };
        h.Files["/etc/os-release"] = "ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\nVERSION_ID=\"9.4\"\nPRETTY_NAME=\"Rocky Linux 9.4 (Blue Onyx)\"\n";

        var ok = await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.True(ok);
        var (serverId, report) = Assert.Single(h.Sent);
        Assert.Equal(7, serverId);
        Assert.Equal("rocky", report.OsId);
        Assert.Equal("9.4", report.OsVersionId);
        Assert.False(report.PackageManagerSupported);
        Assert.Empty(report.Packages);
        Assert.Empty(Validate(report));
    }

    [Fact]
    public async Task Collect_NormalHost_IsFlaggedSupported_WithTheArchitecture()
    {
        using var h = new Harness();

        var report = await h.Reporter.CollectAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.True(report.PackageManagerSupported);
        Assert.Equal(["amd64", "amd64"], report.Packages.Select(p => p.Architecture));
    }

    [Fact]
    public async Task Collect_Cancelled_PropagatesInsteadOfBeingReadAsMissingDpkg()
    {
        using var h = new Harness { ShellFailure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Reporter.CollectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Report_DpkgQueryFails_SendsNothing()
    {
        using var h = new Harness { Dpkg = new ShellExecResult(2, TwoPackages, "dpkg: error") };

        await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Report_TruncatedOutput_SendsNothing()
    {
        using var h = new Harness { Dpkg = new ShellExecResult(0, TwoPackages, string.Empty, Truncated: true) };

        await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rc \tgone\t1.0\t\nun \tnever\t1.0\t\n")]
    [InlineData("garbage without tabs\n")]
    public async Task Report_NoPackage_SendsNothing(string stdout)
    {
        using var h = new Harness { Dpkg = new ShellExecResult(0, stdout, string.Empty) };

        await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Report_MoreThanTheBound_SendsNothing()
    {
        using var h = new Harness { Dpkg = new ShellExecResult(0, Packages(HostPackageLimits.MaxPackages + 1), string.Empty) };

        await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Report_ExactlyTheBound_IsSent()
    {
        using var h = new Harness { Dpkg = new ShellExecResult(0, Packages(HostPackageLimits.MaxPackages), string.Empty) };

        await h.Reporter.ReportAsync(CancellationToken.None);

        Assert.Equal(HostPackageLimits.MaxPackages, Assert.Single(h.Sent).Report.Packages.Count);
    }

    [Fact]
    public async Task Run_NotLinux_NeverReadsNorSends()
    {
        using var h = new Harness { IsLinux = false };

        await h.Reporter.StartAsync(CancellationToken.None);
        await h.AdvanceAsync(TimeSpan.FromHours(30), 30 * Minute, () => false);
        await h.Reporter.StopAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
        Assert.Equal(0, h.ShellCalls);
    }

    [Fact]
    public async Task Run_FirstSendComesAfterTheStartDelay()
    {
        using var h = new Harness();
        await h.Reporter.StartAsync(CancellationToken.None);

        await h.AdvanceAsync(HostPackageInventoryReporter.StartDelay - TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), () => false);
        Assert.Empty(h.Sent);

        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Sent.Count == 1));
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_NotEnrolledYet_SendsNothingUntilEnrolled()
    {
        using var h = new Harness { Enrolled = false };
        await h.Reporter.StartAsync(CancellationToken.None);

        await h.AdvanceAsync(10 * Minute, TimeSpan.FromSeconds(1), () => false);
        Assert.Empty(h.Sent);

        h.Enrolled = true;
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(1), () => h.Sent.Count == 1));
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_AfterASend_ReportsAgainOnlyAfterADay()
    {
        using var h = new Harness();
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Sent.Count == 1));
        var firstSentAt = h.Time.GetUtcNow();

        await h.AdvanceAsync(HostPackageInventoryReporter.ReportInterval - Minute, Minute, () => h.Sent.Count > 1);
        Assert.Single(h.Sent);

        Assert.True(await h.AdvanceAsync(TimeSpan.FromHours(1), Minute, () => h.Sent.Count == 2));
        Assert.True(h.Time.GetUtcNow() - firstSentAt >= HostPackageInventoryReporter.ReportInterval - Minute);
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_FailedSend_IsRetriedAnHourLaterNotADayLater()
    {
        using var h = new Harness { FailSends = 1 };
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Attempts == 1));
        var failedAt = h.Time.GetUtcNow();
        Assert.Empty(h.Sent);

        await h.AdvanceAsync(HostPackageInventoryReporter.RetryInterval - 2 * Minute, Minute, () => h.Attempts > 1);
        Assert.Equal(1, h.Attempts);

        Assert.True(await h.AdvanceAsync(10 * Minute, Minute, () => h.Sent.Count == 1));
        Assert.True(h.Time.GetUtcNow() - failedAt <= HostPackageInventoryReporter.RetryInterval + 5 * Minute);

        // The retry succeeded, so the next attempt is a day away, not an hour.
        await h.AdvanceAsync(TimeSpan.FromHours(5), 10 * Minute, () => h.Attempts > 2);
        Assert.Equal(2, h.Attempts);
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_ThrowingSend_DoesNotStopTheLoop_AndIsRetriedAnHourLater()
    {
        using var h = new Harness { FailSends = 2 };
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Attempts == 1));

        Assert.True(await h.AdvanceAsync(TimeSpan.FromHours(1) + 5 * Minute, Minute, () => h.Attempts == 2));
        Assert.True(await h.AdvanceAsync(TimeSpan.FromHours(1) + 5 * Minute, Minute, () => h.Sent.Count == 1));
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_DpkgGivesNothingUsable_IsNotRetriedBeforeTomorrow()
    {
        using var h = new Harness { Dpkg = new ShellExecResult(1, string.Empty, "boom") };
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.ShellCalls == 1));

        await h.AdvanceAsync(TimeSpan.FromHours(20), 10 * Minute, () => h.ShellCalls > 1);

        Assert.Equal(1, h.ShellCalls);
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_AfterASystemUpgrade_ReportsAgainWithoutWaitingADay()
    {
        using var h = new Harness();
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Sent.Count == 1));
        h.Dpkg = new ShellExecResult(0, "ii \topenssl\t3.0.13-0ubuntu3.5\t\n", string.Empty);

        h.Health.RequestHostPackageReport();

        Assert.True(await Harness.WaitForAsync(() => h.Sent.Count == 2));
        Assert.Equal("3.0.13-0ubuntu3.5", Assert.Single(h.Sent[1].Report.Packages).Version);
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_UpgradeRequestAfterAFailedSend_ReportsAtOnce()
    {
        using var h = new Harness { FailSends = 1 };
        await h.Reporter.StartAsync(CancellationToken.None);
        Assert.True(await h.AdvanceAsync(5 * Minute, TimeSpan.FromSeconds(10), () => h.Attempts == 1));

        h.Health.RequestHostPackageReport();

        Assert.True(await Harness.WaitForAsync(() => h.Sent.Count == 1));
        await h.Reporter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_StopWhileWaiting_EndsCleanly()
    {
        using var h = new Harness();
        await h.Reporter.StartAsync(CancellationToken.None);
        await Harness.WaitForAsync(() => false, TimeSpan.FromMilliseconds(100));

        await h.Reporter.StopAsync(CancellationToken.None);

        Assert.Empty(h.Sent);
    }

    // os-release parsing

    [Fact]
    public void OsRelease_Parse_ReadsQuotedUnquotedAndSingleQuotedValues()
    {
        var values = OsReleaseFile.Parse("ID=debian\nNAME=\"Debian GNU/Linux\"\nVERSION_ID='12'\n");

        Assert.Equal("debian", values["ID"]);
        Assert.Equal("Debian GNU/Linux", values["NAME"]);
        Assert.Equal("12", values["VERSION_ID"]);
    }

    [Fact]
    public void OsRelease_Parse_SkipsCommentsBlankLinesAndNonAssignments()
    {
        var values = OsReleaseFile.Parse("# comment\n\n   \n  # indented comment\nnot an assignment\n=novalue\nlower=case\nBAD-KEY=x\nID=ubuntu\n");

        Assert.Equal(["ID"], values.Keys);
    }

    [Fact]
    public void OsRelease_Parse_HandlesCrlfAndSurroundingSpaces()
    {
        var values = OsReleaseFile.Parse("  ID=ubuntu  \r\nVERSION_ID=\"24.04\"\r\n");

        Assert.Equal("ubuntu", values["ID"]);
        Assert.Equal("24.04", values["VERSION_ID"]);
    }

    [Fact]
    public void OsRelease_Parse_UnescapesDoubleQuotedAndKeepsSingleQuotedLiteral()
    {
        var values = OsReleaseFile.Parse("A=\"say \\\"hi\\\" \\\\ \\$x \\`y\\` \\n\"\nB='a\\\"b'\n");

        Assert.Equal("say \"hi\" \\ $x `y` \\n", values["A"]);
        Assert.Equal("a\\\"b", values["B"]);
    }

    [Fact]
    public void OsRelease_Parse_SkipsAnUnterminatedQuote_AndKeepsEqualsInsideValues()
    {
        var values = OsReleaseFile.Parse("PRETTY_NAME=\"Broken\nHOME_URL=\"https://x.org/?a=b\"\nEMPTY=\nID=ubuntu\n");

        Assert.False(values.ContainsKey("PRETTY_NAME"));
        Assert.Equal("https://x.org/?a=b", values["HOME_URL"]);
        Assert.Equal(string.Empty, values["EMPTY"]);
    }

    [Fact]
    public void OsRelease_Parse_LastAssignmentWins()
    {
        Assert.Equal("b", OsReleaseFile.Parse("ID=a\nID=b\n")["ID"]);
    }

    [Fact]
    public void OsRelease_Identify_LowercasesIdAndDropsBlankOptionals()
    {
        var os = OsReleaseFile.Identify(OsReleaseFile.Parse("ID=Ubuntu\nVERSION_ID=\"\"\nVERSION=\"  \"\nPRETTY_NAME=\"Ubuntu\"\n"));

        Assert.NotNull(os);
        Assert.Equal("ubuntu", os.Value.Id);
        Assert.Null(os.Value.VersionId);
        Assert.Null(os.Value.Version);
        Assert.Equal("Ubuntu", os.Value.PrettyName);
    }

    // dpkg-query parsing

    [Fact]
    public void Dpkg_Parse_ReadsNameVersionAndSource()
    {
        var packages = DpkgHostPackages.Parse(
            "ii \tzlib1g\t1:1.3.dfsg-3.1ubuntu2\tzlib\nii \tlinux-image-generic\t6.8.0-45.45\tlinux-meta (6.8.0-45.45)\nii \tbash\t5.2\t\n");

        Assert.Equal(["bash", "linux-image-generic", "zlib1g"], packages.Select(p => p.Name));
        var kernel = packages.Single(p => p.Name == "linux-image-generic");
        Assert.Equal("linux-meta", kernel.SourceName);
        Assert.Equal("6.8.0-45.45", kernel.SourceVersion);
        var zlib = packages.Single(p => p.Name == "zlib1g");
        Assert.Equal("1:1.3.dfsg-3.1ubuntu2", zlib.Version);
        Assert.Equal("zlib", zlib.SourceName);
        Assert.Null(zlib.SourceVersion);
        Assert.Null(packages.Single(p => p.Name == "bash").SourceName);
    }

    [Fact]
    public void Dpkg_Parse_SkipsNotInstalledAndConfigOnlyPackages_KeepsHeldAndUnpacked()
    {
        var packages = DpkgHostPackages.Parse(
            "ii \ta\t1\t\nrc \tb\t1\t\nun \tc\t1\t\npn \td\t1\t\nhi \te\t1\t\niU \tf\t1\t\n");

        Assert.Equal(["a", "e", "f"], packages.Select(p => p.Name));
    }

    [Fact]
    public void Dpkg_Parse_SkipsMalformedLines_AndAcceptsCrlfAndMissingSourceColumn()
    {
        var packages = DpkgHostPackages.Parse("\nonly\ttwo\r\nii \t\t1\t\nii \tnover\t \t\nii \tok\t2\r\nii \tok2\t3\t\r\n");

        Assert.Equal(["ok", "ok2"], packages.Select(p => p.Name));
        Assert.Equal("2", packages[0].Version);
    }

    [Fact]
    public void Dpkg_Parse_DuplicateBinaryPackage_KeepsOneEntry()
    {
        var packages = DpkgHostPackages.Parse("ii \tlibc6\t2.39-0ubuntu8.2\tglibc\nii \tlibc6\t2.39-0ubuntu8.3\tglibc\n");

        Assert.Equal("2.39-0ubuntu8.3", Assert.Single(packages).Version);
    }

    [Fact]
    public void Dpkg_Parse_OversizedNameOrVersion_DropsThePackage_OversizedSourceOnlyTheSource()
    {
        var tooLong = new string('x', HostPackageLimits.MaxNameLength + 1);
        var packages = DpkgHostPackages.Parse(
            $"ii \t{tooLong}\t1\t\nii \tv\t{tooLong}\t\nii \tsrc\t1\t{tooLong}\nii \tsrcv\t1\tname ({tooLong})\n");

        Assert.Equal(["src", "srcv"], packages.Select(p => p.Name));
        Assert.All(packages, p => Assert.Null(p.SourceVersion));
        Assert.Null(packages.Single(p => p.Name == "src").SourceName);
        Assert.Equal("name", packages.Single(p => p.Name == "srcv").SourceName);
    }

    [Fact]
    public void Dpkg_Parse_SourceWithUnclosedParenthesis_KeepsTheNameOnly()
    {
        var package = Assert.Single(DpkgHostPackages.Parse("ii \tp\t1\tsrc (1.0\n"));

        Assert.Equal("src", package.SourceName);
        Assert.Null(package.SourceVersion);
    }

    [Fact]
    public void Dpkg_Parse_NullOrEmpty_GivesNoPackage()
    {
        Assert.Empty(DpkgHostPackages.Parse(null));
        Assert.Empty(DpkgHostPackages.Parse(string.Empty));
    }

    [Fact]
    public void Dpkg_Parse_ReadsTheArchitecture_AndKeepsBothArchitecturesOfAMultiarchPackage()
    {
        var packages = DpkgHostPackages.Parse(
            "ii \tlibc6\t2.39-0ubuntu8.3\tglibc\tamd64\nii \tlibc6\t2.39-0ubuntu8.3\tglibc\ti386\nii \ttzdata\t2024a-3ubuntu1\t\tall\n");

        Assert.Equal([("libc6", "amd64"), ("libc6", "i386"), ("tzdata", "all")], packages.Select(p => (p.Name, p.Architecture)));
        Assert.All(packages.Where(p => p.Name == "libc6"), p => Assert.Equal("glibc", p.SourceName));
    }

    [Theory]
    [InlineData("AMD64")]
    [InlineData("amd64;rm")]
    [InlineData("an-architecture-name-far-too-long-to-be-real")]
    public void Dpkg_Parse_AnUnreadableArchitecture_IsLeftOut_ThePackageKept(string architecture)
    {
        var package = Assert.Single(DpkgHostPackages.Parse($"ii \tbash\t5.2\t\t{architecture}\n"));

        Assert.Null(package.Architecture);
        Assert.Equal("5.2", package.Version);
    }

    // The report contract the back enforces on the agent's push.

    [Fact]
    public void Report_ADpkgHostWithoutPackages_IsInvalid_ItWouldPassForACleanHost()
    {
        var report = new HostPackageInventoryReportDto { OsId = "ubuntu", OsVersionId = "24.04", Packages = [] };

        Assert.Contains(Validate(report), r => r.MemberNames.Contains(nameof(HostPackageInventoryReportDto.Packages)));
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("Debian")]
    public void Report_UnsupportedOnADebianFamilySystem_IsInvalid(string osId)
    {
        var report = new HostPackageInventoryReportDto { OsId = osId, PackageManagerSupported = false, Packages = [] };

        Assert.Contains(Validate(report), r => r.MemberNames.Contains(nameof(HostPackageInventoryReportDto.PackageManagerSupported)));
    }

    [Fact]
    public void Report_UnsupportedWithPackages_IsInvalid()
    {
        var report = new HostPackageInventoryReportDto
        {
            OsId = "rocky",
            PackageManagerSupported = false,
            Packages = [new HostPackageDto { Name = "bash", Version = "5.1" }]
        };

        Assert.NotEmpty(Validate(report));
    }

    private static List<System.ComponentModel.DataAnnotations.ValidationResult> Validate(object model)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            model, new System.ComponentModel.DataAnnotations.ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static string Packages(int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++) text.Append("ii \tpkg").Append(i).Append("\t1.0\t\n");
        return text.ToString();
    }

    private sealed class Harness : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
        private readonly int? _serverId;
        private HostPackageInventoryReporter? _reporter;
        private int _attempts;
        private int _shellCalls;
        private int _failSends;

        public Harness(int? serverId = 7)
        {
            _serverId = serverId;
            Time = new FakeTimeProvider(Start);
            Health = new AgentRuntimeHealth(Time);
            Files["/etc/os-release"] = UbuntuRelease;
        }

        public FakeTimeProvider Time { get; }
        public AgentRuntimeHealth Health { get; }
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<(int ServerId, HostPackageInventoryReportDto Report)> Sent { get; } = [];
        public ShellExecResult Dpkg { get; set; } = new(0, TwoPackages, string.Empty);
        public Exception? ShellFailure { get; init; }
        public bool Enrolled { get; set; } = true;
        public bool IsLinux { get; init; } = true;
        public int Attempts => Volatile.Read(ref _attempts);
        public int ShellCalls => Volatile.Read(ref _shellCalls);
        public int FailSends { init => _failSends = value; }

        public IShellRunner Shell { get; private set; } = null!;

        /// <summary>Built on first use, so the initializer values (IsLinux, FailSends...) are in place.</summary>
        public HostPackageInventoryReporter Reporter => _reporter ??= Build();

        public void Dispose() => _reporter?.Dispose();

        private HostPackageInventoryReporter Build()
        {
            Shell = Substitute.For<IShellRunner>();
            Shell.RunExecAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref _shellCalls);
                return ShellFailure is null ? Task.FromResult(Dpkg) : Task.FromException<ShellExecResult>(ShellFailure);
            });
            var api = Substitute.For<IServerApiClient>();
            api.ReportHostPackagesAsync(default, default!, default).ReturnsForAnyArgs(call =>
            {
                Interlocked.Increment(ref _attempts);
                if (Interlocked.Decrement(ref _failSends) >= 0) return Task.FromException(new HttpRequestException("503"));
                lock (Sent) Sent.Add((call.ArgAt<int>(0), call.ArgAt<HostPackageInventoryReportDto>(1)));
                return Task.CompletedTask;
            });
            var enrollment = Substitute.For<IEnrollmentService>();
            enrollment.IsEnrolled.Returns(_ => Enrolled);
            return new HostPackageInventoryReporter(
                api, enrollment, new AgentState { ServerId = _serverId }, Health, Shell, Time,
                NullLogger<HostPackageInventoryReporter>.Instance)
            {
                IsLinux = IsLinux,
                ReadFile = path => Files.TryGetValue(path, out var text) ? text : null
            };
        }

        /// <summary>Advances the fake clock by <paramref name="total"/>, one <paramref name="step"/> at a time, letting the
        /// reporter's loop run between steps; true as soon as <paramref name="done"/> holds.</summary>
        public async Task<bool> AdvanceAsync(TimeSpan total, TimeSpan step, Func<bool> done)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
            {
                if (await WaitForAsync(done, TimeSpan.FromMilliseconds(15))) return true;
                Time.Advance(step);
            }

            return await WaitForAsync(done, TimeSpan.FromMilliseconds(15));
        }

        public static async Task<bool> WaitForAsync(Func<bool> done, TimeSpan? timeout = null)
        {
            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < until)
            {
                if (done()) return true;
                await Task.Delay(5);
            }

            return done();
        }
    }
}
