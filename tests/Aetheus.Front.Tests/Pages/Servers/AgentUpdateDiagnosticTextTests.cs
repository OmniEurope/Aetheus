// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.Architecture;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-113: a failed agent self-update says why in the user's language, never the agent's English code.</summary>
public sealed class AgentUpdateDiagnosticTextTests
{
    private static readonly BunitTestHelper.StubLocalizer L = new();

    [Theory]
    [InlineData(null, "release manifest invalid", "AgentUpdateDiag_ManifestInvalid")]
    [InlineData("agent-update-failed", "download failed", "AgentUpdateDiag_DownloadFailed")]
    [InlineData("agent-update-failed", " capability request refused ", "AgentUpdateDiag_CapabilityRequestRefused")]
    [InlineData("confirmation-timeout", "No confirming heartbeat arrived before the deadline.", "AgentUpdateDiag_ConfirmationTimeout")]
    [InlineData("update-task-timeout", "The update task did not reach handoff before its execution deadline.", "AgentUpdateDiag_TaskTimeout")]
    public void KnownDiagnostic_IsSaidByItsResource(string? code, string diagnostic, string expected)
    {
        Assert.Equal(expected, AgentUpdateDiagnosticText.Localize(code, diagnostic, L));
    }

    [Theory]
    [InlineData("wrong-version", "Expected 1.5.0, received 1.2.0.", "AgentUpdateDiag_WrongVersion|1.5.0|1.2.0")]
    [InlineData("wrong-version", "Expected 1.5.0, received missing.", "AgentUpdateDiag_WrongVersion|1.5.0|missing")]
    [InlineData("unsupported-protocol", "Received unsupported protocol 7.", "AgentUpdateDiag_UnsupportedProtocol|7")]
    [InlineData("missing-capabilities", "Missing expected capabilities: docker.manage, ports.observe.", "AgentUpdateDiag_MissingCapabilities|docker.manage, ports.observe")]
    public void CodedServerFailure_KeepsItsValues(string code, string diagnostic, string expected)
    {
        var parts = expected.Split('|');
        var localizer = new FormatRecordingLocalizer();
        Assert.Equal(string.Join('|', parts), AgentUpdateDiagnosticText.Localize(code, diagnostic, localizer));
    }

    [Theory]
    [InlineData(null, "Connection refused (host.docker.internal:5301)")]
    [InlineData("agent-update-failed", "Something new the agent said")]
    [InlineData("wrong-version", "an unexpected sentence")]
    [InlineData(null, null)]
    public void FreeText_IsShownAsItCame(string? code, string? diagnostic)
    {
        Assert.Equal(diagnostic, AgentUpdateDiagnosticText.Localize(code, diagnostic, L));
    }

    // Guard: every fixed diagnostic the agent's self-update reports has a translation, so a new one cannot reach
    // the screen in English unnoticed.
    [Fact]
    public void EveryFixedAgentDiagnostic_IsTranslated()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryScan.Root,
            "src", "Aetheus.Agent.Core", "Operations", "AgentSelfUpdateOperationExecutor.cs"));
        var literals = Regex.Matches(source, @"ReportPhaseAsync\(AgentUpdatePhase\.Failed, \d+, ""(?<text>[^""]+)""")
            .Select(m => m.Groups["text"].Value)
            .Concat(Regex.Matches(source, @"const string diagnostic = ""(?<text>[^""]+)""").Select(m => m.Groups["text"].Value))
            .Distinct()
            .ToList();

        Assert.True(literals.Count >= 7, $"expected the agent's fixed diagnostics, found {literals.Count}");
        Assert.All(literals, text =>
            Assert.StartsWith("AgentUpdateDiag_", AgentUpdateDiagnosticText.Localize(null, text, L)));
    }

    // Returns "key|arg1|arg2" so a test sees which values were passed to the sentence.
    private sealed class FormatRecordingLocalizer : IStringLocalizer<AppStrings>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Join('|', new[] { name }.Concat(arguments.Select(a => a.ToString()!))));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
