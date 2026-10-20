// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

namespace Aetheus.Agent.Core.Operations;

internal static class ScannerOperationValidator
{
    public static bool ValidateDastTarget(
        ScannerManifestEntry scanner,
        IReadOnlyDictionary<string, string> envVars,
        TimeProvider timeProvider,
        out string error)
    {
        error = string.Empty;
        if (!scanner.Key.StartsWith("zap-", StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryValidateDastEndpoint(envVars, out var endpoint, out var endpointError))
            return Fail(endpointError, out error);
        if (!ValidateDastLease(envVars, timeProvider))
            return Fail("DAST refused: scheduler-issued execution lease is absent, invalid or expired.", out error);
        if (!ValidateDastMode(scanner, envVars))
            return Fail("DAST refused: active flag and scanner mode do not match.", out error);
        return !string.Equals(scanner.Key, "zap-api", StringComparison.OrdinalIgnoreCase)
               || TryValidateApiSpecification(envVars, endpoint.Uri, endpoint.AllowedHost, out error);
    }

    /// <summary>Verify the scanner image archive matches the run source commit and return a precise
    /// refusal reason. Missing archives and mismatched provenance are distinct failures.</summary>
    public static async Task<ImageAssociationResult> ValidateImageAssociationAsync(
        ScannerManifestEntry scanner,
        string sourceDirectory,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var role = ScannerImageArchiveResolver.ResolveRole(scanner.Key);
        if (role == ScannerImageRole.None) return ImageAssociationResult.Valid;

        var expected = Value(envVars, "BUILD_SOURCEVERSION") ?? Value(envVars, "AETHEUS_SOURCE_VERSION");
        if (!IsValidRevision(expected))
            return ImageAssociationResult.Refused(
                "the run provides no usable source commit (BUILD_SOURCEVERSION / AETHEUS_SOURCE_VERSION)");

        var imageDirectory = Path.Combine(sourceDirectory, ".analysis-image");
        var archive = ScannerImageArchiveResolver.Resolve(role, imageDirectory, envVars);
        if (archive.Outcome != ImageArchiveOutcome.Resolved)
            return ImageAssociationResult.Refused(archive.Detail ?? "the image archive could not be resolved");

        var markerPath = Path.Combine(imageDirectory, "source-commit");
        if (!File.Exists(markerPath))
            return ImageAssociationResult.Refused(
                "the run's provenance marker .analysis-image/source-commit is missing");

        var marker = (await File.ReadAllTextAsync(markerPath, ct).ConfigureAwait(false)).Trim();
        if (!string.Equals(marker, expected, StringComparison.OrdinalIgnoreCase))
            return ImageAssociationResult.Refused(
                $"the images were built at {marker}, this run is at {expected}");

        var archivePath = Path.Combine(imageDirectory, archive.FileName!);
        var configPath = await FindImageConfigPathAsync(archivePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(configPath) || configPath.Contains("..", StringComparison.Ordinal))
            return ImageAssociationResult.Refused(
                $"'{archive.FileName}' carries no readable image configuration");

        return await ImageConfigMatchesRevisionAsync(archivePath, configPath, expected!, ct).ConfigureAwait(false)
            ? ImageAssociationResult.Valid
            : ImageAssociationResult.Refused(
                $"'{archive.FileName}' does not carry org.opencontainers.image.revision {expected}");
    }

    private static bool TryValidateDastEndpoint(
        IReadOnlyDictionary<string, string> envVars,
        out DastEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        var target = Value(envVars, "AETHEUS_SCANNER_TARGET_URL");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail("DAST refused: target URL is not absolute HTTP(S).", out error);
        var allowedHost = Value(envVars, "AETHEUS_SCANNER_TARGET_ALLOWED_HOST")?.TrimEnd('.');
        var trusted = string.Equals(Value(envVars, "AETHEUS_SCANNER_TARGET_TRUSTED"), "true", StringComparison.OrdinalIgnoreCase);
        if (!trusted || string.IsNullOrWhiteSpace(allowedHost)
            || !string.Equals(uri.Host.TrimEnd('.'), allowedHost, StringComparison.OrdinalIgnoreCase))
            return Fail("DAST refused: backend environment trust or hostname allowlist is absent.", out error);
        if (!string.Equals(Value(envVars, "AETHEUS_SCANNER_TARGET_CLASSIFICATION"), "ephemeral", StringComparison.OrdinalIgnoreCase))
            return Fail("DAST refused: target is not explicitly classified as ephemeral.", out error);
        if (uri.Host.EndsWith("aetheus.sonytumen.com", StringComparison.OrdinalIgnoreCase))
            return Fail("DAST refused: production Aetheus domains are permanently denied.", out error);
        endpoint = new DastEndpoint(uri, allowedHost);
        error = string.Empty;
        return true;
    }

    private static bool ValidateDastLease(
        IReadOnlyDictionary<string, string> envVars,
        TimeProvider timeProvider)
    {
        var token = Value(envVars, "AETHEUS_SCANNER_DAST_LEASE_TOKEN");
        if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
            return false;
        if (!DateTimeOffset.TryParse(
                Value(envVars, "AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expiresAt))
            return false;
        var now = timeProvider.GetUtcNow();
        return expiresAt > now && expiresAt <= now.AddHours(24).AddMinutes(5);
    }

    private static bool ValidateDastMode(
        ScannerManifestEntry scanner,
        IReadOnlyDictionary<string, string> envVars) =>
        string.Equals(Value(envVars, "AETHEUS_SCANNER_ACTIVE"), "true", StringComparison.OrdinalIgnoreCase)
        == string.Equals(scanner.Key, "zap-active", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateApiSpecification(
        IReadOnlyDictionary<string, string> envVars,
        Uri targetUri,
        string allowedHost,
        out string error)
    {
        // Every definition of a partitioned step passes the same check as a single one.
        var specifications = ScannerApiSpecifications.Read(envVars);
        var format = Value(envVars, "AETHEUS_SCANNER_API_SPECIFICATION_FORMAT")?.ToLowerInvariant();
        if (specifications.Count == 0
            || specifications.Any(specification => !Uri.TryCreate(specification, UriKind.Absolute, out var specificationUri)
                || !UsesAllowedApiScheme(specificationUri, targetUri)
                || !string.Equals(specificationUri.Host.TrimEnd('.'), allowedHost, StringComparison.OrdinalIgnoreCase))
            || format is not ("openapi" or "graphql"))
            return Fail("DAST API refused: specification must use HTTPS except on loopback QA, use the allowlisted host and declare openapi or graphql.", out error);
        error = string.Empty;
        return true;
    }

    private static bool UsesAllowedApiScheme(Uri specificationUri, Uri targetUri) =>
        specificationUri.Scheme == Uri.UriSchemeHttps
        || specificationUri.Scheme == Uri.UriSchemeHttp && specificationUri.IsLoopback && targetUri.IsLoopback;

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool IsValidRevision(string? revision) =>
        !string.IsNullOrWhiteSpace(revision)
        && revision.Length is >= 7 and <= 64
        && revision.All(Uri.IsHexDigit);

    internal static async Task<string?> FindImageConfigPathAsync(string archivePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (!string.Equals(entry.Name, "manifest.json", StringComparison.Ordinal) || entry.DataStream is null)
                continue;
            using var manifest = await JsonDocument.ParseAsync(
                entry.DataStream, new JsonDocumentOptions { MaxDepth = 16 }, ct).ConfigureAwait(false);
            if (manifest.RootElement.ValueKind != JsonValueKind.Array || manifest.RootElement.GetArrayLength() != 1)
                return null;
            return manifest.RootElement[0].TryGetProperty("Config", out var config)
                   && config.ValueKind == JsonValueKind.String
                ? config.GetString()
                : null;
        }
        return null;
    }

    internal static async Task<bool> ImageConfigMatchesRevisionAsync(
        string archivePath,
        string configPath,
        string expected,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (!string.Equals(entry.Name, configPath, StringComparison.Ordinal) || entry.DataStream is null)
                continue;
            using var config = await JsonDocument.ParseAsync(
                entry.DataStream, new JsonDocumentOptions { MaxDepth = 32 }, ct).ConfigureAwait(false);
            return TryGetNestedString(config.RootElement, out var revision,
                       "config", "Labels", "org.opencontainers.image.revision")
                   && string.Equals(revision, expected, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private readonly record struct DastEndpoint(Uri Uri, string AllowedHost);

    public static List<string> ResolveArguments(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> envVars,
        string source,
        string report,
        string rules,
        string output)
    {
        var argumentList = arguments.ToList();
        var targetUrl = Value(envVars, "AETHEUS_SCANNER_TARGET_URL") ?? string.Empty;
        var apiSpecUrl = Value(envVars, "AETHEUS_SCANNER_API_SPECIFICATION_URL") ?? string.Empty;
        var apiFormat = Value(envVars, "AETHEUS_SCANNER_API_SPECIFICATION_FORMAT") ?? string.Empty;
        var gitLogRange = Value(envVars, "AETHEUS_GITLEAKS_LOG_RANGE") ?? string.Empty;
        if (argumentList.Any(argument => argument.Contains("{gitLogRange}", StringComparison.Ordinal))
            && !IsImmutableGitLogRange(gitLogRange))
            throw new IOException("Gitleaks history requires an immutable full-SHA range.");
        return argumentList.Select(argument => argument
            .Replace("{source}", source, StringComparison.Ordinal)
            .Replace("{report}", report, StringComparison.Ordinal)
            .Replace("{rules}", rules, StringComparison.Ordinal)
            .Replace("{output}", output, StringComparison.Ordinal)
            .Replace("{apiSpecUrl}", apiSpecUrl, StringComparison.Ordinal)
            .Replace("{apiFormat}", apiFormat, StringComparison.Ordinal)
            .Replace("{gitLogRange}", gitLogRange, StringComparison.Ordinal)
            .Replace("{targetUrl}", targetUrl, StringComparison.Ordinal)).ToList();
    }

    private static bool IsImmutableGitLogRange(string value)
    {
        var revisions = value.Split("..", StringSplitOptions.None);
        return revisions.Length == 2
            && revisions.All(revision => revision.Length is 40 or 64
                && revision.All(Uri.IsHexDigit));
    }

    public static bool HasApplicableSource(ScannerManifestEntry scanner, string sourceDirectory)
    {
        if ((scanner.FileExtensions ?? []).Count == 0) return true;
        var extensions = scanner.FileExtensions!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(sourceDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (ContainsFileWithExtension(directory, extensions)) return true;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IsSkippedSourceDirectory(Path.GetFileName(child)))
                    pending.Push(child);
            }
        }
        return false;
    }

    private static bool ContainsFileWithExtension(string directory, HashSet<string> extensions) =>
        Directory.EnumerateFiles(directory).Any(file => extensions.Contains(Path.GetExtension(file)));

    /// <summary>Tool, build, dependency and analysis output folders never hold the scanned sources.</summary>
    private static readonly HashSet<string> SkippedSourceDirectories = new(StringComparer.Ordinal)
    {
        ".git", ".vs", ".claude", ".Codex", "bin", "obj", "node_modules", ".pipeline-artifacts", ".analysis-image"
    };

    private static bool IsSkippedSourceDirectory(string name) =>
        SkippedSourceDirectories.Contains(name)
        || name.StartsWith(".analysis-", StringComparison.Ordinal)
        || name.StartsWith(".qa-", StringComparison.Ordinal);

    public static string? ResolveScannerVersion(ScannerManifestEntry scanner, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(scanner.VersionPackage)) return scanner.Version;
        var packageJson = Path.Combine(sourceDirectory, "node_modules", scanner.VersionPackage, "package.json");
        if (File.Exists(packageJson))
        {
            using var package = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { MaxDepth = 16 });
            if (package.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString())) return version.GetString();
        }

        var lockPath = Path.Combine(sourceDirectory, "package-lock.json");
        if (!File.Exists(lockPath)) return null;
        using var lockFile = JsonDocument.Parse(File.ReadAllText(lockPath), new JsonDocumentOptions { MaxDepth = 64 });
        if (!lockFile.RootElement.TryGetProperty("packages", out var packages)
            || packages.ValueKind != JsonValueKind.Object
            || !packages.TryGetProperty($"node_modules/{scanner.VersionPackage}", out var packageEntry)
            || !packageEntry.TryGetProperty("version", out var lockedVersion)
            || lockedVersion.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(lockedVersion.GetString())) return null;
        return lockedVersion.GetString();
    }

    public static async Task<bool> ValidateReportStructureAsync(
        ScannerManifestEntry scanner,
        string path,
        CancellationToken ct)
    {
        var format = Enum.Parse<AnalysisReportFormat>(scanner.ReportFormat, ignoreCase: true);
        try
        {
            if (format is AnalysisReportFormat.CycloneDxXml)
                return await IsWellFormedXmlAsync(path, scanner.MaxReportBytes, ct).ConfigureAwait(false);

            await using var jsonStream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(jsonStream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            }, ct).ConfigureAwait(false);
            return HasReportShape(format, document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or XmlException or IOException)
        {
            return false;
        }
    }

    /// <summary>Reads the whole XML document, without DTD, external resolution or more than
    /// <paramref name="maxCharacters"/> characters: a malformed or oversized document throws.</summary>
    internal static async Task<bool> IsWellFormedXmlAsync(string path, long maxCharacters, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters
        };
        await using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false)) ct.ThrowIfCancellationRequested();
        return true;
    }

    /// <summary>Whether a parsed JSON report has the root its format declares.</summary>
    internal static bool HasReportShape(AnalysisReportFormat format, JsonElement root) => format switch
    {
        AnalysisReportFormat.Sarif => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("version", out _)
            && root.TryGetProperty("runs", out var runs)
            && runs.ValueKind == JsonValueKind.Array,
        AnalysisReportFormat.CycloneDxJson => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("bomFormat", out var bom)
            && string.Equals(bom.GetString(), "CycloneDX", StringComparison.OrdinalIgnoreCase),
        AnalysisReportFormat.SpdxJson => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("spdxVersion", out _),
        AnalysisReportFormat.MetricsJson or AnalysisReportFormat.NativeJson =>
            root.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
        _ => false
    };

    public static string? ComputeRuleSetHash(string? rules, ScannerManifestEntry scanner)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(scanner));
        if (!string.IsNullOrWhiteSpace(rules) && Directory.Exists(rules))
        {
            foreach (var file in Directory.EnumerateFiles(rules, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(rules, file).Replace('\\', '/')));
                hash.AppendData(File.ReadAllBytes(file));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static bool TryGetNestedString(JsonElement element, out string? value, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                value = null;
                return false;
            }
        }
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? Value(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) ? value : null;
}
