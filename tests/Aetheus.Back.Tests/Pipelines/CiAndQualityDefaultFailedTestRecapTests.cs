// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-72, same contract as <see cref="QaDefaultFailedTestRecapTests"/> for the two other generic templates
/// that run <c>dotnet test</c> (CI and quality): a failed test is repeated at the end of the step, name and
/// message, each line marked ERROR, and the exit code stays the runner's. The shell is the real one (the
/// template's own default text run by <c>sh -eu -c</c> in a workspace holding a solution); only <c>dotnet</c> is a
/// stand-in that prints what a real runner printed, and, for the quality template, answers the few other
/// commands it calls (tool install, sln list, msbuild) and stands for <c>dotnet-coverage</c> by running the
/// command it is given.
/// </summary>
public sealed class CiAndQualityDefaultFailedTestRecapTests
{
    public static TheoryData<string, string> Templates => new()
    {
        { "generic/application-ci.yaml", "APPLICATION_CI_COMMANDS" },
        { "generic/application-quality.yaml", "APPLICATION_QUALITY_COMMANDS" }
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task FailedMtpTest_IsRepeatedAtTheEnd_WithItsNameAndMessage_AndTheExitCodeIsTheRunners(string template, string variable)
    {
        var run = await RunDefaultAsync(template, variable, QaDefaultFailedTestRecapTests.MtpGlobalJson, QaDefaultFailedTestRecapTests.MtpFailure, exitCode: 2);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Test run summary: Failed!", run.Output, StringComparison.Ordinal);
        Assert.Equal(
            [
                "ERROR The tests that failed (repeated from the run above):",
                "ERROR failed MiniTests.ThisOneFails_WithAName (119ms)",
                "ERROR   Assert.Equal() Failure: Strings differ",
                "ERROR              ↓ (pos 0)",
                "ERROR   Expected: \"expected\"",
                "ERROR   Actual:   \"actual\"",
                "ERROR              ↑ (pos 0)"
            ],
            ErrorLines(run.Errors));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task FailedVsTestTest_IsRepeatedAtTheEnd_WithItsNameAndMessage(string template, string variable)
    {
        var run = await RunDefaultAsync(template, variable, QaDefaultFailedTestRecapTests.NoRunnerGlobalJson, QaDefaultFailedTestRecapTests.VsTestFailure, exitCode: 1);

        Assert.Equal(1, run.ExitCode);
        var recap = ErrorLines(run.Errors);
        Assert.Contains("ERROR   Failed MiniTests.ThisOneFails_WithAName [100 ms]", recap);
        Assert.Contains("ERROR Expected: \"expected\"", recap);
        Assert.DoesNotContain(recap, line => line.Contains("at MiniTests", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task PassingRun_RepeatsNothing_AndSucceeds(string template, string variable)
    {
        var run = await RunDefaultAsync(
            template, variable, QaDefaultFailedTestRecapTests.MtpGlobalJson, "Test run summary: Passed!\n  total: 2\n", exitCode: 0);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(ErrorLines(run.Errors));
    }

    private static string[] ErrorLines(string errors) =>
        [.. errors.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("ERROR ", StringComparison.Ordinal))];

    private static string ToShellPath(string path) =>
        OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    private static async Task<(int ExitCode, string Output, string Errors)> RunDefaultAsync(
        string template, string variable, string globalJson, string testOutput, int exitCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = YamlParsingHelper.Deserializer
            .Deserialize<PipelineYamlDefinition>(DeliveryTemplateCatalog.ReadResource(template))!
            .Variables[variable];
        var root = Directory.CreateTempSubdirectory("ci-quality-recap-").FullName;
        var tools = Directory.CreateTempSubdirectory("ci-quality-recap-tools-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Demo.sln"), "Microsoft Visual Studio Solution File\n", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "global.json"), globalJson, ct);
            Directory.CreateDirectory(Path.Combine(root, "Demo.Tests"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Demo.Tests", "Demo.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- Microsoft.NET.Test.Sdk xunit --></Project>", ct);
            Directory.CreateDirectory(Path.Combine(root, "Demo"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Demo", "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", ct);

            // The stand-ins live outside the workspace (the CI default archives it).
            var fakeDotnet = Path.Combine(tools, "fake-dotnet");
            const string fakeScript = """
                #!/bin/sh
                case "$1" in
                  test) cat "$FAKE_TEST_OUTPUT"; exit "$FAKE_TEST_EXIT" ;;
                  sln) printf 'Project(s)\n----------\nDemo/Demo.csproj\n' ;;
                  msbuild) printf '{"Properties":{"AssemblyName":"Demo"}}\n' ;;
                  tool)
                    while test "$#" -gt 0; do
                      if test "$1" = "--tool-path"; then TOOL_PATH="$2"; fi
                      shift
                    done
                    mkdir -p "$TOOL_PATH"
                    printf '#!/bin/sh\nwhile test "$#" -gt 0 && test "$1" != "$DOTNET"; do shift; done\nexec "$@"\n' > "$TOOL_PATH/dotnet-coverage"
                    chmod +x "$TOOL_PATH/dotnet-coverage" ;;
                  *) exit 0 ;;
                esac
                """;
            await File.WriteAllTextAsync(fakeDotnet, fakeScript.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var outputFile = Path.Combine(tools, "fake-test-output.txt");
            await File.WriteAllTextAsync(outputFile, testOutput.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false), ct);

            var bash = ExecutableLocator.Require(
                "bash", OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/bash");
            var startInfo = new ProcessStartInfo(bash)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-eu");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commands);
            startInfo.Environment["DOTNET"] = ToShellPath(fakeDotnet);
            startInfo.Environment["FAKE_TEST_OUTPUT"] = ToShellPath(outputFile);
            startInfo.Environment["FAKE_TEST_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
            var standardError = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(tools, recursive: true);
        }
    }
}
