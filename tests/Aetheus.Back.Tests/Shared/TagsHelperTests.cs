// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests;

public class TagsHelperTests
{
    [Fact]
    public void DeserializeTags_ValidJson_ReturnsList()
    {
        var result = TagsHelper.DeserializeTags("[\"web\",\"prod\"]");
        Assert.Equal(2, result.Count);
        Assert.Contains("web", result);
        Assert.Contains("prod", result);
    }

    [Fact]
    public void DeserializeTags_EmptyString_ReturnsEmpty()
    {
        var result = TagsHelper.DeserializeTags("");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeTags_Null_ReturnsEmpty()
    {
        var result = TagsHelper.DeserializeTags(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeTags_Whitespace_ReturnsEmpty()
    {
        var result = TagsHelper.DeserializeTags("   ");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeTags_InvalidJson_ReturnsEmpty()
    {
        var result = TagsHelper.DeserializeTags("not json");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeTags_EmptyArray_ReturnsEmpty()
    {
        var result = TagsHelper.DeserializeTags("[]");
        Assert.Empty(result);
    }

    [Fact]
    public void SerializeTags_NoTagsOrOnlyBlankOnes_StoresAnEmptyColumn()
    {
        Assert.Equal(string.Empty, TagsHelper.SerializeTags(null));
        Assert.Equal(string.Empty, TagsHelper.SerializeTags([]));
        Assert.Equal(string.Empty, TagsHelper.SerializeTags(["", "  "]));
    }

    [Fact]
    public void SerializeTags_StoresAJsonArrayWithoutTheBlankOnes_ThatReadsBack()
    {
        var stored = TagsHelper.SerializeTags(["prod", " ", "web"]);

        Assert.Equal("""["prod","web"]""", stored);
        Assert.Equal(["prod", "web"], TagsHelper.DeserializeTags(stored));
    }
}
