// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Seeding;

/// <summary>
/// The content of the seeded Toto repositories: the branch and tag names of their history and the
/// embedded files each revision is made of. Shared by the project seeding and the Git seeding of the
/// repository, so neither depends on the other.
/// </summary>
internal static class TotoConformanceContent
{
    internal const string BaselineBranch = "conformance/v1";
    internal const string DevelopBranch = "develop";
    internal const string BaselineTag = "toto-v1";
    internal const string CurrentTag = "toto-v2";
    internal const string VulnerableTag = "toto-vulnerable-v1";
    internal const string ResourceRoot = "TotoConformance/";

    internal static IReadOnlyDictionary<string, string> MergeRepositoryFiles(
        string version,
        IReadOnlyDictionary<string, string> pipelineFiles)
    {
        var files = new Dictionary<string, string>(LoadResources($"{ResourceRoot}Templates/common/"), StringComparer.Ordinal);
        foreach (var (path, content) in LoadResources($"{ResourceRoot}Templates/{version}/"))
            files[path] = content;
        foreach (var (path, content) in pipelineFiles)
            files[path] = content;
        return files;
    }

    internal static IReadOnlyDictionary<string, string> MergeVulnerableRepositoryFiles(
        IReadOnlyDictionary<string, string> pipelineFiles)
    {
        var files = new Dictionary<string, string>(
            MergeRepositoryFiles("v2", pipelineFiles),
            StringComparer.Ordinal);
        foreach (var (path, content) in LoadResources($"{ResourceRoot}Templates/vulnerable/"))
            files[path] = ReplaceVulnerableCanaries(content);
        return files;
    }

    private static string ReplaceVulnerableCanaries(string content)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__TOTO_VULNERABLE_GITHUB_TOKEN__"] =
                string.Concat("ghp_", new string('0', 36)),
            ["__TOTO_VULNERABLE_AWS_ACCESS_KEY__"] =
                string.Concat("AK", "IA", new string('0', 16)),
            ["__TOTO_VULNERABLE_SLACK_TOKEN__"] =
                string.Concat("xoxb-", new string('1', 12), "-", new string('2', 12), "-", new string('a', 24)),
            ["__TOTO_VULNERABLE_PRIVATE_KEY_BLOCK__"] =
                string.Join('\n',
                    string.Concat("-----BEGIN ", "RSA PRIVATE KEY-----"),
                    "VGhpcyBpcyBhIGRldGVybWluaXN0aWMgZmFrZSBjYW5hcnksIG5vdCBhIGtleS4=",
                    string.Concat("-----END ", "RSA PRIVATE KEY-----"))
        };
        foreach (var (marker, canary) in replacements)
            content = content.Replace(marker, canary, StringComparison.Ordinal);
        return content;
    }

    internal static IReadOnlyDictionary<string, string> LoadResources(string prefix)
    {
        var assembly = typeof(TotoConformanceContent).Assembly;
        var normalizedPrefix = prefix.Replace('\\', '/');
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawName in assembly.GetManifestResourceNames())
        {
            var name = rawName.Replace('\\', '/');
            if (!name.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                continue;
            var path = name[normalizedPrefix.Length..];
            using var stream = assembly.GetManifestResourceStream(rawName)
                ?? throw new InvalidOperationException($"Embedded Toto resource '{rawName}' is unavailable.");
            using var reader = new StreamReader(stream);
            result[path] = reader.ReadToEnd();
        }
        if (result.Count == 0)
            throw new InvalidOperationException($"No embedded Toto resources were found below '{prefix}'.");
        return result;
    }
}
