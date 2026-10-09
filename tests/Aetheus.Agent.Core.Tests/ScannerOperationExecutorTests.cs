// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ScannerOperationExecutorTests
{
    [Fact]
    public void ResolveArguments_GitleaksHistoryRequiresImmutableFullShaRange()
    {
        var arguments = new[] { "--log-opts", "{gitLogRange}" };
        var valid = new Dictionary<string, string>
        {
            ["AETHEUS_GITLEAKS_LOG_RANGE"] = $"{new string('a', 40)}..{new string('b', 40)}"
        };

        Assert.Equal(
            valid["AETHEUS_GITLEAKS_LOG_RANGE"],
            ScannerOperationValidator.ResolveArguments(arguments, valid, "/src", "/out/report", "/rules", "/out")[1]);
        Assert.Throws<IOException>(() => ScannerOperationValidator.ResolveArguments(
            arguments,
            new Dictionary<string, string> { ["AETHEUS_GITLEAKS_LOG_RANGE"] = "v0.1.0..HEAD" },
            "/src", "/out/report", "/rules", "/out"));
    }

    [Theory]
    [InlineData("Sarif", "{}")]
    [InlineData("CycloneDxJson", "{\"bomFormat\":\"not-cyclonedx\"}")]
    [InlineData("NativeJson", "{")]
    public async Task ValidateReportStructure_RejectsMalformedOrMismatchedContent(string format, string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var scanner = new ScannerManifestEntry { ReportFormat = format, MaxReportBytes = 1024 };

            var valid = await ScannerOperationExecutor.ValidateReportStructureAsync(
                scanner, path, TestContext.Current.CancellationToken);

            Assert.False(valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnknownScanner_FailsWithoutPublication()
    {
        var context = CreateContext();
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner,
                "not-in-manifest",
                ValidEnvironment(context.SourceDirectory),
                60,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Api.DidNotReceive().PublishAnalysisReportAsync(
                Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), TestContext.Current.CancellationToken);
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_MissingReport_PublishesErrorAndCleansWorkspace()
    {
        var context = CreateContext(new ExecutorResult(0, false));
        ProcessStartInfo? scannerProcess = null;
        context.Runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                scannerProcess = call.Arg<ProcessStartInfo>();
                return new ExecutorResult(0, false);
            });
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new AnalysisReportDto { Status = call.Arg<PublishAnalysisReportRequest>().Status, GateStatus = AnalysisGateStatus.Error });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner,
                "gitleaks",
                ValidEnvironment(context.SourceDirectory),
                60,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.NotNull(scannerProcess);
            Assert.Contains(
                "/tmp:rw,noexec,nosuid,nodev,size=268435456,mode=1777",
                scannerProcess.ArgumentList);
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.Error
                    && request.ErrorMessage!.Contains("without producing", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutPublishesTimedOutAndCleansWorkspace()
    {
        var context = CreateContext(new ExecutorResult(-1, true));
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.TimedOut, GateStatus = AnalysisGateStatus.Error });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "gitleaks", ValidEnvironment(context.SourceDirectory), 1,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.TimedOut),
                Arg.Any<CancellationToken>());
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_AdmissionWaitIsIncludedInTimeout()
    {
        var scanner = ScannerManifestCatalog.Find("trivy-image")!;
        await using var occupiedBudget = await ScannerResourceBudget.Shared.AcquireAsync(
            scanner, TestContext.Current.CancellationToken);
        var context = CreateContext();
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.TimedOut, GateStatus = AnalysisGateStatus.Error });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner,
                "trivy-image",
                ValidEnvironment(context.SourceDirectory),
                1,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.TimedOut
                    && request.ErrorMessage!.Contains("admission", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_ReportQuotaExceededFailsClosedAndCleansWorkspace()
    {
        var context = CreateContext();
        context.Runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var process = call.Arg<ProcessStartInfo>();
                var outputMount = process.ArgumentList.First(argument => argument.Contains("dst=/out", StringComparison.Ordinal));
                var output = outputMount.Split(',').Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                using var report = new FileStream(Path.Combine(output, "report.sarif"), FileMode.CreateNew, FileAccess.Write);
                report.SetLength(104_857_601);
                return new ExecutorResult(0, false);
            });
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.Error, GateStatus = AnalysisGateStatus.Error });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "gitleaks", ValidEnvironment(context.SourceDirectory), 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.Error
                    && request.ErrorMessage!.Contains("quota", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_UntrustedDastTarget_IsRejectedBeforeProcessStart()
    {
        var context = CreateContext();
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Error });
        var env = ValidEnvironment(context.SourceDirectory);
        env["AETHEUS_SCANNER_TARGET_URL"] = "https://qa.example.test";
        env["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral";
        env["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "qa.example.test";
        env["AETHEUS_SCANNER_ACTIVE"] = "false";
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "zap-passive", env, 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(),
                TestContext.Current.CancellationToken);
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.ErrorMessage!.Contains("trust", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Theory]
    [InlineData("qa")]
    [InlineData("pre-release")]
    public void ValidateDastTarget_NonEphemeralClassification_IsRejected(string classification)
    {
        var scanner = ScannerManifestCatalog.Find("zap-passive");
        Assert.NotNull(scanner);
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_SCANNER_TARGET_URL"] = "https://qa.example.test",
            ["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = classification,
            ["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true",
            ["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "qa.example.test",
            ["AETHEUS_SCANNER_ACTIVE"] = "false"
        };

        var valid = ScannerOperationValidator.ValidateDastTarget(
            scanner,
            env,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)),
            out var error);

        Assert.False(valid);
        Assert.Contains("explicitly classified as ephemeral", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateDastTarget_LoopbackHttpOpenApi_IsAccepted()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var scanner = ScannerManifestCatalog.Find("zap-api");
        Assert.NotNull(scanner);
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_SCANNER_TARGET_URL"] = "http://127.0.0.1:10081",
            ["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral",
            ["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true",
            ["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "127.0.0.1",
            ["AETHEUS_SCANNER_ACTIVE"] = "false",
            ["AETHEUS_SCANNER_API_SPECIFICATION_URL"] = "http://127.0.0.1:10082/openapi/v1.json",
            ["AETHEUS_SCANNER_API_SPECIFICATION_FORMAT"] = "openapi",
            ["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = new string('a', 64),
            ["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = now.AddMinutes(5).ToString("O")
        };

        Assert.True(ScannerOperationValidator.ValidateDastTarget(
            scanner,
            env,
            new FakeTimeProvider(now),
            out var error), error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1445, true)]
    [InlineData(1446, false)]
    public void ValidateDastTarget_LeaseBoundariesUseInjectedClock(
        int expiresInMinutes,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var scanner = ScannerManifestCatalog.Find("zap-passive");
        Assert.NotNull(scanner);
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_SCANNER_TARGET_URL"] = "https://qa.example.test",
            ["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral",
            ["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true",
            ["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "qa.example.test",
            ["AETHEUS_SCANNER_ACTIVE"] = "false",
            ["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = new string('a', 64),
            ["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = now.AddMinutes(expiresInMinutes).ToString("O")
        };

        var actual = ScannerOperationValidator.ValidateDastTarget(
            scanner,
            env,
            new FakeTimeProvider(now),
            out _);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExecuteAsync_ValidZapActive_UsesBoundedSeededHomeAndDedicatedWorkDirectory()
    {
        var context = CreateContext();
        string[]? scanArguments = null;
        string? trustedHarnessContent = null;
        var hostileHarness = Path.Combine(
            context.SourceDirectory, "deploy", "scripts", "zap-active-automation.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(hostileHarness)!);
        File.WriteAllText(hostileHarness, "echo HOSTILE_WORKSPACE_HARNESS");
        context.Runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var process = call.Arg<ProcessStartInfo>();
                if (process.ArgumentList.Contains("inspect"))
                    await call.Arg<Func<string, TaskLogLevel, Task>>()("127.0.0.1", TaskLogLevel.Info);
                if (!process.ArgumentList.Contains("run")) return new ExecutorResult(0, false);
                scanArguments = [.. process.ArgumentList];
                var harnessMount = process.ArgumentList.First(argument =>
                    argument.Contains("dst=/aetheus/harness/zap-active-automation.sh", StringComparison.Ordinal));
                var harnessSource = harnessMount.Split(',')
                    .Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                trustedHarnessContent = File.ReadAllText(harnessSource);
                var outputMount = process.ArgumentList.First(argument =>
                    argument.Contains("dst=/zap/wrk", StringComparison.Ordinal));
                var output = outputMount.Split(',')
                    .Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                File.WriteAllText(Path.Combine(output, "report.json"), "{}");
                return new ExecutorResult(0, false);
            });
        context.Api.UploadArtifactAsync(42,
                Arg.Is<string>(name => name.StartsWith("analysis-zap-active-", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 92 });
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
        var env = ValidEnvironment(context.SourceDirectory);
        env["AETHEUS_SCANNER_TARGET_URL"] = "https://127.0.0.1";
        env["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral";
        env["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true";
        env["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "127.0.0.1";
        env["AETHEUS_SCANNER_ACTIVE"] = "true";
        env["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = new string('a', 64);
        env["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = DateTime.UtcNow.AddHours(1).ToString("O");
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "zap-active", env, 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(scanArguments);
            Assert.Contains("/home/zap/runtime:rw,exec,nosuid,nodev,size=1073741824,mode=1777", scanArguments);
            Assert.Contains("HOME=/home/zap/runtime", scanArguments);
            Assert.Contains(
                "_JAVA_OPTIONS=-Duser.home=/home/zap/runtime -Xmx768m -XX:ActiveProcessorCount=1",
                scanArguments);
            Assert.Contains("/zap/wrk", scanArguments);
            Assert.Contains(scanArguments, argument => argument.Contains("dst=/zap/wrk", StringComparison.Ordinal));
            Assert.Contains("/aetheus/harness/zap-active-automation.sh", scanArguments);
            Assert.Contains(scanArguments, argument =>
                argument.Contains("dst=/aetheus/harness/zap-active-automation.sh", StringComparison.Ordinal)
                && argument.EndsWith(",readonly", StringComparison.Ordinal));
            Assert.NotNull(trustedHarnessContent);
            Assert.DoesNotContain("HOSTILE_WORKSPACE_HARNESS", trustedHarnessContent);
            Assert.Contains("aetheus-active-plan.yaml", trustedHarnessContent, StringComparison.Ordinal);
            Assert.Contains("https://127.0.0.1", scanArguments);
            Assert.Contains("aetheus", scanArguments);
            Assert.Contains(scanArguments, argument =>
                argument.Length == 64 && argument.All(Uri.IsHexDigit));
            Assert.DoesNotContain(scanArguments, argument =>
                argument.StartsWith("__AETHEUS_PROXY_", StringComparison.Ordinal));
            Assert.Contains("/bin/sh", scanArguments);
            Assert.Contains("/home/zap/.ZAP", scanArguments);
            Assert.Contains("/home/zap/runtime/.ZAP", scanArguments);
            Assert.DoesNotContain(scanArguments, argument => argument.Contains("/home/zap:rw", StringComparison.Ordinal));
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally { context.Dispose(); }
    }

    /// <summary>
    /// Recette R2-099: the eight OpenAPI partitions are one step. The agent scans each definition in
    /// order, each with its share of the step's timeout, and publishes ONE report holding every
    /// partition's sites, with one raw artifact.
    /// </summary>
    [Fact]
    public async Task R2099_ZapApiWithSeveralDefinitions_ScansThemInOrder_AndPublishesOneMergedReport()
    {
        var context = CreateContext();
        var scanned = new List<(string Specification, int TimeoutSeconds)>();
        context.Runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var process = call.Arg<ProcessStartInfo>();
                if (process.ArgumentList.Contains("inspect"))
                    await call.Arg<Func<string, TaskLogLevel, Task>>()("127.0.0.1", TaskLogLevel.Info);
                if (!process.ArgumentList.Contains("run")) return new ExecutorResult(0, false);
                var arguments = process.ArgumentList.ToList();
                var specification = arguments[arguments.IndexOf("-t") + 1];
                scanned.Add((specification, call.ArgAt<int>(1)));
                var outputMount = arguments.First(argument => argument.Contains("dst=/zap/wrk", StringComparison.Ordinal));
                var output = outputMount.Split(',').Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                var partition = specification[^6];
                File.WriteAllText(Path.Combine(output, "report.json"),
                    "{\"@programName\":\"ZAP\",\"site\":[{\"@name\":\"p" + partition + "\",\"alerts\":[{\"pluginid\":\"1\"},{\"pluginid\":\"2\"}]}]}");
                return new ExecutorResult(partition == '1' ? 2 : 0, false);
            });
        context.Api.UploadArtifactAsync(42, Arg.Is<string>(name => name.StartsWith("analysis-zap-api-", StringComparison.Ordinal)),
                Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 93 });
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
        var env = ValidEnvironment(context.SourceDirectory);
        env["AETHEUS_SCANNER_TARGET_URL"] = "http://127.0.0.1:10081";
        env["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral";
        env["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true";
        env["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "127.0.0.1";
        env["AETHEUS_SCANNER_ACTIVE"] = "false";
        env["AETHEUS_SCANNER_API_SPECIFICATION_URL"] = "";
        env["AETHEUS_SCANNER_API_SPECIFICATION_URLS"] =
            "http://127.0.0.1:10081/openapi/dast-0.json\nhttp://127.0.0.1:10081/openapi/dast-1.json\nhttp://127.0.0.1:10081/openapi/dast-2.json";
        env["AETHEUS_SCANNER_API_SPECIFICATION_FORMAT"] = "openapi";
        env["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = new string('a', 64);
        env["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = DateTime.UtcNow.AddHours(1).ToString("O");
        var log = new List<string>();
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "zap-api", env, 60,
                (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                ["http://127.0.0.1:10081/openapi/dast-0.json", "http://127.0.0.1:10081/openapi/dast-1.json", "http://127.0.0.1:10081/openapi/dast-2.json"],
                scanned.Select(scan => scan.Specification));
            // Each partition keeps its own bound: the step's 60 s shared by three.
            Assert.All(scanned, scan => Assert.InRange(scan.TimeoutSeconds, 1, 20));
            await context.Api.Received(1).UploadArtifactAsync(42, Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Stream>(), Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.ScannerKey == "zap-api"
                    && request.Status == AnalysisReportStatus.Failed
                    && request.ReportContent!.Contains("\"p0\"", StringComparison.Ordinal)
                    && request.ReportContent.Contains("\"p1\"", StringComparison.Ordinal)
                    && request.ReportContent.Contains("\"p2\"", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
            Assert.Contains(log, line => line.StartsWith("OpenAPI partition 3/3: scanning http://127.0.0.1:10081/openapi/dast-2.json", StringComparison.Ordinal));
            Assert.Contains("One report for the 3 partitions, alerts per partition: 1=2, 2=2, 3=2.", log);
        }
        finally { context.Dispose(); }
    }

    /// <summary>R2-099: every definition of the list passes the host check, not only the first.</summary>
    [Fact]
    public void R2099_ADefinitionOnAnotherHost_RefusesTheWholeList()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var env = new Dictionary<string, string>
        {
            ["AETHEUS_SCANNER_TARGET_URL"] = "http://127.0.0.1:10081",
            ["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = "ephemeral",
            ["AETHEUS_SCANNER_TARGET_TRUSTED"] = "true",
            ["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = "127.0.0.1",
            ["AETHEUS_SCANNER_ACTIVE"] = "false",
            ["AETHEUS_SCANNER_API_SPECIFICATION_URLS"] = "http://127.0.0.1:10081/openapi/dast-0.json\nhttps://elsewhere.example/openapi.json",
            ["AETHEUS_SCANNER_API_SPECIFICATION_FORMAT"] = "openapi",
            ["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = new string('a', 64),
            ["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = now.AddMinutes(5).ToString("O")
        };

        Assert.False(ScannerOperationValidator.ValidateDastTarget(
            ScannerManifestCatalog.Find("zap-api")!, env, new FakeTimeProvider(now), out var error));
        Assert.StartsWith("DAST API refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRawArtifactName_DastStepsReceiveDistinctStableNames()
    {
        var scanner = ScannerManifestCatalog.Find("zap-api")!;
        var first = ScannerOperationSupport.BuildRawArtifactName(scanner, new Dictionary<string, string>
        {
            ["AETHEUS_STAGE_NAME"] = "DynamicSecurityApi",
            ["AETHEUS_STEP_NAME"] = "Partition 0"
        });
        var second = ScannerOperationSupport.BuildRawArtifactName(scanner, new Dictionary<string, string>
        {
            ["AETHEUS_STAGE_NAME"] = "DynamicSecurityApi",
            ["AETHEUS_STEP_NAME"] = "Partition 1"
        });

        Assert.StartsWith("analysis-zap-api-", first, StringComparison.Ordinal);
        Assert.StartsWith("analysis-zap-api-", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(first, ScannerOperationSupport.BuildRawArtifactName(scanner, new Dictionary<string, string>
        {
            ["AETHEUS_STAGE_NAME"] = "DynamicSecurityApi",
            ["AETHEUS_STEP_NAME"] = "Partition 0"
        }));
    }

    [Theory]
    [InlineData("trivy-image", "aetheus-back.tar")]
    [InlineData("syft", "aetheus-back.tar")]
    [InlineData("trivy-image-front", "aetheus-front.tar")]
    [InlineData("syft-front", "aetheus-front.tar")]
    // A project that names its archives after itself reaches the SAME refusal, on provenance. The
    // archive name is no longer what blocks it: before the fix these two rows failed on a missing
    // file while reporting a revision mismatch, a cause that was not the real one.
    [InlineData("trivy-image", "atlas-back.tar")]
    [InlineData("syft-front", "atlas-front.tar")]
    public async Task ExecuteAsync_BuiltArtifactFromAnotherCommit_IsRejectedBeforeProcessStart(
        string scannerKey,
        string archiveName)
    {
        var context = CreateContext();
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Error });
        var image = Directory.CreateDirectory(Path.Combine(context.SourceDirectory, ".analysis-image"));
        await File.WriteAllTextAsync(Path.Combine(image.FullName, "source-commit"), "aaaaaaaa",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(image.FullName, archiveName), [0],
            TestContext.Current.CancellationToken);
        var env = ValidEnvironment(context.SourceDirectory);
        env["BUILD_SOURCEVERSION"] = "bbbbbbbb";
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, scannerKey, env, 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>());
            // The message must name the two commits it compared. "revision does not match" alone was
            // reported for an absent file too, which is what sent the reader hunting a provenance
            // problem that did not exist.
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request =>
                    request.ErrorMessage!.Contains("aaaaaaaa", StringComparison.Ordinal)
                    && request.ErrorMessage.Contains("bbbbbbbb", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Theory]
    [InlineData("trivy-image", "-back.tar")]
    [InlineData("syft-front", "-front.tar")]
    public async Task ExecuteAsync_MissingImageArchive_SaysItIsMissingRatherThanBlamingTheRevision(
        string scannerKey,
        string expectedSuffix)
    {
        var context = CreateContext();
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Error });
        // The provenance marker is right and matches the run; only the archive is absent.
        var image = Directory.CreateDirectory(Path.Combine(context.SourceDirectory, ".analysis-image"));
        await File.WriteAllTextAsync(Path.Combine(image.FullName, "source-commit"), "bbbbbbbb",
            TestContext.Current.CancellationToken);
        var env = ValidEnvironment(context.SourceDirectory);
        env["BUILD_SOURCEVERSION"] = "bbbbbbbb";
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, scannerKey, env, 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request =>
                    request.ErrorMessage!.Contains(expectedSuffix, StringComparison.Ordinal)
                    && !request.ErrorMessage.Contains("revision", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_ValidSarif_UploadsRawArtifactAndPublishesGate()
    {
        var context = CreateContext();
        context.Runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var process = call.Arg<ProcessStartInfo>();
                var outputMount = process.ArgumentList.First(argument => argument.Contains("dst=/out", StringComparison.Ordinal));
                var source = outputMount.Split(',').Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                File.WriteAllText(Path.Combine(source, "report.sarif"), "{\"version\":\"2.1.0\",\"runs\":[]}");
                return new ExecutorResult(0, false);
            });
        context.Api.UploadArtifactAsync(42, "analysis-gitleaks", "Security", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 91 });
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
        var env = ValidEnvironment(context.SourceDirectory);
        env["AETHEUS_STAGE_NAME"] = "Security";
        var output = new List<string>();
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "gitleaks", env, 60,
                (message, _) =>
                {
                    output.Add(message);
                    return Task.CompletedTask;
                }, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await context.Api.Received(1).UploadArtifactAsync(42, "analysis-gitleaks", "Security",
                Arg.Any<Stream>(), Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.PipelineArtifactId == 91
                    && request.Status == AnalysisReportStatus.Passed),
                Arg.Any<CancellationToken>());
            Assert.Contains(output, message => message.StartsWith(
                "##aetheus[pipelinemetric key=scanner.budget.wait;type=Duration;unit=s]",
                StringComparison.Ordinal));
            Assert.Contains(output, message => message.StartsWith(
                "Scanner resource budget admitted gitleaks after ",
                StringComparison.Ordinal));
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_SourceOutsideAgentWorkDirectory_MountsBoundedDaemonVisibleProjection()
    {
        var context = CreateContext();
        var externalRoot = Directory.CreateTempSubdirectory("aetheus-private-tmp-source-").FullName;
        var externalSource = Directory.CreateDirectory(Path.Combine(externalRoot, "source")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(externalSource, "sentinel.txt"),
            "private-tmp-source",
            TestContext.Current.CancellationToken);
        string? mountedSource = null;
        context.Runner.RunAsync(
                Arg.Any<ProcessStartInfo>(),
                Arg.Any<int>(),
                Arg.Any<Func<string, TaskLogLevel, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var process = call.Arg<ProcessStartInfo>();
                var sourceMount = process.ArgumentList.First(argument =>
                    argument.Contains("dst=/src", StringComparison.Ordinal));
                mountedSource = sourceMount.Split(',')
                    .Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                Assert.StartsWith(
                    Path.Combine(context.Root, "scanner-projections"),
                    mountedSource,
                    StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(externalSource, mountedSource);
                Assert.Equal("private-tmp-source", File.ReadAllText(Path.Combine(mountedSource, "sentinel.txt")));

                var outputMount = process.ArgumentList.First(argument =>
                    argument.Contains("dst=/out", StringComparison.Ordinal));
                var output = outputMount.Split(',')
                    .Single(part => part.StartsWith("src=", StringComparison.Ordinal))[4..];
                File.WriteAllText(Path.Combine(output, "report.sarif"), "{\"version\":\"2.1.0\",\"runs\":[]}");
                return new ExecutorResult(0, false);
            });
        context.Api.UploadArtifactAsync(
                42,
                "analysis-gitleaks",
                Arg.Any<string?>(),
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifactDto { Id = 93 });
        context.Api.PublishAnalysisReportAsync(
                42,
                Arg.Any<PublishAnalysisReportRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner,
                "gitleaks",
                ValidEnvironment(externalSource),
                60,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(mountedSource);
            Assert.Empty(Directory.Exists(context.ScansDirectory)
                ? Directory.EnumerateDirectories(context.ScansDirectory)
                : []);
        }
        finally
        {
            Directory.Delete(externalRoot, recursive: true);
            context.Dispose();
        }
    }

    [Fact]
    public void StageSourceTree_PreservesInternalSymlinkWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows())
            return; // Linux is the production scanner platform and exercises the symlink contract in CI.

        var root = Directory.CreateTempSubdirectory("aetheus-scanner-stage-").FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var target = Path.Combine(source, "target.txt");
            File.WriteAllText(target, "target");
            File.CreateSymbolicLink(Path.Combine(source, "link.txt"), "target.txt");
            var destination = Path.Combine(root, "destination");

            ScannerSourceStager.StageSourceTree(source, destination);

            var stagedLink = new FileInfo(Path.Combine(destination, "link.txt"));
            Assert.Equal("target.txt", stagedLink.LinkTarget);
            Assert.Equal("target", File.ReadAllText(stagedLink.FullName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchingLanguage_PublishesNotApplicableWithoutStartingScanner()
    {
        var context = CreateContext();
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.NotApplicable, GateStatus = AnalysisGateStatus.Passed });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "ruff", ValidEnvironment(context.SourceDirectory), 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.NotApplicable),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_AuditArtifactIsNotApplicableProductSourceForRuff()
    {
        var context = CreateContext();
        var auditDirectory = Directory.CreateDirectory(Path.Combine(
            context.SourceDirectory, ".claude", "audit", "artefacts"));
        await File.WriteAllTextAsync(
            Path.Combine(auditDirectory.FullName, "generate.py"),
            "print('historical audit')",
            TestContext.Current.CancellationToken);
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.NotApplicable, GateStatus = AnalysisGateStatus.Passed });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "ruff", ValidEnvironment(context.SourceDirectory), 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.NotApplicable),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    [Fact]
    public async Task ExecuteAsync_ProjectScannerWithoutLockedPackage_PublishesUnavailable()
    {
        var context = CreateContext();
        await File.WriteAllTextAsync(Path.Combine(context.SourceDirectory, "app.ts"), "export {};",
            TestContext.Current.CancellationToken);
        context.Api.PublishAnalysisReportAsync(42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { Status = AnalysisReportStatus.Unavailable, GateStatus = AnalysisGateStatus.Error });
        try
        {
            var result = await context.Executor.ExecuteAsync(
                OperationKind.PipelineRunScanner, "eslint", ValidEnvironment(context.SourceDirectory), 60,
                (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await context.Runner.DidNotReceive().RunAsync(
                Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>());
            await context.Api.Received(1).PublishAnalysisReportAsync(42,
                Arg.Is<PublishAnalysisReportRequest>(request => request.Status == AnalysisReportStatus.Unavailable
                    && request.ErrorMessage!.Contains("immutable dependency lock", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        }
        finally { context.Dispose(); }
    }

    private static Dictionary<string, string> ValidEnvironment(string sourceDirectory) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AETHEUS_RUN_ID"] = "42",
        ["AETHEUS_WORKING_DIR"] = sourceDirectory,
        ["AETHEUS_WORKSPACE_MODE"] = "process"
    };

    private static TestContextData CreateContext(ExecutorResult? fixedResult = null)
    {
        var root = Directory.CreateTempSubdirectory("aetheus-scanner-test-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var api = Substitute.For<IServerApiClient>();
        var runner = Substitute.For<IScannerProcessRunner>();
        if (fixedResult is not null)
            runner.RunAsync(Arg.Any<ProcessStartInfo>(), Arg.Any<int>(), Arg.Any<Func<string, TaskLogLevel, Task>>(), Arg.Any<CancellationToken>())
                .Returns(fixedResult);
        var projectionManager = new ScannerSourceProjectionManager(TimeSpan.Zero);
        var executor = new ScannerOperationExecutor(
            api,
            Substitute.For<IHttpClientFactory>(),
            Options.Create(new AetheusAgentOptions { WorkDirectory = root, MinTimeoutSeconds = 1, MaxTimeoutSeconds = 3600 }),
            runner,
            projectionManager,
            TimeProvider.System,
            NullLogger<ScannerOperationExecutor>.Instance);
        return new TestContextData(root, source, api, runner, executor, projectionManager);
    }

    private sealed record TestContextData(
        string Root,
        string SourceDirectory,
        IServerApiClient Api,
        IScannerProcessRunner Runner,
        ScannerOperationExecutor Executor,
        ScannerSourceProjectionManager ProjectionManager) : IDisposable
    {
        public string ScansDirectory => Path.Combine(Root, "scans");
        public void Dispose()
        {
            Assert.True(SpinWait.SpinUntil(() => ProjectionManager.ActiveProjectionCount == 0,
                TimeSpan.FromSeconds(5)), "Scanner projection cleanup did not complete.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
