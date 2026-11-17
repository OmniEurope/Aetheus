// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>Where a package version is written in an MSBuild file.</summary>
/// <param name="ElementIndex">The position of the element among the descendants of the document, to find it again in another parse.</param>
/// <param name="Holder">The name of the attribute or child element that holds the version (<c>Version</c> or <c>VersionOverride</c>).</param>
/// <param name="IsAttribute">The version is an attribute of the element, not a child element.</param>
/// <param name="Value">The version as written, or null when it cannot be replaced in place (an entity, a CDATA section, markup inside).</param>
/// <param name="Start">Character offset of the first character of the value.</param>
internal sealed record MsBuildVersion(
    string ElementName, string PackageName, int ElementIndex, string Holder, bool IsAttribute, string? Value, int Start, int Line);

/// <summary>
/// Reads and changes the version of a package in an MSBuild file (<c>.csproj</c>, <c>Directory.Packages.props</c>) by editing its text:
/// comments, indentation, attribute order and line endings stay as written, which re-saving the document would not keep.
/// The edit is verified: the result is parsed and must equal the original with exactly the replaced values changed.
/// </summary>
internal static class MsBuildDocument
{
    private static readonly string[] PackageElements = ["PackageReference", "PackageVersion", "GlobalPackageReference"];
    private static readonly string[] VersionHolders = ["Version", "VersionOverride"];

