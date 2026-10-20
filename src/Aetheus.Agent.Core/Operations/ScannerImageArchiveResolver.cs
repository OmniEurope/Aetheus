// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Which image a built-artifact scanner analyses. Derived from the scanner key.</summary>
internal enum ScannerImageRole
{
    /// <summary>The scanner does not analyse a built image at all.</summary>
    None,
    Backend,
    Frontend
}

/// <summary>Why the archive a built-artifact scanner needs could not be resolved, or was resolved.</summary>
internal enum ImageArchiveOutcome
{
    Resolved,

    /// <summary>No archive matched the expected role. Distinct from a revision mismatch on purpose:
    /// reporting "the revision does not match" for a file that is simply absent sends the reader
    /// hunting a provenance problem that does not exist.</summary>
    NotFound,

    /// <summary>Several archives matched the role, so picking one would be a guess.</summary>
    Ambiguous,

    /// <summary>The pipeline declared an archive name that is not a plain file name.</summary>
    InvalidDeclaredName
}

internal readonly record struct ImageArchiveResolution(
    ImageArchiveOutcome Outcome,
    string? FileName = null,
    string? Detail = null);

/// <summary>Resolves scanner archives from explicit AETHEUS_IMAGE_ARCHIVE_BACK/FRONT names or
/// unambiguous role-suffixed files in .analysis-image. Source-commit and image-revision checks
/// remain the caller's responsibility.</summary>
internal static class ScannerImageArchiveResolver
{
    internal const string BackendArchiveVariable = "AETHEUS_IMAGE_ARCHIVE_BACK";
    internal const string FrontendArchiveVariable = "AETHEUS_IMAGE_ARCHIVE_FRONT";

    private const string BackendSuffix = "-back.tar";
    private const string FrontendSuffix = "-front.tar";

    public static ScannerImageRole ResolveRole(string scannerKey) => scannerKey.ToLowerInvariant() switch
    {
        "trivy-image" or "syft" => ScannerImageRole.Backend,
        "trivy-image-front" or "syft-front" => ScannerImageRole.Frontend,
        _ => ScannerImageRole.None
    };

    public static ImageArchiveResolution Resolve(
        ScannerImageRole role,
        string imageDirectory,
        IReadOnlyDictionary<string, string> envVars)
    {
        var declared = Declared(role, envVars);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            // A declared name is a file name, never a path: it is joined onto the image directory, so
            // accepting a separator or a traversal segment would let a pipeline read outside it.
            if (declared != Path.GetFileName(declared) || declared.Contains("..", StringComparison.Ordinal))
                return new ImageArchiveResolution(
                    ImageArchiveOutcome.InvalidDeclaredName,
                    Detail: $"'{declared}' is not a plain file name");
            return File.Exists(Path.Combine(imageDirectory, declared))
                ? new ImageArchiveResolution(ImageArchiveOutcome.Resolved, declared)
                : new ImageArchiveResolution(
                    ImageArchiveOutcome.NotFound,
                    Detail: $"the declared archive '{declared}' is not in {DirectoryLabel}");
        }

        var suffix = role == ScannerImageRole.Backend ? BackendSuffix : FrontendSuffix;
        if (!Directory.Exists(imageDirectory))
            return new ImageArchiveResolution(
                ImageArchiveOutcome.NotFound, Detail: $"{DirectoryLabel} does not exist");

        var candidates = Directory
            .EnumerateFiles(imageDirectory, "*.tar", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return candidates.Count switch
        {
            1 => new ImageArchiveResolution(ImageArchiveOutcome.Resolved, candidates[0]),
            0 => new ImageArchiveResolution(
                ImageArchiveOutcome.NotFound,
                Detail: $"no '*{suffix}' archive in {DirectoryLabel}"),
            _ => new ImageArchiveResolution(
                ImageArchiveOutcome.Ambiguous,
                Detail: $"several '*{suffix}' archives in {DirectoryLabel}: {string.Join(", ", candidates)}. "
                        + $"Name the one to analyse with {Variable(role)}.")
        };
    }

    private static string? Declared(ScannerImageRole role, IReadOnlyDictionary<string, string> envVars) =>
        envVars.TryGetValue(Variable(role), out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string Variable(ScannerImageRole role) =>
        role == ScannerImageRole.Backend ? BackendArchiveVariable : FrontendArchiveVariable;

    private const string DirectoryLabel = ".analysis-image/";
}
