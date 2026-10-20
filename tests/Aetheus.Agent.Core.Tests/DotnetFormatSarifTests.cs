// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/convert-dotnet-format-report.mjs</c> and the scope rules of
/// <c>run-affected-dotnet-format.sh</c>, on literal inputs: the SARIF the helper wrote, byte for byte,
/// its defaults and refusals, and which <c>git diff --name-status</c> listings narrow the check.
/// </summary>
public sealed class DotnetFormatSarifTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "aetheus-format-ws");

    [Fact]
    public void Finding_IsWrittenExactlyAsTheHelperWroteIt()
    {
        const string report =
            "[{\"FilePath\":\"src/A.cs\",\"FileChanges\":[{\"LineNumber\":3,\"CharNumber\":5,\"DiagnosticId\":\"WHITESPACE\",\"FormatDescription\":\"Fix whitespace formatting.\"}]}]";

        var (sarif, count) = DotnetFormatSarif.Convert(report, Workspace);

        Assert.Equal(1, count);
        Assert.Equal(
            "{\"version\":\"2.1.0\",\"$schema\":\"https://json.schemastore.org/sarif-2.1.0.json\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"dotnet format\",\"version\":\"1\"}},\"results\":["
            + "{\"ruleId\":\"WHITESPACE\",\"level\":\"note\",\"partialFingerprints\":{\"dotnetFormatFile/v1\":\"src/A.cs\"},"
            + "\"message\":{\"text\":\"Fix whitespace formatting.\"},\"locations\":[{\"physicalLocation\":{\"artifactLocation\":{\"uri\":\"src/A.cs\"},"
            + "\"region\":{\"startLine\":3,\"startColumn\":5}}}]}]}]}\n",
            sarif);
    }

    [Fact]
    public void EmptyReport_IsAnEmptyRun()
    {
        var (sarif, count) = DotnetFormatSarif.Convert("[]\n", Workspace);

        Assert.Equal(0, count);
        Assert.EndsWith("\"results\":[]}]}\n", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsolutePath_IsMadeRelativeToTheWorkingDirectory()
    {
        var file = Path.Combine(Workspace, "src", "Deep", "B.cs");
        var report = "[{\"FilePath\":" + System.Text.Json.JsonSerializer.Serialize(file) + ",\"FileChanges\":[{}]}]";

        var (sarif, _) = DotnetFormatSarif.Convert(report, Workspace);

        Assert.Contains("\"uri\":\"src/Deep/B.cs\"", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public void FileName_IsUsedWhenFilePathIsAbsentOrNull()
    {
        var (sarif, _) = DotnetFormatSarif.Convert("[{\"FilePath\":null,\"FileName\":\"C.cs\",\"FileChanges\":[{}]}]", Workspace);

        Assert.Contains("\"uri\":\"C.cs\"", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFields_TakeTheHelpersDefaults()
    {
        var (sarif, _) = DotnetFormatSarif.Convert("[{\"FileName\":\"C.cs\",\"FileChanges\":[{\"LineNumber\":0,\"CharNumber\":\"7\"}]}]", Workspace);

        Assert.Contains("\"ruleId\":\"DOTNET_FORMAT\"", sarif, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"dotnet format would change this source file.\"", sarif, StringComparison.Ordinal);
        Assert.Contains("\"region\":{\"startLine\":1,\"startColumn\":7}", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericDiagnosticId_IsWrittenAsText()
    {
        var (sarif, _) = DotnetFormatSarif.Convert("[{\"FileName\":\"C.cs\",\"FileChanges\":[{\"DiagnosticId\":42,\"LineNumber\":-4}]}]", Workspace);

        Assert.Contains("\"ruleId\":\"42\"", sarif, StringComparison.Ordinal);
        Assert.Contains("\"startLine\":1", sarif, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", "The dotnet format report must be a JSON array.")]
    [InlineData("[1]", "The dotnet format report contains an invalid document.")]
    [InlineData("[{\"FileName\":\"C.cs\"}]", "The dotnet format report contains an invalid document.")]
    [InlineData("[{\"FileChanges\":[]}]", "A dotnet format finding has no file name.")]
    [InlineData("[{\"FileName\":\"\",\"FileChanges\":[]}]", "A dotnet format finding has no file name.")]
    [InlineData("[{\"FileName\":\"C.cs\",\"FileChanges\":[null]}]", "The dotnet format report contains an invalid finding.")]
    [InlineData("[{\"FileName\":\"C.cs\",\"FileChanges\":[\"x\"]}]", "The dotnet format report contains an invalid finding.")]
    public void InvalidReports_AreRefusedWithTheHelpersMessages(string report, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => DotnetFormatSarif.Convert(report, Workspace));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void NonJsonReport_IsRefusedWithAPlainCause()
    {
        var error = Assert.Throws<InvalidDataException>(() => DotnetFormatSarif.Convert("not json", Workspace));

        Assert.StartsWith("The dotnet format report is not valid JSON:", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("M\tsrc/A.cs\nD\tsrc/B.cs")]
    [InlineData("R100\tsrc/A.cs\tsrc/B.cs")]
    [InlineData("C75\tsrc/A.cs\tsrc/B.cs")]
    [InlineData("A\tsrc/With Space.cs")]
    [InlineData("M\t.editorconfig")]
    [InlineData("M\tsrc/.globalconfig")]
    [InlineData("M\tDirectory.Build.props")] // the script missed these two (see AffectedFormatScope)
    [InlineData("M\tsrc/Directory.Build.targets")]
    [InlineData("M\tDirectory.Packages.props")]
    [InlineData("M\tApp.slnx")]
    [InlineData("M\tApp.sln")]
    [InlineData("M\tsrc/App/App.csproj")]
    public void UnsafeListings_CheckTheWholeSolution(string changes) =>
        Assert.True(AffectedFormatScope.FromChanges(changes).Full);

    [Fact]
    public void AddedAndModifiedSources_AreTheScope()
    {
        var scope = AffectedFormatScope.FromChanges("A\tsrc/A.cs\nM\tsrc/B.razor\nM\tsrc/C.razor.css\nT\tsrc/D.cs\nM\tdocs/E.md");

        Assert.False(scope.Full);
        Assert.Equal(["src/A.cs", "src/B.razor"], scope.Files);
    }

    [Fact]
    public void EmptyListing_HasNoFile()
    {
        var scope = AffectedFormatScope.FromChanges(string.Empty);

        Assert.False(scope.Full);
        Assert.Empty(scope.Files);
    }
}
