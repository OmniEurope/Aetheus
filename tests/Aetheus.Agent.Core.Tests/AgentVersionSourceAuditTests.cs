// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The agent version has one source, <c>VersionPrefix</c> in <c>Directory.Build.props</c>, which every
/// build (Debug, Release, pipeline <c>-p:Version</c>) honors. A literal <c>Version</c> fallback in an agent project
/// froze local builds at an old number (1.2.0 while the backend announced 1.6.0): a local agent could never match
/// the version the backend asks for, so its update always looked pending and failed.</summary>
public sealed class AgentVersionSourceAuditTests
{
    private static readonly Regex VersionElement = new(
        @"<(Version|VersionPrefix|AssemblyVersion|FileVersion)\b", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static readonly string[] AgentProjects =
    [
        Path.Combine("src", "Aetheus.Agent.Core", "Aetheus.Agent.Core.csproj"),
        Path.Combine("src", "Aetheus.Agent.Linux", "Aetheus.Agent.Linux.csproj"),
        Path.Combine("src", "Aetheus.Agent.Windows", "Aetheus.Agent.Windows.csproj"),
    ];

    [Fact]
    public void AgentProjects_DeclareNoVersionOfTheirOwn()
    {
        foreach (var project in AgentProjects)
        {
            var path = Path.Combine(RepositoryScan.Root, project);
            Assert.True(File.Exists(path), $"{project} not found.");
            Assert.False(VersionElement.IsMatch(File.ReadAllText(path)), $"{project} declares its own version.");
        }
    }
}
