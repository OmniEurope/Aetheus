// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Sdk;
using Xunit.v3;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Opt-in, end to end: the published <c>aetheus-fn</c> executables run
/// <c>detect-project-languages</c> inside a real, hardened step container (the agent's own
/// <see cref="ContainerRunExecutor"/>, read-only mount, <c>/w</c> workspace), once in a musl image and
/// once in a glibc image.
///
/// It needs a Docker daemon and the two published executables, so it is discovered only when
/// <c>AETHEUS_FN_DIRECTORY</c> names a directory holding <c>linux-x64/aetheus-fn</c> and
/// <c>linux-musl-x64/aetheus-fn</c> (see the publish commands in Aetheus.Agent.Functions.Cli.csproj).
/// Otherwise it is absent from the results, never reported as skipped.
/// </summary>
public sealed class AetheusFnDockerTests
{
    // The image the quality pipeline's container stages use (musl, ships libstdc++).
    private const string MuslImage =
        "node:24.4.1-alpine@sha256:820e86612c21d0636580206d802a726f2595366e1b867e564cbc652024151e8a";

    private const string GlibcImage =
        "debian:bookworm-slim@sha256:7c7b2c966bc9ee8cedfeef67e0e279108992c77681fa595db4a9d65c06ccc587";

    [AetheusFnDockerTheory]
    [InlineData(MuslImage)]
    [InlineData(GlibcImage)]
    public async Task DetectProjectLanguagesRunsInsideTheStepContainer(string image)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), $"aetheus-fn-docker-{Guid.NewGuid():N}");
        const int workspaceKey = 4242;
        var workspace = Path.Combine(workDirectory, "cw", workspaceKey.ToString());
        Directory.CreateDirectory(Path.Combine(workspace, "src", "App"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "App", "App.csproj"), "<Project />", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace, "web"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "web", "main.ts"), "export {};", TestContext.Current.CancellationToken);
        var log = new List<(string Line, TaskLogLevel Level)>();
        try
        {
            var options = Options.Create(new AetheusAgentOptions
            {
                WorkDirectory = workDirectory,
                FunctionsDirectory = AetheusFnDockerTheoryAttribute.FunctionsDirectory!
            });
            var container = new ContainerRunExecutor(
                options, NullLogger<ContainerRunExecutor>.Instance, new ToolchainResolver(), TimeProvider.System);
            var executor = new ContainerPipelineFunctionExecutor(PipelineFunctions.CreateAll(), container, options);

            var result = await executor.ExecuteAsync(
                new ContainerSpec { Image = image, Shell = "sh", Network = "none", WorkspaceKey = workspaceKey },
                OperationKind.PipelineFunction,
                "detect-project-languages",
                new Dictionary<string, string>
                {
                    ["WORKSPACE"] = "/w",
                    [PipelineFunctionVariables.ParametersJson] = "{}"
                },
                120,
                (line, level) =>
                {
                    lock (log) log.Add((line, level));
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);

            var lines = log.Select(entry => entry.Line).ToList();
            Assert.True(result.ExitCode == 0, $"exit {result.ExitCode} ({result.FailureCode}): {string.Join(Environment.NewLine, lines)}");
            Assert.Contains("##aetheus[setvariable name=HAS_DOTNET]true", lines);
            Assert.Contains("##aetheus[setvariable name=HAS_JAVASCRIPT]true", lines);
            Assert.Contains("##aetheus[setvariable name=HAS_PYTHON]false", lines);
            Assert.Contains("##aetheus[setvariable name=HAS_JAVA]false", lines);
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }
}

/// <summary>A theory discovered only when <c>AETHEUS_FN_DIRECTORY</c> holds both published
/// <c>aetheus-fn</c> executables (see <see cref="AetheusFnDockerTests"/>).</summary>
[XunitTestCaseDiscoverer(typeof(AetheusFnDockerTheoryDiscoverer))]
public sealed class AetheusFnDockerTheoryAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : TheoryAttribute(sourceFilePath, sourceLineNumber)
{
    public const string Variable = "AETHEUS_FN_DIRECTORY";

    public static string? FunctionsDirectory => Environment.GetEnvironmentVariable(Variable);

    public static bool IsEnabled =>
        FunctionsDirectory is { Length: > 0 } directory
        && ContainerPipelineFunctionExecutor.Executables.All(path => File.Exists(Path.Combine(directory, path)));
}

public sealed class AetheusFnDockerTheoryDiscoverer : TheoryDiscoverer
{
    public override ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, IXunitTestMethod testMethod, IFactAttribute factAttribute) =>
        AetheusFnDockerTheoryAttribute.IsEnabled
            ? base.Discover(discoveryOptions, testMethod, factAttribute)
            : new([]);
}
