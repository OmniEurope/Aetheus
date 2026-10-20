// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The SAST scan loads every in-house OpenGrep rule file, not only the first one ever written (PLAN-002
/// lot 8, B3: six of the seven files ran in no pipeline). The embedded list is compared to the rule
/// directory of the repository, so a new rule file that is not embedded fails here.
/// </summary>
public sealed class OpenGrepEmbeddedRulesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aetheus-opengrep-rules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string RuleDirectory => Path.Combine(RepositoryScan.Root, ".aetheus", "security-rules", "opengrep");

    [Fact]
    public void EveryRuleFileOfTheRepository_IsEmbedded()
    {
        var repository = Directory.EnumerateFiles(RuleDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(repository, OpenGrepSandbox.RuleFiles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Extraction_WritesEveryRuleFileWithItsRepositoryContent()
    {
        await OpenGrepSandbox.ExtractEmbeddedRulesAsync(_directory, TestContext.Current.CancellationToken);

        var extracted = Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(OpenGrepSandbox.RuleFiles.Order(StringComparer.Ordinal), extracted);
        foreach (var file in OpenGrepSandbox.RuleFiles)
        {
            var expected = (await File.ReadAllTextAsync(Path.Combine(RuleDirectory, file), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
            var actual = (await File.ReadAllTextAsync(Path.Combine(_directory, file), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Extraction_RefusesToOverwriteARuleFileAlreadyThere()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "aetheus-security.yml"), "planted", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() =>
            OpenGrepSandbox.ExtractEmbeddedRulesAsync(_directory, TestContext.Current.CancellationToken));
    }
}
