// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-72: a failed test must be readable where the user looks. The generic QA default runs the
/// project's tests; the runner prints each failure where it happens, far above its summary, and the run page's
/// failed-step card shows only the lines marked as errors (the exit code, here). The default therefore repeats
/// the failed tests, name and message, at the end of the step, each line marked ERROR. The shell is the real
/// one (the template's own default text run by <c>sh -eu -c</c>, as the QA step runs it); only <c>dotnet</c> is a
/// stand-in that prints what a real runner printed (output captured from <c>dotnet test</c> 10.0.403 on
/// xunit.v3 with Microsoft.Testing.Platform, and on xunit 2.9 with VSTest), so these tests prove the recap on the
/// real shell path, not the runner's own output format on another SDK.
/// </summary>
public sealed class QaDefaultFailedTestRecapTests
{
    internal const string MtpFailure = """
        Running tests from /src/bin/Release/net10.0/Mini.Tests.dll (net10.0|x64)
        failed MiniTests.ThisOneFails_WithAName (119ms)
          from /src/bin/Release/net10.0/Mini.Tests.dll (net10.0|x64)
          Assert.Equal() Failure: Strings differ
                     ↓ (pos 0)
          Expected: "expected"
          Actual:   "actual"
                     ↑ (pos 0)
            at MiniTests.ThisOneFails_WithAName() in /src/T.cs:6
            at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
        /src/bin/Release/net10.0/Mini.Tests.dll (net10.0|x64) failed with 1 error(s) (820ms)
        Exit code: 2
          Error output: ApiBaseUrl 'ftp://api.showcase.example/' is not an absolute http(s) address: the preferences stay in this browser.

        Test run summary: Failed!
          total: 2
          failed: 1
          succeeded: 1
          skipped: 0
          duration: 1s 644ms
        Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
        """;

    internal const string VsTestFailure = """
        Test run for /src/bin/Release/net10.0/MiniVs.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:00.45]     MiniTests.ThisOneFails_WithAName [FAIL]
          Failed MiniTests.ThisOneFails_WithAName [100 ms]
          Error Message:
           Assert.Equal() Failure: Strings differ
                   ↓ (pos 0)
        Expected: "expected"
        Actual:   "actual"
                   ↑ (pos 0)
          Stack Trace:
             at MiniTests.ThisOneFails_WithAName() in /src/T.cs:line 6
           at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

        Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 122 ms - MiniVs.Tests.dll (net10.0)
        """;

    internal const string MtpGlobalJson = """{ "test": { "runner": "Microsoft.Testing.Platform" } }""";
    internal const string NoRunnerGlobalJson = """{ "sdk": { "version": "10.0.100" } }""";

