// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.AgentUpdate;
using Microsoft.Net.Http.Headers;

namespace Aetheus.Back.Extensions;

/// <summary>
/// Maps the two anonymous endpoints serving the Linux/Windows agent archives, and provides
/// helpers to build / re-pack them with a server-URL marker.
/// Lives outside <c>Program.cs</c> to keep host wiring readable.
/// </summary>
internal static class AgentDownloadsExtensions
{
    internal sealed record PreparedArchive(string Path, string Sha256);
    private static readonly ConcurrentDictionary<string, Lazy<Task<PreparedArchive>>> PreparedLinuxArchives = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> FileHashes = new();

    public static WebApplication MapAgentDownloads(this WebApplication app)
    {
        var downloadsPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "downloads");
        Directory.CreateDirectory(downloadsPath);

        var rawAppVersion = app.Configuration["App:Version"] ?? "dev";
        var safeAppVersion = Regex.Replace(rawAppVersion, "[^a-zA-Z0-9._-]", "-");

        EnsureAgentDownloadPackages(
            app.Environment.ContentRootPath,
            downloadsPath,
            app.Environment.IsDevelopment());

        app.MapGet("/downloads/agent-release-manifest.json", (IAgentReleaseCatalog releases) =>
            Results.Json(releases.Current)).AllowAnonymous();

        app.MapGet(
            "/downloads/releases/{version}/{fileName}",
            (string version, string fileName, HttpContext context, IAgentReleaseCatalog releases) =>
            {
                var manifest = releases.Current;
                if (!string.Equals(version, manifest.SoftwareVersion, StringComparison.Ordinal)
                    || Path.GetFileName(fileName) != fileName)
                {
                    return Results.NotFound();
                }

                var archive = manifest.Archives.SingleOrDefault(item =>
                    string.Equals(item.FileName, fileName, StringComparison.Ordinal));
                if (archive is null)
                    return Results.NotFound();

                var path = Path.Combine(downloadsPath, "releases", version, fileName);
                if (!File.Exists(path))
                    return Results.NotFound();

                context.Response.Headers["X-Content-SHA256"] = archive.Sha256;
                var contentType = archive.Platform == "windows"
                    ? "application/zip"
                    : "application/gzip";
                return Results.File(path, contentType, fileName, enableRangeProcessing: false);
            }).AllowAnonymous();

