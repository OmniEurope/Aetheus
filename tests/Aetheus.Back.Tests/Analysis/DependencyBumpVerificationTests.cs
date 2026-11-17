// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// The last line of defence of a text edit (PLAN-004, lot 4, points 6 to 8): the result is parsed and compared with the original, and
/// refused unless it differs in exactly the values asked for. Normal edits never trip it, so these tests hand it an edit whose
/// position and meaning disagree, which is what a bug in locating a value would produce.
/// </summary>
public sealed class DependencyBumpVerificationTests
{
    [Fact]
    public void AJsonEditWhosePositionBelongsToAnotherPackage_IsRefusedByTheComparison()
    {
        const string text = "{\"dependencies\":{\"a\":\"1.0.0\",\"b\":\"1.0.0\"}}";
        var read = JsonManifest.Read(text, ["dependencies"])!;
        var a = read.Declarations.Single(declaration => declaration.Name == "a");
        var b = read.Declarations.Single(declaration => declaration.Name == "b");

        // The text at b's position is "1.0.0", like a's, so the position check passes; the document then differs at b, not a.
        var edited = JsonManifest.Replace(text, [(a with { Start = b.Start }, "2.0.0")], out var error);

        Assert.Null(edited);
        Assert.Contains("more than the version values", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AJsonEditAtTheRightPlace_PassesTheComparison()
    {
        const string text = "{\"dependencies\":{\"a\":\"1.0.0\",\"b\":\"1.0.0\"}}";
        var a = JsonManifest.Read(text, ["dependencies"])!.Declarations.Single(declaration => declaration.Name == "a");

        var edited = JsonManifest.Replace(text, [(a, "2.0.0")], out var error);

        Assert.Null(error);
        Assert.Equal("{\"dependencies\":{\"a\":\"2.0.0\",\"b\":\"1.0.0\"}}", edited);
    }

    [Fact]
    public void AJsonEditWhoseTextIsNotWhereItWasRead_IsRefused()
    {
        const string text = "{\"dependencies\":{\"a\":\"1.0.0\"}}";
        var a = JsonManifest.Read(text, ["dependencies"])!.Declarations.Single();

        var edited = JsonManifest.Replace("{\"dependencies\":{\"a\":\"9.9.9\"}}", [(a, "2.0.0")], out var error);

        Assert.Null(edited);
        Assert.Contains("does not hold the value", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.0.0\"")]
    [InlineData("2.0.0\\u0022")]
    [InlineData("2.0.0\n")]
    public void AJsonValueThatWouldNeedEscaping_IsNeverWritten(string unsafeValue)
    {
        const string text = "{\"dependencies\":{\"a\":\"1.0.0\"}}";
        var a = JsonManifest.Read(text, ["dependencies"])!.Declarations.Single();

        Assert.Null(JsonManifest.Replace(text, [(a, unsafeValue)], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void AnXmlEditWhosePositionBelongsToAnotherElement_IsRefusedByTheComparison()
    {
        const string text = "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\" /><PackageReference Include=\"B\" Version=\"1.0.0\" /></ItemGroup></Project>";
        var a = MsBuildDocument.ReadVersions(text, "A")!.Single();
        var b = MsBuildDocument.ReadVersions(text, "B")!.Single();

        var edited = MsBuildDocument.Replace(text, [a with { Start = b.Start }], "2.0.0", out var error);

        Assert.Null(edited);
        Assert.Contains("more than the version values", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnXmlEditAtTheRightPlace_PassesTheComparison()
    {
        const string text = "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\" /><PackageReference Include=\"B\" Version=\"1.0.0\" /></ItemGroup></Project>";
        var a = MsBuildDocument.ReadVersions(text, "A")!.Single();

        var edited = MsBuildDocument.Replace(text, [a], "2.0.0", out var error);

        Assert.Null(error);
        Assert.Equal(text.Replace("Include=\"A\" Version=\"1.0.0\"", "Include=\"A\" Version=\"2.0.0\"", StringComparison.Ordinal), edited);
    }

    [Theory]
    [InlineData("2.0.0\"")]
    [InlineData("2.0.0<")]
    [InlineData("2.0.0&amp;")]
    public void AnXmlValueThatWouldNeedEscaping_IsNeverWritten(string unsafeValue)
    {
        const string text = "<Project><PackageReference Include=\"A\" Version=\"1.0.0\" /></Project>";
        var a = MsBuildDocument.ReadVersions(text, "A")!.Single();

        Assert.Null(MsBuildDocument.Replace(text, [a], unsafeValue, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void AnXmlDocumentWithADoctype_IsRefusedNotExpanded()
    {
        const string text = "<!DOCTYPE Project [<!ENTITY v \"1.0.0\">]><Project><PackageReference Include=\"A\" Version=\"&v;\" /></Project>";

        Assert.Null(MsBuildDocument.ReadVersions(text, "A"));
    }
}
