// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Log diagnostics only when they change, with collection delays at Information and
/// unavailable configured capabilities at Warning.</summary>
public sealed class CapabilityDiagnosticsRecorderTests
{
    private const string UnreadableDropIn =
        "sudoers drop-in aetheus-deploy present but unreadable (agent lacks read ACL) - its capability shows OFF despite the grant";

    private static readonly string Quarantined = HeartbeatCollectionDiagnostics.Quarantined(["storage diagnostics"]);

    private readonly Server _server = new() { Id = 4, Name = "build-1", Hostname = "build-1" };
    private readonly RecordingLogger<CapabilityDiagnosticsRecorderTests> _logger = new();

    [Fact]
    public void SameDiagnosticsOnTheNextBeats_AreLoggedOnce()
    {
        CapabilityDiagnosticsRecorder.Apply(_server, [UnreadableDropIn], _logger);
        CapabilityDiagnosticsRecorder.Apply(_server, [UnreadableDropIn], _logger);
        CapabilityDiagnosticsRecorder.Apply(_server, [UnreadableDropIn], _logger);

        var (level, message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal($"Server 4 capability diagnostics: {UnreadableDropIn}", message);
        Assert.Contains("aetheus-deploy", _server.CapabilityDiagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionDiagnostics_AreInformation_AndDoNotRepeatTheCapabilityWarning()
    {
        CapabilityDiagnosticsRecorder.Apply(_server, [UnreadableDropIn], _logger);
        CapabilityDiagnosticsRecorder.Apply(
            _server, [UnreadableDropIn, HeartbeatCollectionDiagnostics.CollectionTimedOut, Quarantined], _logger);
        CapabilityDiagnosticsRecorder.Apply(
            _server, [UnreadableDropIn, HeartbeatCollectionDiagnostics.CollectionTimedOut, Quarantined], _logger);

        Assert.Equal(
            [
                (LogLevel.Warning, $"Server 4 capability diagnostics: {UnreadableDropIn}"),
                (LogLevel.Information,
                    $"Server 4 heartbeat collection diagnostics: {HeartbeatCollectionDiagnostics.CollectionTimedOut}; {Quarantined}")
            ],
            _logger.Entries);
    }

    [Fact]
    public void ClearedDiagnostics_AreLoggedOnceAndRemovedFromTheServer()
    {
        CapabilityDiagnosticsRecorder.Apply(_server, [UnreadableDropIn], _logger);
        CapabilityDiagnosticsRecorder.Apply(_server, [], _logger);
        CapabilityDiagnosticsRecorder.Apply(_server, [], _logger);

        Assert.Null(_server.CapabilityDiagnosticsJson);
        Assert.Equal((LogLevel.Information, "Server 4 capability diagnostics cleared"), _logger.Entries[^1]);
        Assert.Equal(2, _logger.Entries.Count);
    }

    [Fact]
    public void AStoredDiagnostic_IsNotLoggedAgainAfterARestart()
    {
        CapabilityDiagnosticsRecorder.Apply(_server, [Quarantined], _logger);
        var restarted = new RecordingLogger<CapabilityDiagnosticsRecorderTests>();

        CapabilityDiagnosticsRecorder.Apply(_server, [Quarantined], restarted);

        Assert.Equal(LogLevel.Information, Assert.Single(_logger.Entries).Level);
        Assert.Empty(restarted.Entries);
    }

    [Theory]
    [InlineData("Heartbeat inventory collection timed out; cached or partial data reported", true)]
    [InlineData("Heartbeat collectors quarantined: docker, apache", true)]
    [InlineData("passwordless sudo unavailable (sudo -n -l failed) - elevated capabilities cannot execute even where a grant is installed", false)]
    [InlineData("deployment grant is installed but its versioned functional probe failed; deployment.apply is disabled", false)]
    public void OnlyTheAgentsCollectionNotices_AreCollectionDiagnostics(string diagnostic, bool expected) =>
        Assert.Equal(expected, HeartbeatCollectionDiagnostics.IsCollectionDiagnostic(diagnostic));
}
