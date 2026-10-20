// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

public class ServerQueryHelperTests
{
    [Theory]
    [InlineData("virtualserver_name=MyServer virtualserver_port=9987", "virtualserver_name", "MyServer")]
    [InlineData("virtualserver_port=9987 virtualserver_name=MyServer", "virtualserver_port", "9987")]
    [InlineData("no_equals_here", "virtualserver_name", "")]
    [InlineData("virtualserver_name=TailValue", "virtualserver_name", "TailValue")]
    public void GetValue_ReturnsExpected(string line, string key, string expected)
    {
        Assert.Equal(expected, ServerQueryHelper.GetValue(line, key));
    }

    [Theory]
    [InlineData("hello world", "hello\\sworld")]
    [InlineData("a/b", "a\\/b")]
    [InlineData("x|y", "x\\py")]
    [InlineData("line1\nline2", "line1\\nline2")]
    [InlineData("a\\b", "a\\\\b")]
    public void EscapeServerQuery_EscapesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, ServerQueryHelper.EscapeServerQuery(input));
    }

    [Theory]
    [InlineData("hello\\sworld", "hello world")]
    [InlineData("a\\/b", "a/b")]
    [InlineData("x\\py", "x|y")]
    [InlineData("line1\\nline2", "line1\nline2")]
    [InlineData("line1\\rline2", "line1\rline2")]
    public void UnescapeServerQuery_ReversesEscape(string input, string expected)
    {
        Assert.Equal(expected, ServerQueryHelper.UnescapeServerQuery(input));
    }

    [Fact]
    public void EscapeUnescape_RoundTrip_Preserves()
    {
        var original = "Name With / |pipe|\nnewline";
        var escaped = ServerQueryHelper.EscapeServerQuery(original);
        var back = ServerQueryHelper.UnescapeServerQuery(escaped);
        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData(@"literal\s-token")]
    [InlineData(@"literal\p-token")]
    [InlineData(@"literal\n-token")]
    [InlineData(@"literal\\s-token")]
    [InlineData(@"trailing\")]
    public void EscapeUnescape_RoundTrip_DecodesExactlyOnce(string original)
    {
        Assert.Equal(
            original,
            ServerQueryHelper.UnescapeServerQuery(ServerQueryHelper.EscapeServerQuery(original)));
    }

    [Fact]
    public void UnescapeServerQuery_PreservesUnknownEscape()
    {
        Assert.Equal(@"unknown\x", ServerQueryHelper.UnescapeServerQuery(@"unknown\x"));
    }
}
