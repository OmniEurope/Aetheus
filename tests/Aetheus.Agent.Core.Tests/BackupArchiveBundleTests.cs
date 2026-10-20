// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public class BackupArchiveBundleTests
{
    [Fact]
    public async Task CreateVerifyAndRestoreAsync_CombinedDatabaseDirectoryAndFile_RestoresSnapshot()
    {
        var root = NewRoot();
        try
        {
            var directory = Path.Combine(root, "application-data");
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), "before-config", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(nested, "state.bin"), "before-state", TestContext.Current.CancellationToken);
            var singleFile = Path.Combine(root, "single.txt");
            await File.WriteAllTextAsync(singleFile, "before-single", TestContext.Current.CancellationToken);
            var databaseDump = Path.Combine(root, "database.dump");
            await File.WriteAllTextAsync(databaseDump, "database-snapshot", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "archives", "backup.aetheus-backup");

            await BackupArchiveBundle.CreateAsync(
                archive, databaseDump, [directory, singleFile], TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), "after-config", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "extra.txt"), "not-in-snapshot", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(singleFile, "after-single", TestContext.Current.CancellationToken);

            using var verified = await BackupArchiveBundle.OpenVerifiedAsync(
                archive, TestContext.Current.CancellationToken);
            Assert.NotNull(verified.DatabaseDumpPath);
            Assert.Equal(
                "database-snapshot",
                await File.ReadAllTextAsync(verified.DatabaseDumpPath, TestContext.Current.CancellationToken));

            await BackupArchiveBundle.RestoreFilesAsync(verified, TestContext.Current.CancellationToken);

            Assert.Equal("before-config", await File.ReadAllTextAsync(
                Path.Combine(directory, "config.json"), TestContext.Current.CancellationToken));
            Assert.Equal("before-state", await File.ReadAllTextAsync(
                Path.Combine(nested, "state.bin"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, "extra.txt")));
            Assert.Equal("before-single", await File.ReadAllTextAsync(
                singleFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CreateAsync_FilesOnlyArchive_IsVerified()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "data");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "value.txt"), "value", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "backup.aetheus-backup");

            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [source], TestContext.Current.CancellationToken);
            using var verified = await BackupArchiveBundle.OpenVerifiedAsync(
                archive, TestContext.Current.CancellationToken);

            Assert.Null(verified.DatabaseDumpPath);
            Assert.Single(verified.Sources);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CreateVerifyAndRestoreAsync_PreservesEmptyDirectoriesTimestampsAndUnixModes()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source");
            var empty = Path.Combine(source, "empty", "nested");
            Directory.CreateDirectory(empty);
            var executable = Path.Combine(source, "run.sh");
            await File.WriteAllTextAsync(
                executable, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);
            var timestamp = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(executable, timestamp);
            Directory.SetLastWriteTimeUtc(empty, timestamp);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(empty, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            var archive = Path.Combine(root, "backup.aetheus-backup");

            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [source], TestContext.Current.CancellationToken);
            Directory.Delete(source, recursive: true);

            using var verified = await BackupArchiveBundle.OpenVerifiedAsync(
                archive, TestContext.Current.CancellationToken);
            await BackupArchiveBundle.RestoreFilesAsync(verified, TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(empty));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(executable));
            Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(empty));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(executable));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(empty));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public async Task RestoreFilesAsync_FailureAtEveryBoundaryRestoresAllPreviousSources(
        int failingBoundaryValue,
        int failingIndex)
    {
        var failingBoundary = (BackupFileRestoreBoundary)failingBoundaryValue;
        var root = NewRoot();
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            await File.WriteAllTextAsync(Path.Combine(first, "value.txt"), "snapshot-first", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(second, "value.txt"), "snapshot-second", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "backup.aetheus-backup");
            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [first, second], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(first, "value.txt"), "current-first", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(second, "value.txt"), "current-second", TestContext.Current.CancellationToken);

            using var verified = await BackupArchiveBundle.OpenVerifiedAsync(
                archive, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => BackupArchiveBundle.RestoreFilesAsync(
                verified,
                TestContext.Current.CancellationToken,
                (boundary, index) =>
                {
                    if (boundary == failingBoundary && index == failingIndex)
                        throw new IOException("Injected restore boundary failure.");
                }));

            Assert.Equal("current-first", await File.ReadAllTextAsync(
                Path.Combine(first, "value.txt"), TestContext.Current.CancellationToken));
            Assert.Equal("current-second", await File.ReadAllTextAsync(
                Path.Combine(second, "value.txt"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".aetheus-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CreateAsync_MissingConfiguredPath_FailsInsteadOfReportingSuccess()
    {
        var root = NewRoot();
        try
        {
            var missing = Path.Combine(root, "missing");
            var archive = Path.Combine(root, "backup.aetheus-backup");

            var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                BackupArchiveBundle.CreateAsync(
                    archive, databaseDumpPath: null, [missing], TestContext.Current.CancellationToken));

            Assert.Equal(missing, error.FileName);
            Assert.False(File.Exists(archive));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OpenVerifiedAsync_TamperedFile_FailsIntegrityCheck()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "backup.aetheus-backup");
            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [source], TestContext.Current.CancellationToken);

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                var entry = Assert.Single(
                    zip.Entries,
                    candidate => candidate.FullName.StartsWith("files/", StringComparison.Ordinal));
                var name = entry.FullName;
                entry.Delete();
                var replacement = zip.CreateEntry(name);
                await using var stream = replacement.Open();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("tampered"), TestContext.Current.CancellationToken);
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupArchiveBundle.OpenVerifiedAsync(archive, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OpenVerifiedAsync_ManifestTargetsFilesystemRoot_RefusesRestore()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "original", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "backup.aetheus-backup");
            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [source], TestContext.Current.CancellationToken);

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                var manifestEntry = zip.GetEntry("manifest.json")!;
                string json;
                await using (var stream = manifestEntry.Open())
                using (var reader = new StreamReader(stream))
                    json = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
                manifestEntry.Delete();
                var manifest = JsonNode.Parse(json)!;
                manifest["Sources"]![0]!["OriginalPath"] = Path.GetPathRoot(root);
                var replacement = zip.CreateEntry("manifest.json");
                await using var output = new StreamWriter(replacement.Open());
                await output.WriteAsync(manifest.ToJsonString());
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupArchiveBundle.OpenVerifiedAsync(archive, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ParseFilePaths_InvalidJson_IsRejected()
    {
        var environment = new Dictionary<string, string>
        {
            [BackupConstants.FilePathsEnvVar] = "not-json"
        };

        Assert.Throws<InvalidDataException>(() => BackupArchiveBundle.ParseFilePaths(environment));
    }

    [Fact]
    public async Task CreateAsync_InvalidCanonicalPath_IsNormalizedToInvalidData()
    {
        var root = NewRoot();
        try
        {
            var invalid = Path.Combine(root, "invalid") + '\0';

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupArchiveBundle.CreateAsync(
                    Path.Combine(root, "backup.aetheus-backup"),
                    databaseDumpPath: null,
                    [invalid],
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ValidateExtractionBudget_ExactLimits_AreAccepted()
    {
        BackupArchiveBundle.ValidateExtractionBudget(
            [(BackupArchiveBundle.MaxCompressedBundleBytes, BackupArchiveBundle.MaxExpandedBundleBytes)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateExtractionBudget_ExceededLimit_IsRejected(bool compressed)
    {
        var entry = compressed
            ? (BackupArchiveBundle.MaxCompressedBundleBytes + 1, 0L)
            : (0L, BackupArchiveBundle.MaxExpandedBundleBytes + 1);

        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveBundle.ValidateExtractionBudget([entry]));
    }

    [Fact]
    public void IndexSourceFileCounts_OneHundredThousandHostileAssociationsStayBounded()
    {
        const int sourceCount = 200;
        const int fileCount = 100_000;
        var entries = Enumerable.Range(0, fileCount)
            .Select(index => $"files/{index % sourceCount}/nested/{index}.bin")
            .ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var counts = BackupArchiveBundle.IndexSourceFileCounts(entries);

        stopwatch.Stop();
        Assert.Equal(sourceCount, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(fileCount / sourceCount, count));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Indexing the maximum manifest file set took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("unknown-root", "Backup directory metadata is invalid.")]
    [InlineData("absolute-path", "Backup directory metadata is invalid.")]
    [InlineData("dot-dot-segment", "Backup directory metadata is invalid.")]
    [InlineData("duplicate", "Backup directory metadata is invalid.")]
    [InlineData("missing-timestamp", "Backup directory metadata is invalid.")]
    [InlineData("foreign-mode", "Backup directory metadata is invalid.")]
    [InlineData("missing-root", "Backup directory source is missing its root metadata.")]
    [InlineData("legacy-version", "Legacy backup manifests cannot contain directory metadata.")]
    public async Task OpenVerifiedAsync_TamperedDirectoryMetadata_IsRefusedWithItsReason(
        string tampering, string expectedMessage)
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "sub", "data.txt"), "data", TestContext.Current.CancellationToken);
            var archive = Path.Combine(root, "backup.aetheus-backup");
            await BackupArchiveBundle.CreateAsync(
                archive, databaseDumpPath: null, [source], TestContext.Current.CancellationToken);
            await RewriteManifestAsync(archive, manifest => TamperDirectories(manifest, tampering, root));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BackupArchiveBundle.OpenVerifiedAsync(archive, TestContext.Current.CancellationToken));

            Assert.Equal(expectedMessage, error.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static void TamperDirectories(JsonNode manifest, string tampering, string root)
    {
        var directories = manifest["Directories"]!.AsArray();
        var rootDirectory = directories.First(directory => (string?)directory!["RelativePath"] == string.Empty)!;
        var subDirectory = directories.First(directory => (string?)directory!["RelativePath"] != string.Empty)!;
        switch (tampering)
        {
            case "unknown-root": subDirectory["EntryRoot"] = "files/999"; break;
            case "absolute-path": subDirectory["RelativePath"] = Path.GetFullPath(root); break;
            case "dot-dot-segment": subDirectory["RelativePath"] = "sub/../escape"; break;
            case "duplicate": directories.Add(subDirectory.DeepClone()); break;
            case "missing-timestamp": subDirectory["LastWriteTimeUtcTicks"] = null; break;
            case "foreign-mode": subDirectory["UnixMode"] = OperatingSystem.IsWindows() ? 0x1ED : 0x1000; break;
            case "missing-root": directories.Remove(rootDirectory); break;
            case "legacy-version": manifest["Version"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(tampering), tampering, null);
        }
    }

    private static async Task RewriteManifestAsync(string archive, Action<JsonNode> tamper)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        var manifestEntry = zip.GetEntry("manifest.json")!;
        string json;
        await using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream))
            json = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        manifestEntry.Delete();
        var manifest = JsonNode.Parse(json)!;
        tamper(manifest);
        var replacement = zip.CreateEntry("manifest.json");
        await using var output = new StreamWriter(replacement.Open());
        await output.WriteAsync(manifest.ToJsonString());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-backup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