    /// <summary>Every place the document writes a version for a package named <paramref name="packageName"/>. Null when the text is not XML.</summary>
    public static List<MsBuildVersion>? ReadVersions(string text, string packageName)
    {
        // Offsets count from the text without its byte order mark, which Replace takes off and puts back.
        if (text.StartsWith('﻿')) text = text[1..];
        XDocument document;
        try
        {
            document = Load(text, withLineInfo: true);
        }
        catch (XmlException)
        {
            return null;
        }

        var lineStarts = LineStarts(text);
        var found = new List<MsBuildVersion>();
        var elements = document.Descendants().ToList();
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (!PackageElements.Contains(element.Name.LocalName, StringComparer.Ordinal)) continue;
            var name = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (!string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase) || !HasLineInfo(element)) continue;
            var nameStart = OffsetOf(lineStarts, ((IXmlLineInfo)element).LineNumber, ((IXmlLineInfo)element).LinePosition);
            found.AddRange(VersionsOf(text, lineStarts, element, index, nameStart));
        }

        return found;
    }

    /// <summary>The text with each version replaced by <paramref name="newVersion"/>, or null (with the reason) when the result does not verify.</summary>
    public static string? Replace(string text, IReadOnlyList<MsBuildVersion> versions, string newVersion, out string? error)
    {
        error = null;
        if (newVersion.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_')))
        {
            error = "The new version holds characters that are not written safely in an XML value.";
            return null;
        }

        var byteOrderMark = text.StartsWith('﻿') ? "﻿" : string.Empty;
        text = text[byteOrderMark.Length..];
        var builder = new StringBuilder(text.Length + 16);
        var cursor = 0;
        foreach (var version in versions.OrderBy(version => version.Start))
        {
            if (version.Value is null || version.Start < cursor || string.CompareOrdinal(text, version.Start, version.Value, 0, version.Value.Length) != 0)
            {
                error = "The text of the file does not hold the version where it was read.";
                return null;
            }

            builder.Append(text, cursor, version.Start - cursor).Append(newVersion);
            cursor = version.Start + version.Value.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        var result = builder.ToString();
        error = Verify(text, result, versions, newVersion);
        return error is null ? byteOrderMark + result : null;
    }

    private static string? Verify(string original, string edited, IReadOnlyList<MsBuildVersion> versions, string newVersion)
    {
        try
        {
            var expected = Load(original, withLineInfo: false);
            var elements = expected.Descendants().ToList();
            foreach (var version in versions)
            {
                var element = elements[version.ElementIndex];
                if (version.IsAttribute) element.SetAttributeValue(version.Holder, newVersion);
                else element.Element(element.Name.Namespace + version.Holder)!.Value = newVersion;
            }

            return XNode.DeepEquals(expected, Load(edited, withLineInfo: false))
                ? null
                : "The edited document differs from the original by more than the version values asked for.";
        }
        catch (XmlException)
        {
            return "The edited document does not parse.";
        }
    }

    private static IEnumerable<MsBuildVersion> VersionsOf(string text, List<int> lineStarts, XElement element, int index, int nameStart)
    {
        var tag = StartTag.Scan(text, nameStart);
        foreach (var holder in VersionHolders)
        {
            if (element.Attribute(holder) is { } attribute)
                yield return AttributeVersion(text, lineStarts, element, index, nameStart, tag, attribute);
            if (element.Element(element.Name.Namespace + holder) is { } child && HasLineInfo(child))
                yield return ChildVersion(text, lineStarts, element, index, child);
        }
    }

    private static MsBuildVersion AttributeVersion(
        string text, List<int> lineStarts, XElement element, int index, int nameStart, StartTag? tag, XAttribute attribute)
    {
        var holder = attribute.Name.LocalName;
        var written = tag?.Attributes.FirstOrDefault(candidate => candidate.Name == holder);
        var usable = written is not null && !attribute.Value.Contains('&', StringComparison.Ordinal)
            && text.AsSpan(written.ValueStart, written.ValueLength).SequenceEqual(attribute.Value);
        return new MsBuildVersion(
            element.Name.LocalName, NameOf(element), index, holder, IsAttribute: true,
            usable ? attribute.Value : null, written?.ValueStart ?? nameStart, LineOf(lineStarts, written?.ValueStart ?? nameStart));
    }

    private static MsBuildVersion ChildVersion(string text, List<int> lineStarts, XElement element, int index, XElement child)
    {
        var childStart = OffsetOf(lineStarts, ((IXmlLineInfo)child).LineNumber, ((IXmlLineInfo)child).LinePosition);
        var textStart = StartTag.Scan(text, childStart) is { SelfClosing: false } childTag ? childTag.End + 1 : -1;
        var simple = textStart >= 0 && child.Nodes().Count() == 1 && child.FirstNode is XText { } node and not XCData
            && !node.Value.Contains('&', StringComparison.Ordinal)
            && textStart + node.Value.Length <= text.Length
            && text.AsSpan(textStart, node.Value.Length).SequenceEqual(node.Value);
        return new MsBuildVersion(
            element.Name.LocalName, NameOf(element), index, child.Name.LocalName, IsAttribute: false,
            simple ? child.Value : null, Math.Max(textStart, childStart), LineOf(lineStarts, Math.Max(textStart, childStart)));
    }

    private static bool HasLineInfo(XObject node) => node is IXmlLineInfo info && info.HasLineInfo();

    private static string NameOf(XElement element) => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty;

    private static XDocument Load(string text, bool withLineInfo)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | (withLineInfo ? LoadOptions.SetLineInfo : LoadOptions.None));
    }

    /// <summary>The offset of the first character of each line, lines ending at <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c> as XML counts them.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) starts.Add(i + 1);
        }

        return starts;
    }

    private static int OffsetOf(List<int> lineStarts, int line, int position) =>
        line >= 1 && line <= lineStarts.Count ? lineStarts[line - 1] + position - 1 : -1;

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var line = lineStarts.BinarySearch(offset);
        return line >= 0 ? line + 1 : ~line;
    }

    /// <summary>One attribute of a start tag as it is written.</summary>
    private sealed record WrittenAttribute(string Name, int ValueStart, int ValueLength);

    /// <summary>The attributes of one start tag and where the tag ends, read from the text: the XML parser gives values, not where they sit.</summary>
    private sealed record StartTag(IReadOnlyList<WrittenAttribute> Attributes, int End, bool SelfClosing)
    {
        /// <summary>Scans the tag whose name begins at <paramref name="nameStart"/>. Null when the text is not a well-formed tag there.</summary>
        public static StartTag? Scan(string text, int nameStart)
        {
            if (nameStart < 1 || nameStart >= text.Length || text[nameStart - 1] != '<') return null;
            var attributes = new List<WrittenAttribute>();
            var i = nameStart;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '/')) i++;
            while (true)
            {
                i = SkipWhitespace(text, i);
                if (i >= text.Length) return null;
                if (text[i] == '>') return new StartTag(attributes, i, text[i - 1] == '/');
                if (text[i] == '/')
                {
                    i++;
                    continue;
                }

                if (ReadAttribute(text, ref i) is not { } attribute) return null;
                attributes.Add(attribute);
            }
        }

        private static int SkipWhitespace(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        /// <summary>Reads <c>name = "value"</c> from <paramref name="i"/> and leaves it after the closing quote. Null when it is not written that way.</summary>
        private static WrittenAttribute? ReadAttribute(string text, ref int i)
        {
            var attributeStart = i;
            while (i < text.Length && text[i] is not ('=' or '>') && !char.IsWhiteSpace(text[i])) i++;
            var name = text[attributeStart..i];
            i = SkipWhitespace(text, i);
            if (i >= text.Length || text[i] != '=') return null;
            i = SkipWhitespace(text, i + 1);
            if (i >= text.Length || text[i] is not ('"' or '\'')) return null;
            var close = text.IndexOf(text[i], i + 1);
            if (close < 0) return null;
            var attribute = new WrittenAttribute(name, i + 1, close - i - 1);
            i = close + 1;
            return attribute;
        }
    }
}
