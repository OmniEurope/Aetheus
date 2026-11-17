// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Analysis.DependencyBump;
using static Aetheus.Back.Tests.Analysis.DependencyBumpTestKit;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The .NET bump (PLAN-004, lot 4, point 6) on this repository's own manifests: the central <c>Directory.Packages.props</c>, the
/// project file that references without a version, and the real <c>packages.lock.json</c>. The edited text is compared whole with
/// the original, so formatting, comments and every other version are proved untouched; the lock file is never edited, only named.
/// </summary>
public sealed class DependencyBumpNuGetTests
{
    private const string BackProject = "src/Aetheus.Back/Aetheus.Back.csproj";
    private const string BackLock = "src/Aetheus.Back/packages.lock.json";

    private static readonly string Props = Fixture("Directory.Packages.props");
    private static readonly string Project = Fixture("Aetheus.Back.csproj");
    private static readonly string Lock = Fixture("Aetheus.Back.packages.lock.json");

    private static (string, string)[] RealFiles() =>
    [
        ("Directory.Packages.props", Props), (BackProject, Project), (BackLock, Lock)
    ];

    [Fact]
    public void ACentralVersion_IsRaisedInDirectoryPackagesProps_AndTheLockFileIsNamedNotEdited()
    {
        var result = Plan(Request("NuGet", "Cronos", "0.13.0", "0.13.1", BackLock), RealFiles());

        Assert.Equal(DependencyBumpOutcome.NeedsPackageManager, result.Outcome);
        var edit = SingleEdit(result, "Directory.Packages.props", Props,
            "<PackageVersion Include=\"Cronos\" Version=\"0.13.0\" />", "<PackageVersion Include=\"Cronos\" Version=\"0.13.1\" />");
        var change = Assert.Single(edit.Changes);
        Assert.Equal(LineOf(Props, "Include=\"Cronos\""), change.Line);
        Assert.Contains("Cronos 0.13.0 -> 0.13.1", change.Description, StringComparison.Ordinal);
        var refresh = Assert.Single(result.LockRefreshes);
        Assert.Equal((BackLock, "src/Aetheus.Back", "dotnet restore --force-evaluate"), (refresh.LockFile, refresh.WorkingDirectory, refresh.Command));
        Assert.DoesNotContain(result.Edits, candidate => candidate.Path == BackLock);
    }

