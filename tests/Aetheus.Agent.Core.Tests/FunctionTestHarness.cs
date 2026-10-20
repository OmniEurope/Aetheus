// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Stands in for the tools a pipeline function uses: each process run is answered by
/// <see cref="OnRun"/> (exit code and stdout lines, streamed like the real runner, which also writes
/// its <c>[process] exit-code</c> line), downloads write <see cref="DownloadContent"/>, and delays are
/// recorded instead of waited.
/// </summary>
internal sealed class FakeFunctionTools : IPipelineFunctionTools
{
    public List<ProcessStartInfo> Runs { get; } = [];
    public List<Uri> Downloads { get; } = [];
    public List<TimeSpan> Delays { get; } = [];
    public byte[] DownloadContent { get; set; } = [];
    public bool IsLinuxX64 { get; set; }
    public Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string[] Stdout)>> OnRun { get; set; } =
        (_, _) => Task.FromResult((127, Array.Empty<string>()));

    public async Task<ExecutorResult> RunAsync(
        ProcessStartInfo process, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        lock (Runs)
            Runs.Add(process);
        var (exitCode, stdout) = await OnRun(process, cancellationToken);
        foreach (var line in stdout)
            await onOutput(line, TaskLogLevel.Info);
        await onOutput($"[process] exit-code={exitCode}", exitCode == 0 ? TaskLogLevel.Info : TaskLogLevel.Error);
        return new ExecutorResult(exitCode, false);
    }

    public async Task DownloadAsync(Uri source, string destination, CancellationToken cancellationToken)
    {
        Downloads.Add(source);
        await File.WriteAllBytesAsync(destination, DownloadContent, cancellationToken);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A temp directory unique to one test, holding the workspace and whatever else the test lays out,
/// and a runner that executes one function through the real <see cref="PipelineFunctionOperationExecutor"/>
/// and returns its result and the published <c>##aetheus[setvariable]</c> values.
/// </summary>
internal sealed class FunctionTestHarness : IDisposable
{
    private const string SetVariablePrefix = "##aetheus[setvariable name=";

    public FunctionTestHarness(string prefix)
    {
        Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"))).FullName;
        Workspace = Directory.CreateDirectory(Path.Combine(Root, "ws")).FullName;
    }

    public string Root { get; }
    public string Workspace { get; }

    /// <summary>The <c>.pipeline-artifacts</c> folder of this test's temp workspace, where functions write their outputs.</summary>
    public string ArtifactsDirectory => Path.Combine(Workspace, ".pipeline-artifacts");

    /// <summary>One file of <see cref="ArtifactsDirectory"/>.</summary>
    public string Artifact(string name) => Path.Combine(ArtifactsDirectory, name);

    public List<(string Line, TaskLogLevel Level)> Log { get; } = [];

    public void Dispose() => Directory.Delete(Root, recursive: true);

    public string Write(string path, string content)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>An executable file (mode 755 outside Windows), as <c>command -v</c> and
    /// <c>[ -x ]</c> recognise one.</summary>
    public string Executable(string path)
    {
        var full = Write(path, "#!/bin/sh\n");
        MakeExecutable(full);
        return full;
    }

    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    public async Task<(ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(
        IPipelineFunction function, IDictionary<string, string> environment, int timeoutSeconds = 30)
    {
        var env = new Dictionary<string, string>(environment) { [PipelineFunctionRequest.WorkspaceVariable] = Workspace };
        var executor = new PipelineFunctionOperationExecutor([function]);
        var result = await executor.ExecuteAsync(
            PipelineFunctionCatalog.Find(function.Name)?.Operation ?? OperationKind.PipelineFunction, function.Name, env,
            timeoutSeconds, Capture,
            TestContext.Current.CancellationToken);
        var variables = Log
            .Select(entry => entry.Line)
            .Where(line => line.StartsWith(SetVariablePrefix, StringComparison.Ordinal))
            .Select(line => line[SetVariablePrefix.Length..].Split(']', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        return (result, variables);
    }

    private Task Capture(string line, TaskLogLevel level)
    {
        lock (Log)
            Log.Add((line, level));
        return Task.CompletedTask;
    }
}
