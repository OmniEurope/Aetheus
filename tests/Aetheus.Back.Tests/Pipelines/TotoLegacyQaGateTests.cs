// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// DEP-009: the probe of toto-legacy-qa leaves a <c>.qa-failed</c> marker and continues so the
/// teardown still runs; the QAGate stage is what turns that marker into a failed run. Its shell is
/// read from the definition and run the way the Linux agent runs a step (<c>bash -c</c>, no
/// <c>-e</c>), so a gate that prints "passed" after a failed probe is observed, not restated.
/// </summary>
public sealed class TotoLegacyQaGateTests
{
    [Fact]
    public async Task QaGate_FailsWhenTheProbeLeftItsFailureMarker()
    {
        var workspace = Directory.CreateTempSubdirectory("aetheus-legacy-qa-gate-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, ".qa-failed"), string.Empty,
                TestContext.Current.CancellationToken);

            var result = await RunGateAsync(workspace);

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("QA validation passed.", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("the runtime probe did not pass", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task QaGate_PassesWhenTheProbeLeftNoMarker()
    {
        var workspace = Directory.CreateTempSubdirectory("aetheus-legacy-qa-gate-").FullName;
        try
        {
            var result = await RunGateAsync(workspace);

            Assert.True(result.ExitCode == 0, $"Gate failed ({result.ExitCode}): {result.StdErr}");
            Assert.Contains("QA validation passed.", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string QaGateShell()
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(Path.Combine(
                   Aetheus.Back.Tests.Architecture.RepositoryScan.Root, "deploy", "pipelines", "toto-legacy-qa.yaml"))))
        {
            yaml.Load(reader);
        }

        var stages = (YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode)["stages"];
        var gate = stages.Children.Cast<YamlMappingNode>()
            .Single(stage => ((YamlScalarNode)stage["name"]).Value == "QAGate");
        var step = (YamlMappingNode)((YamlSequenceNode)gate["steps"]).Children.Single();
        return ((YamlScalarNode)step["shell"]).Value!;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGateAsync(string workspace)
    {
        var bash = ExecutableLocator.Require(
            "bash",
            OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/bash");
        var startInfo = new ProcessStartInfo(bash)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(QaGateShell());
        startInfo.Environment["WORKSPACE"] = workspace.Replace('\\', '/');
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }
}
