// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A scanner run from the repository's own packages reports the version actually installed,
/// else the one the lock file pins; it reports none rather than guessing.</summary>
public sealed class ScannerVersionResolutionTests : IDisposable
{
    private readonly string _source = Directory.CreateTempSubdirectory("aetheus-scanner-version-").FullName;
    private static readonly ScannerManifestEntry Eslint = new() { Key = "eslint", Version = "9.0.0", VersionPackage = "eslint" };

    public void Dispose() => Directory.Delete(_source, recursive: true);

    [Fact]
    public void AManifestVersion_IsUsedWhenTheScannerIsNotAPackage() =>
        Assert.Equal("1.2.3", ScannerOperationValidator.ResolveScannerVersion(
            new ScannerManifestEntry { Key = "gitleaks", Version = "1.2.3" }, _source));

    [Fact]
    public void TheInstalledPackage_GivesTheVersion()
    {
        WritePackage("""{ "name": "eslint", "version": "9.12.0" }""");
        WriteLock("""{ "packages": { "node_modules/eslint": { "version": "9.11.0" } } }""");

        Assert.Equal("9.12.0", ScannerOperationValidator.ResolveScannerVersion(Eslint, _source));
    }

    [Theory]
    [InlineData("""{ "name": "eslint" }""")]
    [InlineData("""{ "name": "eslint", "version": 9 }""")]
    [InlineData("""{ "name": "eslint", "version": " " }""")]
    [InlineData(null)]
    public void WithoutAnInstalledVersion_TheLockFileGivesIt(string? package)
    {
        if (package is not null) WritePackage(package);
        WriteLock("""{ "packages": { "node_modules/eslint": { "version": "9.11.0" } } }""");

        Assert.Equal("9.11.0", ScannerOperationValidator.ResolveScannerVersion(Eslint, _source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "lockfileVersion": 3 }""")]
    [InlineData("""{ "packages": [] }""")]
    [InlineData("""{ "packages": { "node_modules/prettier": { "version": "3.0.0" } } }""")]
    [InlineData("""{ "packages": { "node_modules/eslint": { } } }""")]
    [InlineData("""{ "packages": { "node_modules/eslint": { "version": 9 } } }""")]
    [InlineData("""{ "packages": { "node_modules/eslint": { "version": "" } } }""")]
    public void WithoutAnyPinnedVersion_NoneIsReported(string? lockFile)
    {
        if (lockFile is not null) WriteLock(lockFile);

        Assert.Null(ScannerOperationValidator.ResolveScannerVersion(Eslint, _source));
    }

    private void WritePackage(string json)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_source, "node_modules", "eslint")).FullName;
        File.WriteAllText(Path.Combine(directory, "package.json"), json);
    }

    private void WriteLock(string json) => File.WriteAllText(Path.Combine(_source, "package-lock.json"), json);
}