        app.MapGet("/downloads/aetheus-agent-linux-x64.tar.gz", async (HttpContext httpContext) =>
        {
            httpContext.Response.ContentType = "application/gzip";
            httpContext.Response.Headers[HeaderNames.ContentDisposition] =
                $"attachment; filename=\"aetheus-agent-linux-x64-v{safeAppVersion}.tar.gz\"";

            // Resolve the public API URL from configuration - never derive from Request.Host.
            // Request.Host may point to the frontend hostname when the user downloads through
            // the front; baking that value into .aetheus-server-url would make the agent
            // POST /api/auth/register on the frontend and hit 405 Method Not Allowed.
            var serverUrl = HostUrlExtensions.ResolvePublicApiUrl(app.Configuration, httpContext);

            // Freshness guard: the dev archive is rebuilt from the linux-x64 publish output, which is
            // refreshed by `dotnet publish -r linux-x64` (ylaunch does this after a build) - NOT by a
            // plain `dotnet build`. If that publish silently lagged the latest agent build, an agent
            // self-update/install would pull stale code. Warn loudly so it's caught before deploy.
            WarnIfLinuxAgentPublishStale(app);

            // Serve pre-built archive if present (Docker / production).
            // The Dockerfile creates a versioned file (e.g. aetheus-agent-linux-x64-v1.0.0.tar.gz).
            var prebuiltArchive = Directory.GetFiles(downloadsPath, "aetheus-agent-linux-x64*.tar.gz")
                .OrderByDescending(f => f)
                .FirstOrDefault();

            // Both paths re-pack to a temp file so .aetheus-server-url reflects the configured
            // API URL - which also lets us hash the exact payload before streaming it (F-004:
            // X-Content-SHA256 is verified fail-closed by the agent self-update).
            var solutionRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
            var installScript = Path.Combine(solutionRoot, "deploy", "agent-installer", "install-agent-linux.sh");
            var hostConfig = HostConfigSourcePath(solutionRoot);

            if (prebuiltArchive is not null)
            {
                var prepared = await GetPreparedLinuxArchiveAsync(prebuiltArchive, serverUrl, installScript, hostConfig);
                httpContext.Response.Headers["X-Content-SHA256"] = prepared.Sha256;
                await using var preparedStream = new FileStream(
                    prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                httpContext.Response.ContentLength = preparedStream.Length;
                await preparedStream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
                return;
            }

            var tempArchivePath = await TryBuildLinuxAgentArchiveAsync(app.Environment.ContentRootPath, serverUrl);

            if (tempArchivePath is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            try
            {
                httpContext.Response.Headers["X-Content-SHA256"] = await ComputeFileSha256Async(tempArchivePath);
                await using var archiveStream = File.OpenRead(tempArchivePath);
                httpContext.Response.ContentLength = archiveStream.Length;
                await archiveStream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            }
            finally
            {
                if (File.Exists(tempArchivePath))
                    File.Delete(tempArchivePath);
            }
        }).AllowAnonymous();

        app.MapGet("/downloads/aetheus-agent-win-x64.zip", async (HttpContext httpContext) =>
        {
            httpContext.Response.ContentType = "application/zip";
            httpContext.Response.Headers[HeaderNames.ContentDisposition] =
                $"attachment; filename=\"aetheus-agent-win-x64-v{safeAppVersion}.zip\"";

            var prebuiltArchive = Directory.GetFiles(downloadsPath, "aetheus-agent-win-x64*.zip")
                .OrderByDescending(f => f)
                .FirstOrDefault();

            if (prebuiltArchive != null)
            {
                httpContext.Response.Headers["X-Content-SHA256"] = await GetCachedFileSha256Async(prebuiltArchive);
                await using var fs = new FileStream(
                    prebuiltArchive, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                httpContext.Response.ContentLength = fs.Length;
                await fs.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }).AllowAnonymous();

        var downloadsContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        downloadsContentTypeProvider.Mappings[".tar.gz"] = "application/gzip";
        downloadsContentTypeProvider.Mappings[".tgz"] = "application/gzip";
        downloadsContentTypeProvider.Mappings[".msi"] = "application/x-msi";
        downloadsContentTypeProvider.Mappings[".sh"] = "application/x-sh";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(downloadsPath),
            RequestPath = "/downloads",
            ContentTypeProvider = downloadsContentTypeProvider,
            ServeUnknownFileTypes = false,
        });

        return app;
    }

    internal static async Task<PreparedArchive> GetPreparedLinuxArchiveAsync(
        string prebuiltArchive, string serverUrl, string? freshInstallScript, string? freshHostConfig)
    {
        var archive = new FileInfo(prebuiltArchive);
        var scriptStamp = freshInstallScript is not null && File.Exists(freshInstallScript)
            ? File.GetLastWriteTimeUtc(freshInstallScript).Ticks
            : 0;
        // A template edit must refresh the prepared archive exactly like a script edit does.
        var hostConfigStamp = freshHostConfig is not null && Directory.Exists(freshHostConfig)
            ? string.Join(',', Directory.GetFiles(freshHostConfig, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => $"{file}:{File.GetLastWriteTimeUtc(file).Ticks}"))
            : string.Empty;
        var key = string.Join('|', archive.FullName, archive.Length, archive.LastWriteTimeUtc.Ticks, scriptStamp, hostConfigStamp, serverUrl);
        var lazy = PreparedLinuxArchives.GetOrAdd(
            key,
            _ => new Lazy<Task<PreparedArchive>>(
                async () =>
                {
                    var path = await BuildPrebuiltLinuxArchiveWithMarkerAsync(
                        prebuiltArchive, serverUrl, freshInstallScript, freshHostConfig).ConfigureAwait(false);
                    return new PreparedArchive(path, await ComputeFileSha256Async(path).ConfigureAwait(false));
                },
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var prepared = await lazy.Value.ConfigureAwait(false);
            if (File.Exists(prepared.Path)) return prepared;

            PreparedLinuxArchives.TryRemove(key, out _);
            throw new IOException("Prepared Linux agent archive disappeared before it could be served.");
        }
        catch
        {
            PreparedLinuxArchives.TryRemove(key, out _);
            throw;
        }
    }

    private static readonly Lock _packageLock = new();

    private static async Task<string> GetCachedFileSha256Async(string path)
    {
        var file = new FileInfo(path);
        var key = string.Join('|', file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        var lazy = FileHashes.GetOrAdd(
            key,
            _ => new Lazy<Task<string>>(
                () => ComputeFileSha256Async(path),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            FileHashes.TryRemove(key, out _);
            throw;
        }
    }

    private static void EnsureAgentDownloadPackages(
        string backContentRootPath,
        string downloadsPath,
        bool refreshDevelopmentPackages)
    {
        var solutionRootPath = Path.GetFullPath(Path.Combine(backContentRootPath, "..", ".."));
        var scriptsPath = Path.Combine(solutionRootPath, "deploy", "agent-installer");

        // Windows agent: build from local Debug output only if no pre-built archive exists.
        // In Docker/production, the Dockerfile already created the versioned zip - skip rebuilding.
        // Lock: parallel WebApplicationFactory instances in integration tests race here.
        lock (_packageLock)
        {
            var existingWinZip = Directory.GetFiles(downloadsPath, "aetheus-agent-win-x64*.zip").FirstOrDefault();
            if (existingWinZip == null || refreshDevelopmentPackages)
            {
                var windowsBuildPath = Path.Combine(solutionRootPath, "src", "Aetheus.Agent.Windows", "bin", "Debug", "net10.0-windows");
                var windowsInstallScriptPath = Path.Combine(scriptsPath, "install-agent-windows.ps1");

                if (Directory.Exists(windowsBuildPath) && File.Exists(windowsInstallScriptPath))
                {
                    // The lock above is per process: two processes sharing this tree (two test runs, a
                    // test run beside the dev instance) each stage in their own folder, and a failed
                    // attempt leaves neither its folder nor its archive behind. The staging folder lives
                    // in the temp directory, never under wwwroot: the source-scanning guards of a test
                    // suite running at the same time walk this tree and would meet a folder that vanishes
                    // mid-scan. The archive itself is written beside its final name, so the move that
                    // replaces it stays on one volume and a concurrent download never reads half a zip.
                    var attempt = Guid.NewGuid().ToString("N");
                    var windowsPackageTempPath = Path.Combine(Path.GetTempPath(), $"aetheus-windows-pkg-{attempt}");
                    var replacementArchivePath = Path.Combine(downloadsPath, $"_aetheus-agent-win-x64-{attempt}.zip");
                    try
                    {
                        RecreateDirectory(windowsPackageTempPath);
                        CopyDirectoryContent(windowsBuildPath, windowsPackageTempPath);
                        File.Copy(windowsInstallScriptPath, Path.Combine(windowsPackageTempPath, "install-agent-windows.ps1"), true);
                        ZipFile.CreateFromDirectory(
                            windowsPackageTempPath,
                            replacementArchivePath,
                            CompressionLevel.Optimal,
                            includeBaseDirectory: false);
                        ReplaceArchive(replacementArchivePath, Path.Combine(downloadsPath, "aetheus-agent-win-x64.zip"));
                    }
                    finally
                    {
                        if (Directory.Exists(windowsPackageTempPath)) Directory.Delete(windowsPackageTempPath, true);
                        if (File.Exists(replacementArchivePath)) File.Delete(replacementArchivePath);
                    }
                }
            }
        }

        // Linux agent: pre-built versioned archive (Docker) is served by the MapGet endpoint on demand.
        // In dev mode, the endpoint builds dynamically from Debug output when requested.
    }

    /// <summary>Builds the dev-mode Linux archive from Debug output into a temp file the caller owns (and deletes).</summary>
    internal static async Task<string?> TryBuildLinuxAgentArchiveAsync(string backContentRootPath, string serverUrl)
    {
        var solutionRootPath = Path.GetFullPath(Path.Combine(backContentRootPath, "..", ".."));
        // Prefer cross-compiled linux-x64 publish output (produced by ylaunch -ra).
        var linuxPublishPath = Path.Combine(solutionRootPath, "src", "Aetheus.Agent.Linux", "bin", "Debug", "net10.0", "linux-x64", "publish");
        var linuxBuildPath = Directory.Exists(linuxPublishPath)
            ? linuxPublishPath
            : Path.Combine(solutionRootPath, "src", "Aetheus.Agent.Linux", "bin", "Debug", "net10.0");
        var linuxInstallScriptPath = Path.Combine(solutionRootPath, "deploy", "agent-installer", "install-agent-linux.sh");
        var hostConfigPath = HostConfigSourcePath(solutionRootPath);

        // The installer refuses to run without its templates, so an archive without them is no
        // archive.
        if (!Directory.Exists(linuxBuildPath) || !File.Exists(linuxInstallScriptPath) || !Directory.Exists(hostConfigPath))
            return null;

        var linuxPackageTempPath = Path.Combine(Path.GetTempPath(), $"aetheus-linux-pkg-{Guid.NewGuid():N}");
        RecreateDirectory(linuxPackageTempPath);

        try
        {
            CopyDirectoryContent(linuxBuildPath, linuxPackageTempPath);
            // Normalize shell script to LF - File.Copy preserves CRLF from Windows checkout.
            var installScriptDest = Path.Combine(linuxPackageTempPath, "install-agent-linux.sh");
            File.Copy(linuxInstallScriptPath, installScriptDest, true);
            NormalizeToLf(installScriptDest);
            CopyHostConfigTemplates(hostConfigPath, linuxPackageTempPath);
            File.WriteAllText(Path.Combine(linuxPackageTempPath, ".aetheus-server-url"), serverUrl + "\n");

            var tempArchivePath = Path.Combine(Path.GetTempPath(), $"aetheus-linux-{Guid.NewGuid():N}.tar.gz");
            await using (var fs = new FileStream(tempArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var gzipStream = new GZipStream(fs, CompressionLevel.Optimal))
            {
                await WriteTarIncludingHiddenAsync(linuxPackageTempPath, gzipStream).ConfigureAwait(false);
            }

            return tempArchivePath;
        }
        finally
        {
            if (Directory.Exists(linuxPackageTempPath))
                Directory.Delete(linuxPackageTempPath, true);
        }
    }

    /// <summary>Re-packs the prebuilt Linux archive with the server-URL marker and the
    /// current install script and host configuration templates into a temp file the caller owns (and deletes).</summary>
    internal static async Task<string> BuildPrebuiltLinuxArchiveWithMarkerAsync(
        string prebuiltArchive, string serverUrl, string? freshInstallScript, string? freshHostConfig)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"aetheus-linux-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            await using (var fs = File.OpenRead(prebuiltArchive))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gz, extractDir, overwriteFiles: true).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(extractDir, ".aetheus-server-url"),
                serverUrl + "\n").ConfigureAwait(false);

            // Overlay the current install script so prebuilt archives
            // pick up flag additions (--module, --allow-insecure-certs, etc.).
            if (freshInstallScript is not null && File.Exists(freshInstallScript))
            {
                var dest = Path.Combine(extractDir, "install-agent-linux.sh");
                File.Copy(freshInstallScript, dest, true);
                NormalizeToLf(dest);
            }

            // The templates the overlaid script reads travel with it, never an older copy.
            if (freshHostConfig is not null && Directory.Exists(freshHostConfig))
                CopyHostConfigTemplates(freshHostConfig, extractDir);

            var tempArchivePath = Path.Combine(Path.GetTempPath(), $"aetheus-linux-{Guid.NewGuid():N}.tar.gz");
            await using (var fs = new FileStream(tempArchivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var gzipStream = new GZipStream(fs, CompressionLevel.Optimal))
            {
                await WriteTarIncludingHiddenAsync(extractDir, gzipStream).ConfigureAwait(false);
            }

            return tempArchivePath;
        }
        finally
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
        }
    }

    /// <summary>
    /// Logs a loud warning when the linux-x64 publish output the dev archive is built from is older
    /// than the latest agent build - i.e. someone built but the publish silently lagged, so a
    /// downloading agent would get stale code. No-op in Docker/prod (paths absent) and when fresh.
    /// </summary>
    internal static void WarnIfLinuxAgentPublishStale(WebApplication app)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
        var publishDll = Path.Combine(solutionRoot, "src", "Aetheus.Agent.Linux", "bin", "Debug", "net10.0", "linux-x64", "publish", "Aetheus.Agent.Core.dll");
        var buildDll = Path.Combine(solutionRoot, "src", "Aetheus.Agent.Core", "bin", "Debug", "net10.0", "Aetheus.Agent.Core.dll");
        if (!File.Exists(publishDll) || !File.Exists(buildDll)) return;

        var publishTime = File.GetLastWriteTimeUtc(publishDll);
        var buildTime = File.GetLastWriteTimeUtc(buildDll);
        // 5s slack absorbs build/publish ordering jitter.
        if (publishTime < buildTime.AddSeconds(-5))
        {
            app.Logger.LogWarning(
                "Served linux agent publish is STALE: published {PublishTime:u} but the agent was last built {BuildTime:u}. " +
                "A downloading agent will get OLDER code - re-run the linux-x64 publish (ylaunch does this after a build).",
                publishTime, buildTime);
        }
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Moves the freshly built archive over the served one. On Windows the target stays locked
    /// while another host process of the same checkout (parallel test hosts, a download in flight) holds
    /// it open, so the move is retried briefly; a lock that outlasts the retries still fails startup.</summary>
    private static void ReplaceArchive(string sourcePath, string targetPath)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);

        Directory.CreateDirectory(path);
    }

