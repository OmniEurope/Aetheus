// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A command's captured output is bounded so a runaway process cannot exhaust the agent's memory; the
/// cap is in bytes and the result says whether anything was dropped.</summary>
public class DefaultShellRunnerOutputTests
{
    private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);

    [Fact]
    public async Task ReadOutputAsync_WithoutALimit_ReturnsEverythingUntruncated()
    {
        var text = new string('x', 20_000);

        var output = await DefaultShellRunner.ReadOutputAsync(Reader(text), null, TestContext.Current.CancellationToken);

        Assert.Equal(text, output.Value);
        Assert.False(output.Truncated);
    }

    [Fact]
    public async Task ReadOutputAsync_OutputWithinTheLimit_IsKeptWhole()
    {
        var output = await DefaultShellRunner.ReadOutputAsync(Reader("hello"), 100, TestContext.Current.CancellationToken);

        Assert.Equal("hello", output.Value);
        Assert.False(output.Truncated);
    }

    [Fact]
    public async Task ReadOutputAsync_OutputOverTheLimit_IsCutAtTheLimitAndFlagged()
    {
        var output = await DefaultShellRunner.ReadOutputAsync(Reader("abcdefghij"), 4, TestContext.Current.CancellationToken);

        Assert.Equal("abcd", output.Value);
        Assert.True(output.Truncated);
    }

    [Fact]
    public async Task ReadOutputAsync_OutputSpanningSeveralReads_IsCutAtTheLimit()
    {
        var output = await DefaultShellRunner.ReadOutputAsync(
            Reader(new string('y', 12_000)), 5_000, TestContext.Current.CancellationToken);

        Assert.Equal(5_000, output.Value.Length);
        Assert.True(output.Truncated);
    }

    [Fact]
    public async Task ReadOutputAsync_ALimitOfZero_StillKeepsOneCharacter()
    {
        var output = await DefaultShellRunner.ReadOutputAsync(Reader("abc"), 0, TestContext.Current.CancellationToken);

        Assert.Equal("a", output.Value);
        Assert.True(output.Truncated);
    }

    [Fact]
    public async Task ReadOutputAsync_MultiByteCharactersAreBoundedInBytesNotCharacters()
    {
        // Three two-byte characters are three characters but six bytes: a 4-byte cap must cut the text.
        var output = await DefaultShellRunner.ReadOutputAsync(Reader("ééé"), 4, TestContext.Current.CancellationToken);

        Assert.True(output.Truncated);
        Assert.StartsWith("éé", output.Value, StringComparison.Ordinal);
        Assert.True(output.Value.Length <= 3);
    }
}