    [Fact]
    public async Task FailedMtpTest_IsRepeatedAtTheEnd_WithItsNameAndMessage_AndTheExitCodeIsTheRunners()
    {
        var run = await RunQaDefaultAsync(MtpGlobalJson, "Microsoft.NET.Test.Sdk xunit.v3", MtpFailure, exitCode: 2);

        Assert.Equal(2, run.ExitCode);
        // The live log still holds the runner's own output, unchanged.
        Assert.Contains("failed MiniTests.ThisOneFails_WithAName (119ms)", run.Output, StringComparison.Ordinal);
        Assert.Contains("Test run summary: Failed!", run.Output, StringComparison.Ordinal);
        var recap = ErrorLines(run.Errors);
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
            recap);
    }

    [Fact]
    public async Task FailedVsTestTest_IsRepeatedAtTheEnd_WithItsNameAndMessage()
    {
        var run = await RunQaDefaultAsync(NoRunnerGlobalJson, "Microsoft.NET.Test.Sdk xunit", VsTestFailure, exitCode: 1);

        Assert.Equal(1, run.ExitCode);
        var recap = ErrorLines(run.Errors);
        Assert.Contains("ERROR   Failed MiniTests.ThisOneFails_WithAName [100 ms]", recap);
        Assert.Contains("ERROR    Assert.Equal() Failure: Strings differ", recap);
        Assert.Contains("ERROR Expected: \"expected\"", recap);
        Assert.Contains("ERROR Actual:   \"actual\"", recap);
        Assert.DoesNotContain(recap, line => line.Contains("at MiniTests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassingRun_RepeatsNothing_AndSucceeds()
    {
        var run = await RunQaDefaultAsync(
            MtpGlobalJson, "Microsoft.NET.Test.Sdk xunit.v3", "Test run summary: Passed!\n  total: 2\n", exitCode: 0);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Test run summary: Passed!", run.Output, StringComparison.Ordinal);
        Assert.Empty(ErrorLines(run.Errors));
    }

    [Fact]
    public async Task MoreThanTwentyFailures_AreCappedWithTheirCount()
    {
        var output = new StringBuilder();
        for (var index = 1; index <= 22; index++)
            output.Append($"failed Suite.Test{index} (1ms)\n  from x.dll (net10.0|x64)\n  boom {index}\n    at Suite.Test{index}()\n");
        output.Append("Test run summary: Failed!\n");

        var run = await RunQaDefaultAsync(MtpGlobalJson, "Microsoft.NET.Test.Sdk xunit.v3", output.ToString(), exitCode: 2);

        var recap = ErrorLines(run.Errors);
        Assert.Contains("ERROR failed Suite.Test20 (1ms)", recap);
        Assert.DoesNotContain("ERROR failed Suite.Test21 (1ms)", recap);
        Assert.Contains("ERROR 2 more failed test(s) are not repeated here: see the run above.", recap);
    }

    private static string[] ErrorLines(string errors) =>
        [.. errors.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("ERROR ", StringComparison.Ordinal))];

    private static string QaDefaultCommands() =>
        YamlParsingHelper.Deserializer
            .Deserialize<PipelineYamlDefinition>(DeliveryTemplateCatalog.ReadResource("generic/application-qa.yaml"))!
            .Variables["APPLICATION_QA_COMMANDS"];

    private static string ToShellPath(string path) =>
        OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    /// <summary>Runs the QA default in a workspace holding a sealed source of one test project, with a
    /// <c>dotnet</c> whose <c>test</c> prints <paramref name="testOutput"/> and exits with
    /// <paramref name="exitCode"/> (restore and build succeed).</summary>
    private static async Task<(int ExitCode, string Output, string Errors)> RunQaDefaultAsync(
        string globalJson, string testProjectContent, string testOutput, int exitCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("qa-recap-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".pipeline-artifacts"));
            await WriteSourceArchiveAsync(Path.Combine(root, ".pipeline-artifacts", "source.tar.gz"), globalJson, testProjectContent, ct);

            var fakeDotnet = Path.Combine(root, "fake-dotnet");
            await File.WriteAllTextAsync(
                fakeDotnet,
                "#!/bin/sh\ncase \"$1\" in\n  test) cat \"$FAKE_TEST_OUTPUT\"; exit \"$FAKE_TEST_EXIT\" ;;\n  *) exit 0 ;;\nesac\n",
                ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var outputFile = Path.Combine(root, "fake-test-output.txt");
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
            // The QA step's own line: sh -eu -c "$APPLICATION_QA_COMMANDS".
            startInfo.ArgumentList.Add("-eu");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(QaDefaultCommands());
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
        }
    }

    private static async Task WriteSourceArchiveAsync(
        string path, string globalJson, string testProjectContent, CancellationToken ct)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, leaveOpen: false);
        foreach (var (name, content) in new[]
                 {
                     ("Demo.sln", "Microsoft Visual Studio Solution File\n"),
                     ("global.json", globalJson),
                     ("Demo.Tests/Demo.Tests.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><!-- {testProjectContent} --></Project>")
                 })
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            await tar.WriteEntryAsync(entry, ct);
        }
    }
}
