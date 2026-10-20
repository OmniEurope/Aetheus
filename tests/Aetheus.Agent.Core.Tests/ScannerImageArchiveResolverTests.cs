// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Verifies scanner archive resolution order, ambiguous-role rejection and traversal
/// protection for declared archive names.</summary>
public sealed class ScannerImageArchiveResolverTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"aetheus-image-archive-{Guid.NewGuid():N}");

    public ScannerImageArchiveResolverTests() => Directory.CreateDirectory(_directory);

    private static Dictionary<string, string> Env(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private void WriteArchive(string name) => File.WriteAllText(Path.Combine(_directory, name), "tar");

    // One Fact rather than a Theory: ScannerImageRole is internal, so it cannot appear in the
    // signature of a public test method.
    [Fact]
    public void ResolveRole_MapsOnlyTheFourBuiltArtifactScanners()
    {
        Assert.Equal(ScannerImageRole.Backend, ScannerImageArchiveResolver.ResolveRole("trivy-image"));
        Assert.Equal(ScannerImageRole.Backend, ScannerImageArchiveResolver.ResolveRole("syft"));
        Assert.Equal(ScannerImageRole.Frontend, ScannerImageArchiveResolver.ResolveRole("trivy-image-front"));
        Assert.Equal(ScannerImageRole.Frontend, ScannerImageArchiveResolver.ResolveRole("syft-front"));
        Assert.Equal(ScannerImageRole.None, ScannerImageArchiveResolver.ResolveRole("gitleaks"));
        Assert.Equal(ScannerImageRole.None, ScannerImageArchiveResolver.ResolveRole("zap-api"));
    }

    [Theory]
    [InlineData("aetheus-back.tar")]
    // The whole point of the fix: a project named after itself resolves with zero configuration.
    [InlineData("atlas-back.tar")]
    [InlineData("some-other-project-back.tar")]
    public void DiscoversTheBackendArchiveWhateverTheProjectIsCalled(string archive)
    {
        WriteArchive(archive);

        var result = ScannerImageArchiveResolver.Resolve(ScannerImageRole.Backend, _directory, Env());

        Assert.Equal(ImageArchiveOutcome.Resolved, result.Outcome);
        Assert.Equal(archive, result.FileName);
    }

    [Fact]
    public void DiscoveryKeepsTheTwoRolesApart()
    {
        WriteArchive("atlas-back.tar");
        WriteArchive("atlas-front.tar");

        Assert.Equal("atlas-back.tar",
            ScannerImageArchiveResolver.Resolve(ScannerImageRole.Backend, _directory, Env()).FileName);
        Assert.Equal("atlas-front.tar",
            ScannerImageArchiveResolver.Resolve(ScannerImageRole.Frontend, _directory, Env()).FileName);
    }

    [Fact]
    public void RefusesAmbiguityInsteadOfPickingOne()
    {
        WriteArchive("atlas-back.tar");
        WriteArchive("legacy-back.tar");

        var result = ScannerImageArchiveResolver.Resolve(ScannerImageRole.Backend, _directory, Env());

        Assert.Equal(ImageArchiveOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.FileName);
        // The message must name the way out, otherwise the operator has to read the source.
        Assert.Contains(ScannerImageArchiveResolver.BackendArchiveVariable, result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredNameWinsOverDiscovery()
    {
        WriteArchive("atlas-back.tar");
        WriteArchive("payload.tar");

        var result = ScannerImageArchiveResolver.Resolve(
            ScannerImageRole.Backend, _directory,
            Env((ScannerImageArchiveResolver.BackendArchiveVariable, "payload.tar")));

        Assert.Equal(ImageArchiveOutcome.Resolved, result.Outcome);
        Assert.Equal("payload.tar", result.FileName);
    }

    [Theory]
    // A declared name is joined onto the image directory, so a separator or a traversal segment
    // would read outside it.
    [InlineData("../escape.tar")]
    [InlineData("nested/back.tar")]
    [InlineData("..")]
    public void ADeclaredNameThatIsNotAPlainFileNameIsRefused(string declared)
    {
        var result = ScannerImageArchiveResolver.Resolve(
            ScannerImageRole.Backend, _directory,
            Env((ScannerImageArchiveResolver.BackendArchiveVariable, declared)));

        Assert.Equal(ImageArchiveOutcome.InvalidDeclaredName, result.Outcome);
        Assert.Null(result.FileName);
    }

    [Fact]
    public void ReportsAMissingArchiveAsMissing_NotAsARevisionProblem()
    {
        WriteArchive("atlas-front.tar");

        var result = ScannerImageArchiveResolver.Resolve(ScannerImageRole.Backend, _directory, Env());

        Assert.Equal(ImageArchiveOutcome.NotFound, result.Outcome);
        Assert.Contains("-back.tar", result.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("revision", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsAMissingDeclaredArchiveByName()
    {
        var result = ScannerImageArchiveResolver.Resolve(
            ScannerImageRole.Backend, _directory,
            Env((ScannerImageArchiveResolver.BackendArchiveVariable, "absent.tar")));

        Assert.Equal(ImageArchiveOutcome.NotFound, result.Outcome);
        Assert.Contains("absent.tar", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentImageDirectoryIsReportedAsSuch()
    {
        var result = ScannerImageArchiveResolver.Resolve(
            ScannerImageRole.Backend, Path.Combine(_directory, "missing"), Env());

        Assert.Equal(ImageArchiveOutcome.NotFound, result.Outcome);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort: a temp directory left behind fails nothing */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }
}
