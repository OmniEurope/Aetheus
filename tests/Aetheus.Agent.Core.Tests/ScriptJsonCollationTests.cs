// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The two pieces that make a function's JSON byte-identical to what the ported Node.js scripts wrote:
/// the key order of <c>localeCompare</c> (the delivery contract's verifier recomputes the candidate id
/// with it) and <c>JSON.stringify</c>'s layout. The expected orders were produced by Node.js 24.
/// </summary>
public sealed class ScriptJsonCollationTests
{
    [Fact]
    public void Collation_SortsPrintableAsciiAsNodeLocaleCompare()
    {
        // node -e 'a=[...printable ASCII]; a.sort((x,y)=>x.localeCompare(y))'
        const string node = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789aAbBcCdDeEfFgGhHiIjJkKlLmMnNoOpPqQrRsStTuUvVwWxXyYzZ";
        var characters = Enumerable.Range(0x20, 0x7f - 0x20).Select(code => ((char)code).ToString()).ToList();

        characters.Sort(ScriptCollation.Instance);

        Assert.Equal(node, string.Concat(characters));
    }

    [Fact]
    public void Collation_ComparesWholeStringsBeforeCase()
    {
        string[] words = ["b-", "Ab", "a1", "aB", "a.b", "A", "a_b", "b", "ab", "a-b", "a"];

        Array.Sort(words, ScriptCollation.Instance);

        // node: ["a-b","a_b","a.b","ab","aB","Ab","a1","a","A","b-","b"].sort((x,y)=>x.localeCompare(y))
        Assert.Equal(["a", "A", "a_b", "a-b", "a.b", "a1", "ab", "aB", "Ab", "b", "b-"], words);
    }

    [Fact]
    public void Collation_PutsAMixedCaseFileNameWhereNodeDoes()
    {
        string[] names = ["App_b.tar", "aetheus-front.tar.gz", "aetheus-back.tar.gz"];

        Array.Sort(names, ScriptCollation.Instance);

        Assert.Equal(["aetheus-back.tar.gz", "aetheus-front.tar.gz", "App_b.tar"], names);
    }

    [Fact]
    public void Json_CompactAndIndentedMatchJsonStringify()
    {
        var value = new JsonObject
        {
            ["version"] = "1.2.3+meta<&>'é",
            ["empty"] = new JsonArray(),
            ["none"] = new JsonObject(),
            ["list"] = new JsonArray("a", 2, true)
        };

        Assert.Equal("{\"version\":\"1.2.3+meta<&>'é\",\"empty\":[],\"none\":{},\"list\":[\"a\",2,true]}", ScriptJson.Compact(value));
        Assert.Equal(
            "{\n  \"version\": \"1.2.3+meta<&>'é\",\n  \"empty\": [],\n  \"none\": {},\n  \"list\": [\n    \"a\",\n    2,\n    true\n  ]\n}",
            ScriptJson.Indented(value));
    }
}
