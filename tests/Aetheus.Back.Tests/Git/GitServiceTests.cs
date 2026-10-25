// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class GitServiceTests
{
    private readonly GitCliService _sut;

    public GitServiceTests()
    {
        _sut = new GitCliService(Substitute.For<ILogger<GitCliService>>(), new Aetheus.Back.Components.Git.GitProcessRunner(Substitute.For<ILogger<Aetheus.Back.Components.Git.GitProcessRunner>>()));
    }

    private static List<(string BranchName, string Version)> InvokeParseOutput(string output)
    {
        var method = typeof(GitCliService).GetMethod("ParseOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<(string, string)>)method.Invoke(null, [output])!;
    }

    [Fact]
    public void ParseOutput_ValidOutput_ExtractsVersions()
    {
        var output = "abc123\trefs/heads/release/v1.0.0\ndef456\trefs/heads/release/v2.1.0\n";

        var result = InvokeParseOutput(output);

        Assert.Equal(2, result.Count);
        Assert.Equal(("release/v1.0.0", "1.0.0"), result[0]);
        Assert.Equal(("release/v2.1.0", "2.1.0"), result[1]);
    }

    [Fact]
    public void ParseOutput_EmptyOutput_ReturnsEmpty()
    {
        var result = InvokeParseOutput("");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseOutput_NullOutput_ReturnsEmpty()
    {
        var result = InvokeParseOutput(null!);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseOutput_WhitespaceOutput_ReturnsEmpty()
    {
        var result = InvokeParseOutput("   \n  ");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseOutput_NoReleaseBranches_ReturnsEmpty()
    {
        var output = "abc123\trefs/heads/main\ndef456\trefs/heads/feature/something\n";

        var result = InvokeParseOutput(output);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseOutput_MixedBranches_FiltersRelease()
    {
        var output = "abc123\trefs/heads/main\ndef456\trefs/heads/release/v3.0.0\nghi789\trefs/heads/develop\n";

        var result = InvokeParseOutput(output);

        Assert.Single(result);
        Assert.Equal("3.0.0", result[0].Version);
    }

    [Fact]
    public void ParseOutput_WithoutVPrefix_KeepsVersion()
    {
        var output = "abc123\trefs/heads/release/1.2.3\n";

        var result = InvokeParseOutput(output);

        Assert.Single(result);
        Assert.Equal("1.2.3", result[0].Version);
    }

    [Fact]
    public void ParseOutput_UppercaseVPrefix_Strips()
    {
        var output = "abc123\trefs/heads/release/V4.0.0\n";

        var result = InvokeParseOutput(output);

        Assert.Single(result);
        Assert.Equal("4.0.0", result[0].Version);
    }

    [Fact]
    public void ParseOutput_NoTabSeparator_SkipsLine()
    {
        var output = "abc123 refs/heads/release/v1.0.0\n";

        var result = InvokeParseOutput(output);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseOutput_MultipleBranches_AllExtracted()
    {
        var output = string.Join("\n",
            "aaa\trefs/heads/release/v1.0.0",
            "bbb\trefs/heads/release/v1.1.0",
            "ccc\trefs/heads/release/v2.0.0-beta",
            "");

        var result = InvokeParseOutput(output);

        Assert.Equal(3, result.Count);
        Assert.Equal("2.0.0-beta", result[2].Version);
    }

    [Fact]
    public async Task ListReleaseBranchesAsync_NullUrl_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.ListReleaseBranchesAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListReleaseBranchesAsync_EmptyUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.ListReleaseBranchesAsync("", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListReleaseBranchesAsync_WhitespaceUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.ListReleaseBranchesAsync("   ", ct: TestContext.Current.CancellationToken));
    }
}