    /// <summary>Folder of the archive, next to install-agent-linux.sh, holding the host
    /// configuration templates the installer renders (deploy/agent-host-config in the
    /// repository).</summary>
    internal const string HostConfigFolderName = "agent-host-config";

    internal static string HostConfigSourcePath(string solutionRootPath)
        => Path.Combine(solutionRootPath, "deploy", HostConfigFolderName);

    /// <summary>Replaces <paramref name="packageRootPath"/>/agent-host-config with a copy of
    /// <paramref name="sourcePath"/>, every file normalised to LF like the install script: the
    /// installer refuses a template holding a carriage return.</summary>
    internal static void CopyHostConfigTemplates(string sourcePath, string packageRootPath)
    {
        var destination = Path.Combine(packageRootPath, HostConfigFolderName);
        if (Directory.Exists(destination))
            Directory.Delete(destination, true);

        CopyDirectoryContent(sourcePath, destination);
        foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
            NormalizeToLf(file);
    }

    /// <summary>Tars every entry under <paramref name="sourcePath"/>, hidden ones included.
    /// TarFile.CreateFromDirectoryAsync skips hidden entries, and on Linux every dot-file is hidden:
    /// the served archive lost its .aetheus-server-url marker there while a Windows host kept it.</summary>
    private static async Task WriteTarIncludingHiddenAsync(string sourcePath, Stream destination)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        await using var writer = new TarWriter(destination, leaveOpen: true);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(sourcePath, "*", options).Order(StringComparer.Ordinal))
        {
            var entryName = Path.GetRelativePath(sourcePath, entryPath).Replace('\\', '/');
            if (Directory.Exists(entryPath))
                entryName += "/";
            await writer.WriteEntryAsync(entryPath, entryName).ConfigureAwait(false);
        }
    }

    private static void NormalizeToLf(string filePath)
    {
        var content = File.ReadAllText(filePath);
        File.WriteAllText(filePath, content.Replace("\r\n", "\n").Replace("\r", "\n"));
    }

    private static void CopyDirectoryContent(string sourcePath, string destinationPath)
    {
        foreach (var sourceFilePath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, sourceFilePath);
            var destinationFilePath = Path.Combine(destinationPath, relativePath);
            var destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(destinationDirectoryPath))
                Directory.CreateDirectory(destinationDirectoryPath);

            File.Copy(sourceFilePath, destinationFilePath, true);
        }
    }
}
