// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.CodeAnalysis.CSharp;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Each architecture publication phase reports its own duration as a run metric.</summary>
public sealed class ArchitectureAnalysisPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"architecture-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task PublishAsync_ReportsTheDurationOfEveryPhase()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));
        var path = Path.Combine(_root, "src", "App", "Service.cs");
        const string code = "namespace App.Services; public sealed class Service { }";
        var api = Substitute.For<IServerApiClient>();
        api.UploadArtifactAsync(1, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 5 });
        api.PublishAnalysisReportAsync(1, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Id = 9 });
        var output = new List<string>();

        var result = await ArchitectureAnalysisPublisher.PublishAsync(
            api, TimeProvider.System, 1, "Quality", _root,
            [new ParsedCSharpSource(path, code, CSharpSyntaxTree.ParseText(code, path: path, cancellationToken: TestContext.Current.CancellationToken))],
            (line, _) => { output.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        foreach (var phase in new[] { "analyze", "metrics_artifact", "metrics_report", "findings" })
            Assert.Single(output, line => line.StartsWith(
                $"##aetheus[pipelinemetric key=architecture.publish.{phase};type=Duration;unit=s]", StringComparison.Ordinal));
    }
}