    [Fact]
    public void ASharedFileEdit_NamesEveryLockFileBelowItThatNamesThePackage()
    {
        var other = ("tools/Other/packages.lock.json", Lock);
        var unrelated = ("tools/Unrelated/packages.lock.json", """{"version":2,"dependencies":{"net10.0":{}}}""");

        var result = Plan(Request("NuGet", "Cronos", "0.13.0", "0.13.1", BackLock), [.. RealFiles(), other, unrelated]);

        Assert.Equal(
            [BackLock, "tools/Other/packages.lock.json"],
            result.LockRefreshes.Select(refresh => refresh.LockFile).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(result.LockRefreshes, refresh => refresh.LockFile.Contains("Unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void ATransitivePackage_NoManifestNames_IsRefusedNotInvented()
    {
        // Newtonsoft.Json 13.0.4 is in the real lock file as "Transitive": no project or props file writes it.
        var result = Plan(Request("NuGet", "Newtonsoft.Json", "13.0.4", "13.0.5", BackLock), RealFiles());

        AssertRefused(result, DependencyBumpRefusal.NotDeclared);
        Assert.Contains("another package", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionThatIsAProperty_IsRefusedAsNotPinned()
    {
        // The real example project writes Version="$(AetheusTelemetryVersion)".
        var project = Fixture("OptionalObservabilityExample.csproj");
        var result = Plan(
            Request("NuGet", "Aetheus.Telemetry", "1.2.0", "1.3.0", "examples/optional-observability/packages.lock.json"),
            ("examples/optional-observability/OptionalObservabilityExample.csproj", project),
            ("examples/optional-observability/packages.lock.json", Fixture("OptionalObservabilityExample.packages.lock.json")));

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
        Assert.Contains("$(AetheusTelemetryVersion)", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionWrittenInTheProjectFile_IsChangedThereAndTheCentralFileIsLeftAlone()
    {
        // The real project, with Cronos given its version in place and dropped from the central file.
        var inline = Project.Replace("<PackageReference Include=\"Cronos\" />", "<PackageReference Include=\"Cronos\" Version=\"0.13.0\" />", StringComparison.Ordinal);
        // The checkout may hold CRLF or LF line endings, so the line is dropped whichever it is.
        var props = Regex.Replace(Props, "[ ]*<PackageVersion Include=\"Cronos\" Version=\"0.13.0\" />\r?\n", string.Empty);
        Assert.DoesNotContain("Include=\"Cronos\"", props, StringComparison.Ordinal);

        var result = Plan(Request("NuGet", "Cronos", "0.13.0", "0.14.0", BackLock), ("Directory.Packages.props", props), (BackProject, inline), (BackLock, Lock));

        var edit = SingleEdit(result, BackProject, inline, "Include=\"Cronos\" Version=\"0.13.0\"", "Include=\"Cronos\" Version=\"0.14.0\"");
        Assert.Equal(LineOf(inline, "Include=\"Cronos\""), Assert.Single(edit.Changes).Line);
        Assert.Equal(BackLock, Assert.Single(result.LockRefreshes).LockFile);
    }

    [Fact]
    public void AVersionInAChildElement_IsChangedInPlace()
    {
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Serilog\">\n      <Version>4.0.0</Version>\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>\n  </ItemGroup>\n</Project>\n";

        var result = Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", project));

        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
        SingleEdit(result, "App/App.csproj", project, "<Version>4.0.0</Version>", "<Version>4.2.0</Version>");
        Assert.Equal(4, Assert.Single(Assert.Single(result.Edits).Changes).Line);
    }

    [Fact]
    public void WithoutALockFile_TheEditIsComplete()
    {
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Serilog\" Version=\"4.0.0\" /></ItemGroup></Project>";

        var result = Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", project));

        Assert.Equal(DependencyBumpOutcome.Complete, result.Outcome);
        Assert.Empty(result.LockRefreshes);
        SingleEdit(result, "App/App.csproj", project, "Version=\"4.0.0\"", "Version=\"4.2.0\"");
    }

    [Fact]
    public void OnlyTheDeclarationAtTheCurrentVersionChanges_ComparedWithoutCase_CommentsAndOtherVersionsStay()
    {
        var project = "﻿<Project>\r\n\t<!-- <PackageReference Include=\"Serilog\" Version=\"4.0.0\" /> was dropped -->\r\n\t<ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">\r\n"
            + "\t\t<PackageReference\r\n\t\t\tInclude=\"serilog\"\r\n\t\t\tVersion = '4.0.0' />\r\n\t</ItemGroup>\r\n"
            + "\t<ItemGroup Condition=\"'$(TargetFramework)' == 'net6.0'\">\r\n\t\t<PackageReference Include=\"Serilog\" Version=\"3.1.1\" />\r\n\t\t<PackageReference Include=\"Serilog.Sinks.File\" Version=\"4.0.0\" />\r\n\t</ItemGroup>\r\n</Project>\r\n";

        var result = Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", project));

        var edit = Assert.Single(result.Edits);
        Assert.Equal(project.Replace("Version = '4.0.0'", "Version = '4.2.0'", StringComparison.Ordinal), edit.Content);
        Assert.StartsWith("﻿<Project>\r\n", edit.Content, StringComparison.Ordinal);
        Assert.Equal(6, Assert.Single(edit.Changes).Line);
    }

    [Fact]
    public void AVersionOverrideAtTheCurrentVersion_ChangesWithTheCentralOne()
    {
        var props = "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"4.0.0\" /></ItemGroup></Project>";
        var project = "<Project><ItemGroup><PackageReference Include=\"Serilog\" VersionOverride=\"4.0.0\" /></ItemGroup></Project>";

        var result = Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("Directory.Packages.props", props), ("App/App.csproj", project));

        Assert.Equal(["App/App.csproj", "Directory.Packages.props"], result.Edits.Select(edit => edit.Path).Order(StringComparer.Ordinal));
        Assert.All(result.Edits, edit => Assert.DoesNotContain("4.0.0", edit.Content, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[4.0.0]")]
    [InlineData("4.*")]
    [InlineData("[4.0.0,5.0.0)")]
    [InlineData("$(SerilogVersion)")]
    public void AVersionThatIsNotOneVersion_IsRefusedAsNotPinned(string written)
    {
        var project = $"<Project><ItemGroup><PackageReference Include=\"Serilog\" Version=\"{written}\" /></ItemGroup></Project>";

        var result = Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", project));

        AssertRefused(result, DependencyBumpRefusal.VersionNotPinned);
    }

    [Fact]
    public void AVersionWrittenWithAnEntity_IsRefusedRatherThanRewrittenBlind()
    {
        var project = "<Project><ItemGroup><PackageReference Include=\"Serilog\" Version=\"4&#46;0.0\" /></ItemGroup></Project>";

        AssertRefused(Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", project)), DependencyBumpRefusal.VersionNotPinned);
    }

    [Theory]
    [InlineData("<Project><unclosed></Project>")]
    [InlineData("not xml at all")]
    public void AFileThatIsNotXml_IsRefusedAsUnreadable(string text) =>
        AssertRefused(Plan(Request("NuGet", "Serilog", "4.0.0", "4.2.0", "App/App.csproj"), ("App/App.csproj", text)), DependencyBumpRefusal.UnreadableManifest);

    [Fact]
    public void ALockFileWithoutAProjectBesideIt_IsRefusedAsManifestNotFound() =>
        AssertRefused(
            Plan(Request("NuGet", "Cronos", "0.13.0", "0.13.1", BackLock), (BackLock, Lock)),
            DependencyBumpRefusal.ManifestNotFound);

    [Fact]
    public void ASourceThatIsNotAProjectNorALockFile_IsRefusedAsManifestNotFound() =>
        AssertRefused(
            Plan(Request("NuGet", "Cronos", "0.13.0", "0.13.1", "README.md"), ("README.md", "text")),
            DependencyBumpRefusal.ManifestNotFound);
}
