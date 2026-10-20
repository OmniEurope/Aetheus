// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Agent.Core.Services;

public static class ScannerCapabilityProbe
{
    public static async Task<List<string>> CollectAsync(
        IShellRunner shell,
        string workDirectory,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        var diagnostics = new List<string>();
        var scansDirectory = Path.Combine(workDirectory, "scans");
        var residualWorkspaces = Directory.Exists(scansDirectory)
            ? Directory.EnumerateDirectories(scansDirectory).Take(101).Count()
            : 0;
        diagnostics.Add(residualWorkspaces == 0
            ? "scanner-cleanup:ready:0 residual workspaces"
            : $"scanner-cleanup:degraded:{residualWorkspaces} residual workspaces require operator review");
        diagnostics.Add($"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}");
        await AddTrivyDatabaseDiagnosticAsync(diagnostics, workDirectory, timeProvider, ct).ConfigureAwait(false);
        var dockerAvailable = await DockerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false);
        foreach (var scanner in ScannerManifestCatalog.Default.Scanners)
        {
            ct.ThrowIfCancellationRequested();
            diagnostics.Add(scanner.Execution == "container"
                ? await ContainerDiagnosticAsync(shell, scanner, dockerAvailable, ct).ConfigureAwait(false)
                : await BinaryDiagnosticAsync(scanner, workDirectory, IsLinuxX64, dockerAvailable, ct).ConfigureAwait(false));
        }
        return diagnostics;
    }

    private static bool IsLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>A container scanner: ready when its immutable image is cached, pulled on demand
    /// otherwise, unavailable without Docker or when the image probe fails.</summary>
    internal static async Task<string> ContainerDiagnosticAsync(
        IShellRunner shell, ScannerManifestEntry scanner, bool dockerAvailable, CancellationToken ct)
    {
        if (!dockerAvailable)
            return $"scanner:{scanner.Key}:{scanner.Version}:unavailable:docker daemon or socket unavailable";
        try
        {
            var result = await shell.RunExecAsync(
                "docker", ["image", "inspect", scanner.Image!], ct,
                AgentRuntimeDefaults.CapabilityProbeTimeout).ConfigureAwait(false);
            return result.ExitCode == 0
                ? $"scanner:{scanner.Key}:{scanner.Version}:ready:immutable image cached"
                : $"scanner:{scanner.Key}:{scanner.Version}:available-on-demand:immutable image pull required";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"scanner:{scanner.Key}:{scanner.Version}:unavailable:container image probe failed";
        }
    }

    /// <summary>A verified binary scanner: Linux x64 only, confined by the container runtime, ready
    /// when the cached binary matches its manifest hash.</summary>
    internal static async Task<string> BinaryDiagnosticAsync(
        ScannerManifestEntry scanner, string workDirectory, bool linuxX64, bool dockerAvailable, CancellationToken ct)
    {
        if (!linuxX64)
            return $"scanner:{scanner.Key}:{scanner.Version}:unavailable:verified binary supports Linux x64 only";
        if (!dockerAvailable)
            return $"scanner:{scanner.Key}:{scanner.Version}:unavailable:container runtime required for binary confinement";
        var path = Path.Combine(workDirectory, "scanners", scanner.Key, scanner.Version, scanner.Key);
        if (!File.Exists(path))
            return $"scanner:{scanner.Key}:{scanner.Version}:available-on-demand:verified binary download required";
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return string.Equals(hash, scanner.Sha256LinuxAmd64, StringComparison.OrdinalIgnoreCase)
            ? $"scanner:{scanner.Key}:{scanner.Version}:ready:verified binary cached"
            : $"scanner:{scanner.Key}:{scanner.Version}:degraded:cached binary hash mismatch; redownload required";
    }

    private static async Task AddTrivyDatabaseDiagnosticAsync(
        List<string> diagnostics,
        string workDirectory,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var metadataPath = Path.Combine(workDirectory, "scanner-cache", "trivy", "db", "metadata.json");
        if (!File.Exists(metadataPath))
        {
            diagnostics.Add("scanner-db:trivy:not-initialized:no vulnerability database cached");
            return;
        }
        try
        {
            var info = new FileInfo(metadataPath);
            if (info.Length is <= 0 or > 65_536)
            {
                diagnostics.Add("scanner-db:trivy:degraded:database metadata size is invalid");
                return;
            }
            await using var stream = File.OpenRead(metadataPath);
            using var document = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 8 }, ct).ConfigureAwait(false);
            var updatedAt = ReadTimestamp(document.RootElement, "UpdatedAt")
                ?? ReadTimestamp(document.RootElement, "DownloadedAt");
            if (!updatedAt.HasValue)
            {
                diagnostics.Add("scanner-db:trivy:degraded:database metadata has no update timestamp");
                return;
            }
            var age = timeProvider.GetUtcNow() - updatedAt.Value;
            diagnostics.Add(age > TimeSpan.FromHours(48)
                ? $"scanner-db:trivy:degraded:database is {Math.Floor(age.TotalHours)} hours old"
                : $"scanner-db:trivy:ready:updated={updatedAt:O}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add("scanner-db:trivy:degraded:database metadata is unreadable");
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String)
                continue;
            return DateTimeOffset.TryParse(property.Value.GetString(), out var timestamp)
                ? timestamp.ToUniversalTime()
                : null;
        }
        return null;
    }
}
